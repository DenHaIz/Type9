namespace Type9.Prediction;

/// <summary>
/// Исправление опечаток: слова на расстоянии одной правки (пропущенная, лишняя, заменённая буква или две
/// переставленные соседние) от набранного. Вариант предлагается, только если он намного частотнее набранного:
/// редкое «выедал» → «выехал», «приет» → «привет», «здраствуйте» → «здравствуйте».
/// Замена соседней на клавиатуре буквы считается вероятнее замены далёкой.
/// </summary>
public static class Corrector
{
    /// <summary>Во сколько раз исправление должно быть частотнее набранного (или лучшего его дополнения).</summary>
    public const double Ratio = 30;

    /// <summary>Нижний порог частоты исправления — совсем редкие слова не предлагаем.</summary>
    public const double MinScore = 1e-6;

    /// <summary>С какой длины слова ищем опечатки.</summary>
    public const int MinLength = 5;

    private const string Ru = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
    private const string En = "abcdefghijklmnopqrstuvwxyz";

    private static readonly string[] RuRows = ["йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю"];
    private static readonly string[] EnRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
    private static readonly Dictionary<char, (int Row, int Col)> KeyPos = BuildKeyPos();

    private static Dictionary<char, (int, int)> BuildKeyPos()
    {
        var d = new Dictionary<char, (int, int)>();
        foreach (var rows in new[] { RuRows, EnRows })
            for (int r = 0; r < rows.Length; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    d[rows[r][c]] = (r, c);
        return d;
    }

    /// <summary>Клавиши рядом (ряды сдвинуты: у клавиши ряда r соседи сверху — col и col+1 ряда r−1).</summary>
    internal static bool Adjacent(char a, char b)
    {
        if (a == 'е' && b == 'ё' || a == 'ё' && b == 'е') return true;
        if (!KeyPos.TryGetValue(a, out var pa) || !KeyPos.TryGetValue(b, out var pb)) return false;
        if ((a is >= 'a' and <= 'z') != (b is >= 'a' and <= 'z')) return false;
        int dr = pb.Row - pa.Row, dc = pb.Col - pa.Col;
        return dr switch
        {
            0 => Math.Abs(dc) == 1,
            -1 => dc is 0 or 1,
            1 => dc is 0 or -1,
            _ => false,
        };
    }

    /// <summary>Все варианты на расстоянии одной правки и их «вероятность» (вес).</summary>
    /// <param name="prefixMode">Слово ещё набирается: вставка в конец — это обычное дополнение, её не даём.</param>
    internal static IEnumerable<(string Variant, double Weight)> Edits(string w, bool prefixMode)
    {
        string abc = w.Any(c => c is >= 'a' and <= 'z') ? En : Ru;
        // Лишняя буква (чаще всего — двойное нажатие или задетая соседняя)
        for (int i = 0; i < w.Length; i++)
        {
            bool neighbour = i > 0 && (w[i] == w[i - 1] || Adjacent(w[i], w[i - 1])) || i + 1 < w.Length && Adjacent(w[i], w[i + 1]);
            yield return (w.Remove(i, 1), neighbour ? 0.9 : 0.6);
        }
        // Переставленные соседние буквы
        for (int i = 0; i + 1 < w.Length; i++)
            if (w[i] != w[i + 1])
                yield return (string.Concat(w.AsSpan(0, i), [w[i + 1], w[i]], w.AsSpan(i + 2)), 0.9);
        // Заменённая буква
        for (int i = 0; i < w.Length; i++)
            foreach (var c in abc)
                if (c != w[i])
                    yield return (string.Concat(w.AsSpan(0, i), [c], w.AsSpan(i + 1)), Adjacent(w[i], c) ? 1.0 : 0.45);
        // Пропущенная буква
        for (int i = 0; i <= (prefixMode ? w.Length - 1 : w.Length); i++)
            foreach (var c in abc)
                yield return (w.Insert(i, c.ToString()), 0.8);
    }

    /// <summary>
    /// Исправления набранного (ключ нормализован). <paramref name="prefixMode"/> — слово ещё набирается:
    /// ищем и слова, начинающиеся с исправленного префикса. <paramref name="known"/> — набранное слово/префикс
    /// встречается у пользователя: тогда это не опечатка.
    /// </summary>
    /// <param name="extra">Дополнительный словарь (деловой): его слова — тоже возможные исправления.</param>
    public static List<(string Key, string Form, double Score)> Find(LanguageModel model, string typed, bool prefixMode, int max = 2,
        BaseDictionary? extra = null)
    {
        var result = new List<(string, string, double)>();
        // Короткие слова почти всегда «исправляются» в другое настоящее слово («мена» → «менее») — не трогаем
        if (typed.Length < MinLength) return result;

        // Насколько «нормально» само набранное: частота слова или лучшего его дополнения
        double own;
        if (prefixMode)
        {
            var (from, to) = model.PrefixRange(typed);
            own = BestInRange(model, from, to, out _);
        }
        else
        {
            int id = model.Find(typed);
            own = id >= 0 ? model.Unigram(id) : 0;
        }
        double threshold = Math.Max(MinScore, own * Ratio);
        // Набранное и так частое — исправление должно было бы входить в первую сотню слов языка: не ищем
        if (threshold > 5e-3) return result;

        var best = new Dictionary<string, (string Form, double Score)>(StringComparer.Ordinal);
        foreach (var (v, weight) in Edits(typed, prefixMode))
        {
            // В коротких словах маловероятные правки (далёкая буква) дают слишком много ложных «исправлений»
            if (weight < 0.8 && typed.Length < 6) continue;
            // Само исправленное слово…
            int exact = model.Find(v);
            double exactScore = exact >= 0 ? model.Unigram(exact) * weight : 0;
            // Слово делового словаря считается не реже его «плоской» частоты (в корпусе оно бывает редким)
            var extraForm = extra?.FormOf(v);
            if (extraForm is not null) exactScore = Math.Max(exactScore, PredictionEngine.SupplementFreq * weight);
            // …а пока слово набирается — и самое частое слово с таким началом (с понижением: это уже догадка);
            // из двух берём более вероятное, чтобы одна правка не давала два варианта
            if (prefixMode && v.Length >= 4)
            {
                var (from, to) = model.PrefixRange(v);
                if (from < to && BestInRange(model, from, to, out var id) * weight * 0.5 > exactScore)
                {
                    Consider(model.Key(id), model.Form(id), model.Unigram(id) * weight * 0.5);
                    continue;
                }
            }
            if (extraForm is not null) Consider(v, extraForm, exactScore);
            else if (exact >= 0) Consider(model.Key(exact), model.Form(exact), exactScore);
        }

        void Consider(string key, string form, double score)
        {
            if (score >= threshold && (!best.TryGetValue(key, out var cur) || cur.Score < score) && !Profanity.IsObscene(key))
                best[key] = (form, score);
        }

        foreach (var (key, (form, score)) in best.OrderByDescending(kv => kv.Value.Score).Take(max))
            result.Add((key, form, score));
        return result;
    }

    /// <summary>Самое частое слово в диапазоне (для длинных диапазонов — первые 3000, этого хватает для префиксов от 4 букв).</summary>
    private static double BestInRange(LanguageModel model, int from, int to, out int bestId)
    {
        bestId = -1;
        double best = 0;
        for (int i = from; i < to && i < from + 3000; i++)
        {
            double u = model.Unigram(i);
            if (u > best) { best = u; bestId = i; }
        }
        return best;
    }
}
