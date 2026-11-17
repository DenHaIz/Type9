using Type9.Core;
using Type9.Input;

namespace Type9.Prediction;

public sealed record PredictionOptions(
    int MinPrefix = 2,
    int MaxResults = 3,
    bool AddSpace = true,
    bool NextWord = true,
    int NextWordMinCount = 2);

/// <summary>Ранжирует варианты: базовый словарь + выученные слова + контекст (n-граммы) + сокращения.</summary>
public sealed class PredictionEngine
{
    private const double WBase = 1.0, WUser = 0.9, WBigram = 1.1, WTrigram = 1.6, WRecency = 0.35;

    public BaseDictionary Base { get; set; } = BaseDictionary.Empty;
    public UserModel User { get; set; } = new();
    public IReadOnlyList<Abbreviation> Abbreviations { get; set; } = [];

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

            if (ctx.Word.Length >= o.MinPrefix)
                result.AddRange(Completions(ctx, o, space).Take(Math.Max(0, o.MaxResults - result.Count)));
        }
        else if (o.NextWord && ctx.Prev1 is not null)
        {
            result.AddRange(NextWords(ctx, o, space));
        }

        return result.Count > o.MaxResults ? result.GetRange(0, o.MaxResults) : result;
    }

    private IEnumerable<Suggestion> Completions(TypingContext ctx, PredictionOptions o, string space)
    {
        var prefix = CaseHelper.Normalize(ctx.Word);
        var p1 = ctx.Prev1 is null ? null : CaseHelper.Normalize(ctx.Prev1);
        var p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);
        long now = UserModel.NowMinutes();

        var scored = new Dictionary<string, (string Form, double Score)>(StringComparer.Ordinal);

        foreach (var (key, form, score) in Base.ByPrefix(prefix))
        {
            if (key.Length <= prefix.Length) continue;
            scored[key] = (form, WBase * score);
        }

        foreach (var (key, st) in User.ByPrefix(prefix))
        {
            if (key.Length <= prefix.Length) continue;
            double s = scored.TryGetValue(key, out var b) ? b.Score : 0;
            s += WUser * Math.Log(1 + st.Count);
            double ageDays = (now - st.LastUsed) / 1440.0;
            if (ageDays < 14) s += WRecency * (1 - ageDays / 14);
            s += WBigram * Math.Log(1 + User.Bigram(p1, key));
            s += WTrigram * Math.Log(1 + User.Trigram(p2, p1, key));
            scored[key] = (st.Form, s);
        }

        return scored
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Key.Length)
            .Take(o.MaxResults)
            .Select(kv => MakeCompletion(ctx.Word, kv.Value.Form, space));
    }

    internal static Suggestion MakeCompletion(string typed, string form, string space)
    {
        var applied = CaseHelper.Apply(typed, form);
        if (applied.StartsWith(typed, StringComparison.Ordinal))
            return new Suggestion(SuggestionKind.Completion, typed, applied[typed.Length..], applied[typed.Length..] + space, 0);
        // Префикс отличается (например, «е»/«ё» или регистр) — заменяем слово целиком
        return new Suggestion(SuggestionKind.Completion, applied[..Math.Min(typed.Length, applied.Length)],
            applied[Math.Min(typed.Length, applied.Length)..], applied + space, typed.Length);
    }

    private IEnumerable<Suggestion> NextWords(TypingContext ctx, PredictionOptions o, string space)
    {
        var p1 = CaseHelper.Normalize(ctx.Prev1!);
        var p2 = ctx.Prev2 is null ? null : CaseHelper.Normalize(ctx.Prev2);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (k, c) in User.Followers(p1))
            if (c >= o.NextWordMinCount) scores[k] = WBigram * Math.Log(1 + c);
        if (p2 is not null)
            foreach (var (k, c) in User.Followers(p2, p1))
                if (c >= o.NextWordMinCount) scores[k] = scores.GetValueOrDefault(k) + WTrigram * Math.Log(1 + c);

        string lead = ctx.NeedsLeadingSpace ? " " : "";
        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(o.MaxResults)
            .Select(kv =>
            {
                var form = User.Get(kv.Key)?.Form ?? kv.Key;
                return new Suggestion(SuggestionKind.NextWord, "", form, lead + form + space, 0);
            });
    }

    private static string OneLine(string text)
    {
        var t = text.Replace("\r", "").Replace("\n", " ⏎ ").Trim();
        return t.Length > 48 ? t[..47] + "…" : t;
    }
}
