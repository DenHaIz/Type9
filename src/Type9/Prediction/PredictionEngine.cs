using Type9.Core;
using Type9.Input;

namespace Type9.Prediction;

public sealed record PredictionOptions(
    int MinPrefix = 2,
    int MaxResults = 3,
    bool AddSpace = true,
    bool NextWord = true,
    int NextWordMinCount = 2,
    SuggestLanguage Language = SuggestLanguage.Russian,
    bool GreetingComma = true,
    bool Corrections = true,
    bool AutoCapitalize = true);

/// <summary>
/// Ранжирование вариантов. Оценка слова — смесь вероятностей:
///   контекст пользователя (его триграммы/биграммы) ×2, контекст корпуса ×1,
///   частота слова (корпус + личный словарь) с поправкой на согласование окончаний с предыдущим словом.
/// Личные слова получают бонус за недавнее использование, длинные дополнения — небольшой бонус за экономию нажатий.
/// </summary>
public sealed class PredictionEngine
{
    private const double WUserCtx = 2.0, WBaseCtx = 1.0, WBaseUni = 1.0, WUserUni = 0.35, WExtraUni = 0.0005;

    /// <summary>Частота, которую получает слово делового словаря (как у умеренно частого слова корпуса).</summary>
    public const double SupplementFreq = 2e-5;
    private const int UnigramShortlist = 400;

    public LanguageModel? Russian { get; set; }
    public LanguageModel? English { get; set; }
    public BaseDictionary Extra { get; set; } = BaseDictionary.Empty;

    /// <summary>Встроенный деловой словарь (русский): слова, которых нет в модели по субтитрам.</summary>
    public BaseDictionary Supplement { get; set; } = BaseDictionary.Empty;
    public UserModel User { get; set; } = new();
    public IReadOnlyList<Abbreviation> Abbreviations { get; set; } = [];

    private enum Script { Other, Cyrillic, Latin }

    private static Script ScriptOf(string w)
    {
        foreach (var c in w)
        {
            if (c is >= 'Ѐ' and <= 'ӿ') return Script.Cyrillic;
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') return Script.Latin;
        }
        return Script.Other;
    }

    private static bool Allowed(Script s, SuggestLanguage lang) => lang switch
    {
        SuggestLanguage.Russian => s == Script.Cyrillic,
        SuggestLanguage.English => s == Script.Latin,
        _ => s is Script.Cyrillic or Script.Latin,
    };

    private LanguageModel? ModelFor(Script s) => s switch
    {
        Script.Cyrillic => Russian,
        Script.Latin => English,
        _ => null,
    };

