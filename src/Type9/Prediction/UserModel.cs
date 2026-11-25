using System.Globalization;
using System.Text;
using Type9.Input;

namespace Type9.Prediction;

public sealed class WordStat
{
    public string Form = "";
    public int Count;
    public long LastUsed; // минуты с эпохи Unix
}

/// <summary>
/// Выученная модель пользователя: частоты слов, биграммы и триграммы.
/// Используется только из UI-потока (обучение туда маршалится из потока хуков).
/// </summary>
public sealed class UserModel
{
    private readonly Dictionary<string, WordStat> _words = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _index = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _bigrams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _trigrams = new(StringComparer.Ordinal);

    public bool Dirty { get; set; }
    public int WordCount => _words.Count;
    /// <summary>Сумма употреблений всех слов (знаменатель для частоты).</summary>
    public long TotalCount { get; private set; }
    public int Version { get; private set; }

    public static long NowMinutes() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;

    /// <summary>Словарная форма слова по ключу (языковая модель, деловой словарь) — чтобы не запоминать капс.</summary>
    public Func<string, string?>? KnownForm { get; set; }

    public static bool IsLearnable(string word) =>
        word.Length >= 2 && word.Length <= 40 && word.Any(char.IsLetter) && !word.Any(char.IsDigit);

    public void Learn(CompletedWord w, long now)
    {
        if (!w.Learnable || !IsLearnable(w.Word)) return;
        var key = CaseHelper.Normalize(w.Word);
        if (w.Tentative && !_words.ContainsKey(key) && KnownForm?.Invoke(key) is null) return;

        if (!_words.TryGetValue(key, out var st))
        {
            st = new WordStat { Form = PickForm(w.Word, w.SentenceStart, null, KnownForm) };
            _words[key] = st;
            _index.Add(key);
        }
        else
        {
            st.Form = PickForm(w.Word, w.SentenceStart, st.Form, KnownForm);
        }
        st.Count++;
        TotalCount++;
        st.LastUsed = now;

        if (w.Prev1 is { } p1 && IsLearnable(p1))
        {
            var k1 = CaseHelper.Normalize(p1);
            Inc(_bigrams, k1, key);
            if (w.Prev2 is { } p2 && IsLearnable(p2))
                Inc(_trigrams, CaseHelper.Normalize(p2) + " " + k1, key);
        }
        Dirty = true;
        Version++;
    }

    /// <summary>
    /// Форма для показа: аббревиатуры и имена собственные сохраняем как есть, остальное — строчными.
    /// Случайный капс («ЧЕРЕЗ», «дА») сначала приводится к обычному виду.
    /// </summary>
    internal static string PickForm(string word, bool sentenceStart, string? existing, Func<string, string?>? knownForm = null)
    {
        word = CaseHelper.CleanCase(word, knownForm);
        if (existing is { Length: 0 }) existing = null;
        if (existing is not null) existing = CaseHelper.CleanCase(existing, knownForm);
        bool innerUpper = word.Skip(1).Any(char.IsUpper);
        if (innerUpper) return word;                          // ООО, iPhone
        if (char.IsUpper(word[0]))
            return sentenceStart ? existing ?? word.ToLowerInvariant() : word; // Москва в середине предложения
        return existing is not null && char.IsUpper(existing[0]) && !existing.Skip(1).Any(char.IsUpper)
            ? word // слово встретилось строчным — значит, это не имя собственное
            : existing ?? word;
    }

    /// <summary>Чинит формы, сохранённые прежними версиями капсом («ЧЕРЕЗ» → «через»). Возвращает число исправленных.</summary>
    public int FixForms()
    {
        int n = 0;
        foreach (var st in _words.Values)
        {
            var f = CaseHelper.CleanCase(st.Form, KnownForm);
            if (f == st.Form) continue;
            st.Form = f;
            n++;
        }
        if (n > 0) { Dirty = true; Version++; }
        return n;
    }

