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
    // Биграммы компактно: продолжения слова a — _biNext[_biStart[a].._biStart[a+1]] (по возрастанию id)
    private int[] _biStart = [];
    private int[] _biNext = [];
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
    private int[] _wordSuf = [];
    private Dictionary<string, int> _classes2 = [];   // класс слова через одно → окончание кандидата
    private long[] _sufKeys2 = [];
    private int[] _sufCnt2 = [];
    private float[] _classTotal2 = [];      // id окончания (2 последние буквы) каждого слова словаря, -1 — нет

    // Профили окончаний после конкретного слова / пары слов (по их продолжениям в корпусе) — кеш
    private readonly Dictionary<long, (Dictionary<int, float> Counts, float Total)> _profiles = [];
    private readonly object _profilesLock = new();

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
        if (version is not (1 or 2)) throw new InvalidDataException($"Неизвестная версия модели {version}");
        var lang = br.ReadString();
        int n = br.ReadInt32();
        long total = br.ReadInt64();
        // Идентификаторы слов упакованы по 20 бит — больше 2^20 слов модель не поддерживает
        if (n <= 0 || n > (1 << Shift) || total <= 0) throw new InvalidDataException("Повреждённая модель: размер словаря");

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

        int formCount = Count(br, n);
        var forms = new Dictionary<int, string>(formCount);
        for (int i = 0; i < formCount; i++)
        {
            int id = br.ReadInt32();
            var f = br.ReadString();
            if ((uint)id < (uint)n) forms[id] = f;
        }

        var ctx = new float[n];
        for (int i = 0; i < n; i++) ctx[i] = br.ReadInt32();

        static int Count(BinaryReader r, int max)
        {
            int v = r.ReadInt32();
            if (v < 0 || v > max) throw new InvalidDataException("Повреждённая модель: счётчик вне диапазона");
            return v;
        }

        int nb = Count(br, 50_000_000);
        var biStart = new int[n + 1];
        var biNext = new int[nb];
        long prevKey = -1;
        for (int i = 0; i < nb; i++)
        {
            long k = br.ReadInt64();
            int a = (int)(k >> Shift), b = (int)(k & Mask);
            if (k <= prevKey || (uint)a >= (uint)n || (uint)b >= (uint)n) throw new InvalidDataException("Повреждённая модель: биграммы");
            prevKey = k;
            biNext[i] = b;
            biStart[a + 1]++;
        }
        for (int i = 0; i < n; i++) biStart[i + 1] += biStart[i];
        var bc = new ushort[nb];
        for (int i = 0; i < nb; i++) bc[i] = br.ReadUInt16();

        int nt = Count(br, 50_000_000);
        var tk = new long[nt];
        for (int i = 0; i < nt; i++) tk[i] = br.ReadInt64();
        var tc = new ushort[nt];
        for (int i = 0; i < nt; i++) tc[i] = br.ReadUInt16();

        int ncls = Count(br, 1 << Shift);
        var classes = new Dictionary<string, int>(ncls, StringComparer.Ordinal);
        for (int i = 0; i < ncls; i++) classes[br.ReadString()] = i;
        int nsuf = Count(br, 1 << Shift);
        var suffixes = new Dictionary<string, int>(nsuf, StringComparer.Ordinal);
        var sufCounts = new long[nsuf];
        for (int i = 0; i < nsuf; i++)
        {
            suffixes[br.ReadString()] = i;
            sufCounts[i] = br.ReadInt64();
        }
        double sufTotal = Math.Max(1, sufCounts.Sum());
        var sufProb = sufCounts.Select(c => (float)(c / sufTotal)).ToArray();
        int ne = Count(br, 50_000_000);
        var sk = new long[ne];
        var sc = new int[ne];
        var classTotal = new float[ncls];
        for (int i = 0; i < ne; i++)
        {
            sk[i] = br.ReadInt64();
            sc[i] = br.ReadInt32();
            int cls = (int)(sk[i] >> Shift);
            if ((uint)cls >= (uint)ncls || (sk[i] & Mask) >= nsuf || (i > 0 && sk[i] < sk[i - 1]))
                throw new InvalidDataException("Повреждённая модель: таблица окончаний");
            classTotal[cls] += sc[i];
        }

        // Версия 2: окончания через слово
        var classes2 = new Dictionary<string, int>(StringComparer.Ordinal);
        long[] sk2 = [];
        int[] sc2 = [];
        var classTotal2 = Array.Empty<float>();
        if (version >= 2)
        {
            int nc2 = Count(br, 1 << Shift);
            for (int i = 0; i < nc2; i++) classes2[br.ReadString()] = i;
            int ne2 = Count(br, 50_000_000);
            sk2 = new long[ne2];
            sc2 = new int[ne2];
            classTotal2 = new float[nc2];
            for (int i = 0; i < ne2; i++)
            {
                sk2[i] = br.ReadInt64();
                sc2[i] = br.ReadInt32();
                int cls = (int)(sk2[i] >> Shift);
                if ((uint)cls >= (uint)nc2 || (sk2[i] & Mask) >= nsuf || (i > 0 && sk2[i] < sk2[i - 1]))
                    throw new InvalidDataException("Повреждённая модель: таблица окончаний через слово");
                classTotal2[cls] += sc2[i];
            }
        }

        var m = new LanguageModel
        {
            _classes2 = classes2, _sufKeys2 = sk2, _sufCnt2 = sc2, _classTotal2 = classTotal2,
            Language = lang, Count = n, _blob = sb.ToString(), _offsets = offsets, _uni = uni, _forms = forms, _ctxTotal = ctx,
            _biStart = biStart, _biNext = biNext, _biCnt = bc, _triKeys = tk, _triCnt = tc,
            _classes = classes, _suffixes = suffixes, _sufProb = sufProb, _sufKeys = sk, _sufCnt = sc, _classTotal = classTotal,
        };
        m._wordSuf = new int[n];
        for (int i = 0; i < n; i++)
            m._wordSuf[i] = suffixes.TryGetValue(NextSuffix(m.KeySpan(i)), out var sid) ? sid : -1;
        return m;
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
        int i = Array.BinarySearch(_biNext, _biStart[a], _biStart[a + 1] - _biStart[a], b);
        return i >= 0 ? _biCnt[i] : 0;
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
        for (int i = _biStart[a], end = _biStart[a + 1]; i < end; i++)
            yield return (_biNext[i], _biCnt[i]);
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
    private static string NextSuffix(ReadOnlySpan<char> w) => (w.Length <= 2 ? w : w[^2..]).ToString();

    /// <summary>
    /// Насколько окончание слова-кандидата типично после предыдущего слова (>1 — типичнее среднего).
    /// Так «этом» → «месте», «с» → «коллегами», «для» → «работы».
    /// </summary>
    /// <summary>
    /// Типичность окончания кандидата после <b>этих</b> слов: по тому, какие окончания у их продолжений в корпусе
    /// («согласно» → «-у/-ю», «в соответствии с» → «-ми/-ом»). Работает и для слов, которых нет среди продолжений;
    /// редкий контекст сглаживается моделью классов (<see cref="SuffixLift"/>).
    /// </summary>
    /// <param name="i2">Слово перед предыдущим (или −1).</param>
    public double ContextSuffixLift(int i1, int i2, string prevKey, string candKey)
    {
        double classLift = SuffixLift(prevKey, candKey);
        if (i1 < 0 || !_suffixes.TryGetValue(NextSuffix(candKey), out var suf)) return classLift;
        var (counts, total) = Profile(i2 >= 0 && Bigram(i2, i1) >= 8 ? ((long)i2 << Shift) | (uint)i1 | (1L << 62) : i1, i1, i2);
        if (total < 3) return classLift;
        // Сглаживание: профиль контекста + α «виртуальных» наблюдений из модели классов
        const double alpha = 12;
        double marg = Math.Max(_sufProb[suf], 1e-6);
        double p = (counts.GetValueOrDefault(suf) + alpha * classLift * marg) / (total + alpha);
        return Math.Clamp(p / marg, 0.03, 30);
    }

    private (Dictionary<int, float> Counts, float Total) Profile(long cacheKey, int i1, int i2)
    {
        lock (_profilesLock)
            if (_profiles.TryGetValue(cacheKey, out var hit)) return hit;
        var counts = new Dictionary<int, float>();
        float total = 0;
        var followers = (cacheKey & (1L << 62)) != 0 ? Followers(i2, i1) : Followers(i1);
        foreach (var (id, cnt) in followers)
        {
            int s = _wordSuf[id];
            if (s < 0) continue;
            counts[s] = counts.GetValueOrDefault(s) + cnt;
            total += cnt;
        }
        lock (_profilesLock)
        {
            if (_profiles.Count > 512) _profiles.Clear();
            _profiles[cacheKey] = (counts, total);
        }
        return (counts, total);
    }

    /// <summary>Типичность окончания кандидата по слову через одно («поиском и возможност<b>ью</b>»); 1 — нет данных.</summary>
    public double SkipSuffixLift(string prev2Key, string candKey)
    {
        if (!_classes2.TryGetValue(PrevClass(prev2Key), out var cls)) return 1;
        if (!_suffixes.TryGetValue(NextSuffix(candKey), out var suf)) return 1;
        double total = _classTotal2[cls];
        if (total < 50) return 1;
        long k = ((long)cls << Shift) | (uint)suf;
        int i = Lower(_sufKeys2, k);
        int c = i < _sufKeys2.Length && _sufKeys2[i] == k ? _sufCnt2[i] : 0;
        double p = (c + 0.5) / (total + 0.5 * _suffixes.Count);
        return Math.Clamp(p / Math.Max(_sufProb[suf], 1e-6), 0.05, 20);
    }

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
