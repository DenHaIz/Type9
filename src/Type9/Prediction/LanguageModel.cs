using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Type9.Prediction;

/// <summary>
/// Встроенная языковая модель (.t9lm, см. tools/DictBuilder): словарь с частотами, биграммы, триграммы
/// и статистика переходов окончаний. Хранится в плоских массивах — ~10–15 МБ на язык без миллионов мелких объектов.
/// Ключи слов нормализованы (нижний регистр, ё→е) и отсортированы, идентификатор слова = индекс.
/// </summary>
public sealed class LanguageModel
{
    private const int Shift = 20;
    private const long Mask = (1L << Shift) - 1;

    public string Language { get; private init; } = "";
    public int Count { get; private init; }

    private string _blob = "";       // все ключи подряд
    private int[] _offsets = [];      // начало ключа i в _blob (Count + 1 элементов)
    private float[] _uni = [];        // вероятность слова
    private float[] _ctxTotal = [];   // сколько раз слово было контекстом (знаменатель для биграмм)
    private Dictionary<int, string> _forms = [];
    private long[] _biKeys = [];
    private ushort[] _biCnt = [];
    private long[] _triKeys = [];
    private ushort[] _triCnt = [];

    // Модель окончаний: класс предыдущего слова (≤3 последних буквы) → 2 последние буквы следующего
    private Dictionary<string, int> _classes = [];
    private Dictionary<string, int> _suffixes = [];
    private float[] _sufProb = [];
    private long[] _sufKeys = [];
    private int[] _sufCnt = [];
    private float[] _classTotal = [];