    private static void Inc(Dictionary<string, Dictionary<string, int>> map, string ctx, string next, int by = 1)
    {
        if (!map.TryGetValue(ctx, out var inner))
            map[ctx] = inner = new Dictionary<string, int>(StringComparer.Ordinal);
        inner[next] = inner.GetValueOrDefault(next) + by;

        // Ограничиваем рост: держим не более 64 продолжений на контекст
        if (inner.Count > 64)
        {
            var weakest = inner.MinBy(kv => kv.Value).Key;
            inner.Remove(weakest);
        }
    }

    public IEnumerable<(string Key, WordStat Stat)> ByPrefix(string keyPrefix)
    {
        if (_index.Count == 0) return [];
        var view = _index.GetViewBetween(keyPrefix, keyPrefix + '￿');
        return view.Select(k => (k, _words[k]));
    }

    public WordStat? Get(string key) => _words.GetValueOrDefault(key);

    public int Bigram(string? p1Key, string key) =>
        p1Key is not null && _bigrams.TryGetValue(p1Key, out var m) ? m.GetValueOrDefault(key) : 0;

    public int Trigram(string? p2Key, string? p1Key, string key) =>
        p2Key is not null && p1Key is not null && _trigrams.TryGetValue(p2Key + " " + p1Key, out var m)
            ? m.GetValueOrDefault(key) : 0;

    public double ContextTotal(string p1Key) =>
        _bigrams.TryGetValue(p1Key, out var m) ? m.Values.Sum() : 0;

    public double ContextTotal(string p2Key, string p1Key) =>
        _trigrams.TryGetValue(p2Key + " " + p1Key, out var m) ? m.Values.Sum() : 0;

    public IEnumerable<KeyValuePair<string, int>> Followers(string p1Key) =>
        _bigrams.TryGetValue(p1Key, out var m) ? m : [];

    public IEnumerable<KeyValuePair<string, int>> Followers(string p2Key, string p1Key) =>
        _trigrams.TryGetValue(p2Key + " " + p1Key, out var m) ? m : [];

    /// <summary>Все слова: нормализованный ключ, форма для показа, число употреблений.</summary>
    public IEnumerable<(string Key, string Form, int Count, long LastUsed)> AllWords() =>
        _words.Select(kv => (kv.Key, kv.Value.Form, kv.Value.Count, kv.Value.LastUsed));

    public void Remove(string word) => RemoveMany([word]);

