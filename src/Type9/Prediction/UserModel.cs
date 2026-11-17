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

    public static bool IsLearnable(string word) =>
        word.Length >= 2 && word.Length <= 40 && word.Any(char.IsLetter) && !word.Any(char.IsDigit);

    public void Learn(CompletedWord w, long now)
    {
        if (!IsLearnable(w.Word)) return;
        var key = CaseHelper.Normalize(w.Word);

        if (!_words.TryGetValue(key, out var st))
        {
            st = new WordStat { Form = PickForm(w.Word, w.SentenceStart, null) };
            _words[key] = st;
            _index.Add(key);
        }
        else
        {
            st.Form = PickForm(w.Word, w.SentenceStart, st.Form);
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

    /// <summary>Форма для показа: аббревиатуры и имена собственные сохраняем как есть, остальное — строчными.</summary>
    private static string PickForm(string word, bool sentenceStart, string? existing)
    {
        bool innerUpper = word.Skip(1).Any(char.IsUpper);
        if (innerUpper) return word;                          // ООО, iPhone
        if (char.IsUpper(word[0]))
            return sentenceStart ? existing ?? word.ToLowerInvariant() : word; // Москва в середине предложения
        return existing is not null && char.IsUpper(existing[0]) && !existing.Skip(1).Any(char.IsUpper)
            ? word // слово встретилось строчным — значит, это не имя собственное
            : existing ?? word;
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

    public IEnumerable<(string Form, int Count, long LastUsed)> AllWords() =>
        _words.Values.Select(s => (s.Form, s.Count, s.LastUsed));

    public void Remove(string word)
    {
        var key = CaseHelper.Normalize(word);
        if (!_words.Remove(key, out var removed)) return;
        TotalCount -= removed.Count;
        _index.Remove(key);
        _bigrams.Remove(key);
        foreach (var m in _bigrams.Values) m.Remove(key);
        foreach (var k in _trigrams.Keys.Where(k => k.Split(' ').Contains(key)).ToList()) _trigrams.Remove(k);
        foreach (var m in _trigrams.Values) m.Remove(key);
        Dirty = true;
        Version++;
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
                case "W" when p.Length >= 5 && int.TryParse(p[3], CultureInfo.InvariantCulture, out var cnt):
                    long.TryParse(p[4], CultureInfo.InvariantCulture, out var last);
                    if (!_words.TryGetValue(p[1], out var st))
                    {
                        _words[p[1]] = st = new WordStat { Form = p[2] };
                        _index.Add(p[1]);
                    }
                    st.Count += cnt;
                    TotalCount += cnt;
                    st.LastUsed = Math.Max(st.LastUsed, last);
                    break;
                case "B" when int.TryParse(p[3], CultureInfo.InvariantCulture, out var bc):
                    Inc(_bigrams, p[1], p[2], bc);
                    break;
                case "T" when int.TryParse(p[3], CultureInfo.InvariantCulture, out var tc):
                    Inc(_trigrams, p[1], p[2], tc);
                    break;
            }
        }
        Dirty = true;
        Version++;
    }
}