    public static LanguageModel? LoadEmbedded(string lang)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Dict.{lang}.t9lm", StringComparison.Ordinal));
        if (name is null) return null;
        using var s = asm.GetManifestResourceStream(name)!;
        return Load(s);
    }

    public static LanguageModel Load(Stream stream)
    {
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var br = new BinaryReader(new BufferedStream(gz, 1 << 16), Encoding.UTF8);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "T9LM") throw new InvalidDataException("Не файл модели Type9");
        int version = br.ReadInt32();
        if (version != 1) throw new InvalidDataException($"Неизвестная версия модели {version}");
        var lang = br.ReadString();
        int n = br.ReadInt32();
        long total = br.ReadInt64();

        var sb = new StringBuilder(n * 9);
        var offsets = new int[n + 1];
        for (int i = 0; i < n; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(br.ReadString());
        }
        offsets[n] = sb.Length;

        var uni = new float[n];
        for (int i = 0; i < n; i++) uni[i] = (float)(br.ReadInt64() / (double)total);

        int formCount = br.ReadInt32();
        var forms = new Dictionary<int, string>(formCount);
        for (int i = 0; i < formCount; i++) forms[br.ReadInt32()] = br.ReadString();

        var ctx = new float[n];
        for (int i = 0; i < n; i++) ctx[i] = br.ReadInt32();

        int nb = br.ReadInt32();
        var bk = new long[nb];
        for (int i = 0; i < nb; i++) bk[i] = br.ReadInt64();
        var bc = new ushort[nb];
        for (int i = 0; i < nb; i++) bc[i] = br.ReadUInt16();

        int nt = br.ReadInt32();
        var tk = new long[nt];
        for (int i = 0; i < nt; i++) tk[i] = br.ReadInt64();
        var tc = new ushort[nt];
        for (int i = 0; i < nt; i++) tc[i] = br.ReadUInt16();

        int ncls = br.ReadInt32();
        var classes = new Dictionary<string, int>(ncls, StringComparer.Ordinal);
        for (int i = 0; i < ncls; i++) classes[br.ReadString()] = i;
        int nsuf = br.ReadInt32();
        var suffixes = new Dictionary<string, int>(nsuf, StringComparer.Ordinal);
        var sufCounts = new long[nsuf];
        for (int i = 0; i < nsuf; i++)
        {
            suffixes[br.ReadString()] = i;
            sufCounts[i] = br.ReadInt64();
        }
        double sufTotal = Math.Max(1, sufCounts.Sum());
        var sufProb = sufCounts.Select(c => (float)(c / sufTotal)).ToArray();
        int ne = br.ReadInt32();
        var sk = new long[ne];
        var sc = new int[ne];
        var classTotal = new float[ncls];
        for (int i = 0; i < ne; i++)
        {
            sk[i] = br.ReadInt64();
            sc[i] = br.ReadInt32();
            classTotal[(int)(sk[i] >> Shift)] += sc[i];
        }

        return new LanguageModel
        {
            Language = lang, Count = n, _blob = sb.ToString(), _offsets = offsets, _uni = uni, _forms = forms, _ctxTotal = ctx,
            _biKeys = bk, _biCnt = bc, _triKeys = tk, _triCnt = tc,
            _classes = classes, _suffixes = suffixes, _sufProb = sufProb, _sufKeys = sk, _sufCnt = sc, _classTotal = classTotal,
        };
    }

    // ------------------------------------------------------------------ словарь

    public ReadOnlySpan<char> KeySpan(int id) => _blob.AsSpan(_offsets[id], _offsets[id + 1] - _offsets[id]);
    public string Key(int id) => KeySpan(id).ToString();
    public string Form(int id) => _forms.TryGetValue(id, out var f) ? f : Key(id);
    public float Unigram(int id) => _uni[id];
    public float ContextTotal(int id) => _ctxTotal[id];

    private int LowerBound(ReadOnlySpan<char> key)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (KeySpan(mid).SequenceCompareTo(key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public int Find(ReadOnlySpan<char> key)
    {
        int i = LowerBound(key);
        return i < Count && KeySpan(i).SequenceEqual(key) ? i : -1;
    }

    /// <summary>Диапазон [from, to) слов, начинающихся с префикса.</summary>
    public (int From, int To) PrefixRange(ReadOnlySpan<char> prefix)
    {
        int from = LowerBound(prefix);
        int to = from;
        while (to < Count && KeySpan(to).StartsWith(prefix)) to++;
        return (from, to);
    }

    // ------------------------------------------------------------------ n-граммы

    private static int Lower(long[] keys, long value)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (keys[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public int Bigram(int a, int b)
    {
        long k = ((long)a << Shift) | (uint)b;
        int i = Lower(_biKeys, k);
        return i < _biKeys.Length && _biKeys[i] == k ? _biCnt[i] : 0;
    }

    public int Trigram(int a, int b, int c)
    {
        long k = ((long)a << (2 * Shift)) | ((long)b << Shift) | (uint)c;
        int i = Lower(_triKeys, k);
        return i < _triKeys.Length && _triKeys[i] == k ? _triCnt[i] : 0;
    }

    /// <summary>Все продолжения слова a: (id, count).</summary>
    public IEnumerable<(int Id, int Count)> Followers(int a)
    {
        long start = (long)a << Shift, end = (long)(a + 1) << Shift;
        for (int i = Lower(_biKeys, start); i < _biKeys.Length && _biKeys[i] < end; i++)
            yield return ((int)(_biKeys[i] & Mask), _biCnt[i]);
    }

    /// <summary>Все продолжения пары (a, b).</summary>
    public IEnumerable<(int Id, int Count)> Followers(int a, int b)
    {
        long start = ((long)a << (2 * Shift)) | ((long)b << Shift), end = start + (1L << Shift);
        for (int i = Lower(_triKeys, start); i < _triKeys.Length && _triKeys[i] < end; i++)
            yield return ((int)(_triKeys[i] & Mask), _triCnt[i]);
    }

    // ------------------------------------------------------------------ окончания

    public static string PrevClass(string w) => w.Length <= 3 ? w : w[^3..];
    public static string NextSuffix(string w) => w.Length <= 2 ? w : w[^2..];

    /// <summary>
    /// Насколько окончание слова-кандидата типично после предыдущего слова (>1 — типичнее среднего).
    /// Так «этом» → «месте», «с» → «коллегами», «для» → «работы».
    /// </summary>
    public double SuffixLift(string prevKey, string candKey)
    {
        if (!_classes.TryGetValue(PrevClass(prevKey), out var cls)) return 1;
        if (!_suffixes.TryGetValue(NextSuffix(candKey), out var suf)) return 1;
        double total = _classTotal[cls];
        if (total < 50) return 1;
        long k = ((long)cls << Shift) | (uint)suf;
        int i = Lower(_sufKeys, k);
        int c = i < _sufKeys.Length && _sufKeys[i] == k ? _sufCnt[i] : 0;
        double p = (c + 0.5) / (total + 0.5 * _suffixes.Count);
        return Math.Clamp(p / Math.Max(_sufProb[suf], 1e-6), 0.05, 20);
    }
}