    /// <summary>Удаляет слова и все сочетания с ними — одним проходом по n-граммам (удаление сотен слов не подвешивает UI).</summary>
    public void RemoveMany(IEnumerable<string> words)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            var key = CaseHelper.Normalize(w);
            if (!_words.Remove(key, out var removed)) continue;
            TotalCount -= removed.Count;
            _index.Remove(key);
            _bigrams.Remove(key);
            keys.Add(key);
        }
        if (keys.Count == 0) return;
        foreach (var m in _bigrams.Values)
            foreach (var k in m.Keys.Where(keys.Contains).ToList()) m.Remove(k);
        foreach (var k in _trigrams.Keys.Where(k => k.Split(' ').Any(keys.Contains)).ToList()) _trigrams.Remove(k);
        foreach (var m in _trigrams.Values)
            foreach (var k in m.Keys.Where(keys.Contains).ToList()) m.Remove(k);
        Dirty = true;
        Version++;
    }

    /// <summary>Добавляет к словарю другую модель (обучение на тексте / импорт, подготовленные в фоне): счётчики суммируются.</summary>
    public void MergeFrom(UserModel other)
    {
        foreach (var (key, o) in other._words)
        {
            if (!_words.TryGetValue(key, out var st))
            {
                _words[key] = st = new WordStat { Form = o.Form };
                _index.Add(key);
            }
            else st.Form = PickForm(o.Form, false, st.Form, KnownForm);
            st.Count += o.Count;
            TotalCount += o.Count;
            st.LastUsed = Math.Max(st.LastUsed, o.LastUsed);
        }
        foreach (var (ctx, m) in other._bigrams)
            foreach (var (next, c) in m) Inc(_bigrams, ctx, next, c);
        foreach (var (ctx, m) in other._trigrams)
            foreach (var (next, c) in m) Inc(_trigrams, ctx, next, c);
        Dirty = true;
        Version++;
    }

    /// <summary>
    /// Чистка: если словарь разросся, убираем слова, встреченные один раз и давно (обычно опечатки и разовые имена).
    /// </summary>
    public int Prune(long now, int maxWords = 150_000)
    {
        if (_words.Count <= maxWords) return 0;
        var victims = _words.Where(kv => kv.Value.Count <= 1 && now - kv.Value.LastUsed > 60 * 1440)
            .Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (victims.Count == 0) return 0;
        foreach (var k in victims)
        {
            TotalCount -= _words[k].Count;
            _words.Remove(k);
            _index.Remove(k);
            _bigrams.Remove(k);
        }
        foreach (var m in _bigrams.Values) foreach (var k in m.Keys.Where(victims.Contains).ToList()) m.Remove(k);
        foreach (var k in _trigrams.Keys.Where(k => k.Split(' ').Any(victims.Contains)).ToList()) _trigrams.Remove(k);
        foreach (var m in _trigrams.Values) foreach (var k in m.Keys.Where(victims.Contains).ToList()) m.Remove(k);
        Dirty = true;
        Version++;
        return victims.Count;
    }

    public void Clear()
    {
        _words.Clear();
        TotalCount = 0;
        _index.Clear();
        _bigrams.Clear();
        _trigrams.Clear();
        Dirty = true;
        Version++;
    }

    /// <summary>Обучение на произвольном тексте (например, типовых письмах).</summary>
    public int Train(string text)
    {
        var tracker = new TypingTracker();
        long now = NowMinutes();
        int n = 0;
        foreach (var c in text + "\n")
        {
            if (tracker.Feed(c) is { } w)
            {
                Learn(w, now);
                n++;
            }
        }
        return n;
    }

    // ---------- сериализация (TSV) ----------

    public string Serialize()
    {
        var sb = new StringBuilder(_words.Count * 24);
        sb.Append("#type9-userdict v1\n");
        foreach (var (k, s) in _words)
            sb.Append("W\t").Append(k).Append('\t').Append(s.Form).Append('\t').Append(s.Count).Append('\t').Append(s.LastUsed).Append('\n');
        foreach (var (ctx, m) in _bigrams)
            foreach (var (next, c) in m)
                sb.Append("B\t").Append(ctx).Append('\t').Append(next).Append('\t').Append(c).Append('\n');
        foreach (var (ctx, m) in _trigrams)
            foreach (var (next, c) in m)
                sb.Append("T\t").Append(ctx).Append('\t').Append(next).Append('\t').Append(c).Append('\n');
        return sb.ToString();
    }

    public static UserModel Parse(string text)
    {
        var model = new UserModel();
        model.Merge(text);
        model.Dirty = false;
        return model;
    }

    /// <summary>Добавляет данные из TSV (импорт): счётчики суммируются.</summary>
    public void Merge(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length < 4) continue;
            switch (p[0])
            {
                case "W" when p.Length >= 5 && int.TryParse(p[3], CultureInfo.InvariantCulture, out var cnt) && cnt > 0
                               && p[2].Length > 0 && IsLearnable(p[2]):
                    long.TryParse(p[4], CultureInfo.InvariantCulture, out var last);
                    var wkey = CaseHelper.Normalize(p[2]); // ключ из файла не доверяем — пересчитываем из формы
                    if (!_words.TryGetValue(wkey, out var st))
                    {
                        _words[wkey] = st = new WordStat { Form = p[2] };
                        _index.Add(wkey);
                    }
                    st.Count += cnt;
                    TotalCount += cnt;
                    st.LastUsed = Math.Max(st.LastUsed, last);
                    break;
                case "B" when int.TryParse(p[3], CultureInfo.InvariantCulture, out var bc) && bc > 0 && p[1].Length > 0 && p[2].Length > 0:
                    Inc(_bigrams, CaseHelper.Normalize(p[1]), CaseHelper.Normalize(p[2]), bc);
                    break;
                case "T" when int.TryParse(p[3], CultureInfo.InvariantCulture, out var tc) && tc > 0 && p[1].Contains(' ') && p[2].Length > 0:
                    Inc(_trigrams, CaseHelper.Normalize(p[1]), CaseHelper.Normalize(p[2]), tc);
                    break;
            }
        }
        Dirty = true;
        Version++;
    }
}
