namespace Type9.Prediction;

/// <summary>
/// Приветствие в начале предложения — после него принято ставить запятую: «Здравствуйте, …», «Добрый день, …».
/// Ключи нормализованы (нижний регистр, ё→е). Prev1/Prev2 — предыдущие слова того же предложения.
/// </summary>
internal static class Greeting
{
    private static readonly HashSet<string> Single = new(StringComparer.Ordinal)
    {
        "здравствуйте", "здравствуй", "привет", "приветствую", "hello", "hi", "hey",
    };

    private static readonly HashSet<(string, string)> Pairs =
    [
        ("добрый", "день"), ("добрый", "вечер"), ("доброе", "утро"),
        ("good", "morning"), ("good", "afternoon"), ("good", "evening"),
    ];

    // «Всем привет», «Всем добрый день», «Hi all» — тоже приветствие в начале
    private static bool IsLead(string? w) => w is null or "всем";

    public static bool NeedsComma(string key, string? prev1, string? prev2) =>
        (Single.Contains(key) && IsLead(prev1)) ||
        (prev1 is not null && Pairs.Contains((prev1, key)) && IsLead(prev2));
}
