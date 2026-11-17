using System.Globalization;

namespace Type9.Core;

/// <summary>Подстановки во фразах: {date}, {time}, {clipboard}.</summary>
public static class Placeholders
{
    public static Func<string?> ClipboardReader { get; set; } = () =>
    {
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    };

    public static string Expand(string text)
    {
        if (text.IndexOf('{') < 0) return text;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var now = DateTime.Now;
        var r = text
            .Replace("{date}", now.ToString("dd.MM.yyyy", ru), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("HH:mm", ru), StringComparison.OrdinalIgnoreCase);
        if (r.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase))
            r = r.Replace("{clipboard}", ClipboardReader() ?? "", StringComparison.OrdinalIgnoreCase);
        return r;
    }
}