    public IReadOnlyList<Suggestion> Predict(TypingContext ctx, PredictionOptions o)
    {
        var result = new List<Suggestion>(o.MaxResults);
        string space = o.AddSpace ? " " : "";

        if (ctx.Word.Length > 0)
        {
            // 1. Сокращения — при точном совпадении, всегда первыми
            foreach (var a in Abbreviations)
            {
                if (a.Key.Length > 0 && a.Text.Length > 0 && string.Equals(a.Key, ctx.Word, StringComparison.OrdinalIgnoreCase))
                    result.Add(new Suggestion(SuggestionKind.Abbreviation, ctx.Word + " → ", OneLine(a.Text), Placeholders.Expand(a.Text), ctx.Word.Length));
            }

            var script = ScriptOf(ctx.Word);
            if (ctx.Word.Length >= Math.Max(1, o.MinPrefix) && Allowed(script, o.Language))
            {
                var model = ModelFor(script);
                var comps = Completions(ctx, model, o, space).ToList();
                var fixes = new List<Suggestion>();
                if (o.Corrections)
                {
                    string? p1 = ctx.Prev1 is null ? null : CaseHelper.Normalize(ctx.Prev1);
                    string? p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);
                    foreach (var (key, form) in Corrections(ctx.Word, model, prefixMode: true))
                    {
                        var punct = o.GreetingComma && Greeting.NeedsComma(key, p1, p2) ? "," : "";
                        var text = CaseHelper.Apply(ctx.Word, form, o.AutoCapitalize && ctx.SentenceStart) + punct;
                        comps.RemoveAll(c => string.Equals(c.Display.TrimEnd(','), text.TrimEnd(','), StringComparison.OrdinalIgnoreCase));
                        fixes.Add(new Suggestion(SuggestionKind.Correction, "", text, text + space, ctx.Word.Length));
                    }
                }
                // Исправление в разы частотнее всего, что начинается с набранного: такие дополнения — мусор корпуса
                result.AddRange(fixes.Count > 0 ? fixes : comps);
            }
        }
        else if (ctx.Prev1 is not null)
        {
            var script = ScriptOf(ctx.Prev1);
            if (Allowed(script, o.Language))
            {
                // Слово только что закончено пробелом — если в нём опечатка, первым вариантом предлагаем исправить его
                if (o.Corrections && ctx.Gap.Length is >= 1 and <= 3 && ctx.Gap[^1] == ' '
                    && ctx.Gap.All(c => c is ' ' or ',' or '!' or '?' or ':' or ';')
                    && Corrections(ctx.Prev1, ModelFor(script), prefixMode: false) is [var (key, form), ..])
                {
                    var p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);
                    var gap = ctx.Gap == " " && o.GreetingComma && Greeting.NeedsComma(key, p2, null) ? ", " : ctx.Gap;
                    var text = CaseHelper.Apply(ctx.Prev1, form);
                    result.Add(new Suggestion(SuggestionKind.Correction, "", text + gap.TrimEnd(' '), text + gap, ctx.Prev1.Length + ctx.Gap.Length));
                }
                if (o.MinPrefix == 0 || o.NextWord)
                    result.AddRange(NextWords(ctx, o.MinPrefix == 0 ? ModelFor(script) : null, o, space));
            }
        }

        return result.Count > o.MaxResults ? result.GetRange(0, o.MaxResults) : result;
    }

    // ------------------------------------------------------------------ дополнение слова

    private sealed class Candidate
    {
        public string Form = "";
        public double BaseUni, UserUni, BaseCtx, UserCtx;
    }

    private IEnumerable<Suggestion> Completions(TypingContext ctx, LanguageModel? model, PredictionOptions o, string space)
    {
        var prefix = CaseHelper.Normalize(ctx.Word);
        var p1 = ctx.Prev1 is null ? null : CaseHelper.Normalize(ctx.Prev1);
        var p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);
        var cands = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var script = ScriptOf(ctx.Word);

        Candidate Get(string key, string form)
        {
            if (!cands.TryGetValue(key, out var c)) cands[key] = c = new Candidate { Form = form };
            return c;
        }

        if (model is not null)
        {
            var (from, to) = model.PrefixRange(prefix);
            int i1 = p1 is null ? -1 : model.Find(p1);
            int i2 = p2 is null ? -1 : model.Find(p2);

            // Контекст корпуса: продолжения предыдущих слов, подходящие под префикс
            if (i1 >= 0)
            {
                double tot1 = Math.Max(1, model.ContextTotal(i1));
                foreach (var (id, cnt) in model.Followers(i1))
                    if (id >= from && id < to && model.KeySpan(id).Length > prefix.Length)
                        Get(model.Key(id), model.Form(id)).BaseCtx += 0.35 * cnt / tot1;
                if (i2 >= 0)
                {
                    double tot2 = Math.Max(1, model.Bigram(i2, i1));
                    foreach (var (id, cnt) in model.Followers(i2, i1))
                        if (id >= from && id < to)
                            Get(model.Key(id), model.Form(id)).BaseCtx += 0.65 * cnt / tot2;
                }
            }

            // Самые частые слова с этим префиксом (короткий список, чтобы не перебирать тысячи)
            foreach (var id in TopByUnigram(model, from, to, prefix.Length))
                Get(model.Key(id), model.Form(id));
            foreach (var (key, c) in cands)
            {
                int id = model.Find(key);
                if (id >= 0) c.BaseUni = model.Unigram(id);
            }
        }

        if (script == Script.Cyrillic)
            foreach (var (key, form, weight) in Supplement.ByPrefix(prefix))
                if (key.Length > prefix.Length)
                    Get(key, form).BaseUni += SupplementFreq * weight;

        foreach (var (key, form, score) in Extra.ByPrefix(prefix))
            if (key.Length > prefix.Length && ScriptOf(key) == script)
                Get(key, form).BaseUni += WExtraUni * score;

        // Личный словарь
        long now = UserModel.NowMinutes();
        double userTotal = User.TotalCount + 200;
        double u1 = p1 is null ? 0 : User.ContextTotal(p1);
        double u2 = p1 is null || p2 is null ? 0 : User.ContextTotal(p2, p1);
        foreach (var (key, st) in User.ByPrefix(prefix))
        {
            if (key.Length <= prefix.Length || ScriptOf(key) != script) continue;
            // Незнакомое словарю слово, встреченное один раз, — скорее опечатка: предлагаем со второго употребления
            if (st.Count < 2 && (model is null || model.Find(key) < 0)) continue;
            var c = Get(key, st.Form);
            c.Form = st.Form;
            double rec = Recency(now, st.LastUsed);
            c.UserUni = st.Count / userTotal * rec;
            // +2 в знаменателе: одно случайное сочетание не перевешивает устойчивые привычки
            if (u1 > 0) c.UserCtx += 0.35 * User.Bigram(p1, key) / (u1 + 2) * rec;
            if (u2 > 0) c.UserCtx += 0.65 * User.Trigram(p2, p1, key) / (u2 + 2) * rec;
        }

        double liftPow = model?.Language == "ru" ? 0.8 : 0.4;
        return cands
            .Select(kv =>
            {
                var c = kv.Value;
                double lift = p1 is not null && model is not null ? model.SuffixLift(p1, kv.Key) : 1;
                double score = (WUserCtx * c.UserCtx + WBaseCtx * c.BaseCtx) * Math.Pow(lift, liftPow * 0.4)
                             + (WBaseUni * c.BaseUni + WUserUni * c.UserUni) * Math.Pow(lift, liftPow);
                score *= 1 + 0.03 * Math.Min(kv.Key.Length - prefix.Length, 8);
                return (kv.Key, c.Form, Score: score);
            })
            .Where(x => x.Score > 0 && x.Key.Length > prefix.Length && (!Profanity.IsObscene(x.Key) || OwnWord(x.Key)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Key.Length)
            // Слово корпуса без поддержки контекста, у которого есть намного более частый «сосед» в одну букву, —
            // скорее всего опечатка из субтитров («здраствуйте»): не предлагаем
            .Where(x => !(o.Corrections && model is not null && cands[x.Key] is { BaseCtx: 0, UserCtx: 0, UserUni: 0 }
                          && Corrector.Find(model, x.Key, prefixMode: false, max: 1).Count > 0))
            .Take(o.MaxResults)
            .Select(x => MakeCompletion(ctx.Word, x.Form, space, o.GreetingComma && Greeting.NeedsComma(x.Key, p1, p2) ? "," : "",
                                        o.AutoCapitalize && ctx.SentenceStart));
    }

    /// <summary>Слово, которое пользователь сам писал хотя бы дважды.</summary>
    private bool OwnWord(string key) => (User.Get(key)?.Count ?? 0) >= 2;

    /// <summary>Исправления опечатки в набранном слове (или префиксе), если слово не знакомо пользователю.</summary>
    private List<(string Key, string Form)> Corrections(string typed, LanguageModel? model, bool prefixMode)
    {
        if (model is null || typed.Length < 3 || !typed.All(char.IsLetter)) return [];
        var key = CaseHelper.Normalize(typed);
        // Слово, которое пользователь уже писал (хотя бы дважды) или которое есть в его словарях, — не опечатка
        if (prefixMode
                ? User.ByPrefix(key).Any(x => x.Stat.Count >= 2) || Extra.ByPrefix(key).Any() || Supplement.ByPrefix(key).Any()
                : (User.Get(key)?.Count ?? 0) >= 2 || Extra.Contains(key) || Supplement.Contains(key))
            return [];
        return Corrector.Find(model, key, prefixMode, extra: Supplement).Select(c => (c.Key, c.Form)).ToList();
    }

    private static IEnumerable<int> TopByUnigram(LanguageModel model, int from, int to, int prefixLen)
    {
        if (to - from <= UnigramShortlist)
        {
            for (int i = from; i < to; i++)
                if (model.KeySpan(i).Length > prefixLen) yield return i;
            yield break;
        }
        var heap = new PriorityQueue<int, float>(UnigramShortlist + 1);
        for (int i = from; i < to; i++)
        {
            if (model.KeySpan(i).Length <= prefixLen) continue;
            heap.Enqueue(i, model.Unigram(i));
            if (heap.Count > UnigramShortlist) heap.Dequeue();
        }
        while (heap.Count > 0) yield return heap.Dequeue();
    }

    private static double Recency(long now, long last)
    {
        double days = (now - last) / 1440.0;
        return days < 30 ? 1 + 0.5 * (1 - days / 30) : 1;
    }

    /// <param name="punct">Знак сразу после слова — запятая после приветствия («Здравствуйте,»).</param>
    /// <param name="capitalize">Начало предложения — первая буква заглавная (набранное слово заменяется целиком).</param>
    internal static Suggestion MakeCompletion(string typed, string form, string space, string punct = "", bool capitalize = false)
    {
        var applied = CaseHelper.Apply(typed, form, capitalize);
        if (applied.StartsWith(typed, StringComparison.Ordinal))
            return new Suggestion(SuggestionKind.Completion, typed, applied[typed.Length..] + punct, applied[typed.Length..] + punct + space, 0);
        // Префикс отличается (например, «е»/«ё» или регистр) — заменяем слово целиком
        return new Suggestion(SuggestionKind.Completion, applied[..Math.Min(typed.Length, applied.Length)],
            applied[Math.Min(typed.Length, applied.Length)..] + punct, applied + punct + space, typed.Length);
    }

    // ------------------------------------------------------------------ следующее слово

    private IEnumerable<Suggestion> NextWords(TypingContext ctx, LanguageModel? model, PredictionOptions o, string space)
    {
        var p1 = CaseHelper.Normalize(ctx.Prev1!);
        var p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);
        var script = ScriptOf(ctx.Prev1!);
        var scores = new Dictionary<string, (string Form, double Score)>(StringComparer.Ordinal);

        void Add(string key, string form, double s)
        {
            if (key.Length < 3 || key == p1 || ScriptOf(key) != script) return; // короткие слова быстрее напечатать
            if (Profanity.IsObscene(key) && !OwnWord(key)) return;
            scores[key] = scores.TryGetValue(key, out var cur) ? (cur.Form, cur.Score + s) : (form, s);
        }

        // Корпус (только в режиме «подсказывать после пробела»)
        if (model is not null)
        {
            int i1 = model.Find(p1);
            if (i1 >= 0)
            {
                double tot1 = Math.Max(1, model.ContextTotal(i1));
                foreach (var (id, cnt) in model.Followers(i1))
                    Add(model.Key(id), model.Form(id), WBaseCtx * 0.35 * cnt / tot1);
                int i2 = p2 is null ? -1 : model.Find(p2);
                if (i2 >= 0)
                {
                    double tot2 = Math.Max(1, model.Bigram(i2, i1));
                    foreach (var (id, cnt) in model.Followers(i2, i1))
                        Add(model.Key(id), model.Form(id), WBaseCtx * 0.65 * cnt / tot2);
                }
            }
        }

        // Личные привычки: в обычном режиме — только устойчивые (повторённые) продолжения
        int minCount = model is null ? o.NextWordMinCount : 1;
        long now = UserModel.NowMinutes();
        double u1 = User.ContextTotal(p1);
        if (u1 > 0)
            foreach (var (k, c) in User.Followers(p1))
                if (c >= minCount && Known(k)) Add(k, User.Get(k)?.Form ?? k, WUserCtx * 0.35 * c / (u1 + 2) * Recency(now, User.Get(k)?.LastUsed ?? 0));
        if (p2 is not null)
        {
            double u2 = User.ContextTotal(p2, p1);
            if (u2 > 0)
                foreach (var (k, c) in User.Followers(p2, p1))
                    if (c >= minCount && Known(k)) Add(k, User.Get(k)?.Form ?? k, WUserCtx * 0.65 * c / (u2 + 2) * Recency(now, User.Get(k)?.LastUsed ?? 0));
        }

        bool Known(string k) => (User.Get(k)?.Count ?? 0) >= 2 || (model ?? ModelFor(script))?.Find(k) >= 0;

        string lead = ctx.NeedsLeadingSpace ? " " : "";
        return scores
            .OrderByDescending(kv => kv.Value.Score)
            .Take(o.MaxResults)
            .Select(kv =>
            {
                var form = kv.Value.Form;
                if (o.GreetingComma && Greeting.NeedsComma(kv.Key, p1, p2)) form += ",";
                return new Suggestion(SuggestionKind.NextWord, "", form, lead + form + space, 0);
            });
    }

    private static string OneLine(string text)
    {
        var t = text.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
        return t.Length > 48 ? t[..47] + "…" : t;
    }
}
