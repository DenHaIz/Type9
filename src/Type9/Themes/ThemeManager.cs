using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Type9.Core;

namespace Type9.Themes;

/// <summary>
/// Палитры тёмной и светлой тем. Кисти создаются один раз и кладутся в ресурсы приложения (ключи «B.*»);
/// при смене темы их цвета плавно анимируются — всё окно перекрашивается без мерцания.
/// </summary>
internal static class ThemeManager
{
    private static readonly string[] Keys =
        ["Bg", "Surface", "Surface2", "Line", "Fg", "FgMuted", "Hover", "Pressed", "Danger",
         "Accent", "AccentSoft", "AccentFg", "OnAccent", "Disc", "Sector", "SectorLine", "Shadow"];

    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static AppTheme _mode = AppTheme.Dark;
    private static Color _accent = (Color)ColorConverter.ConvertFromString(AppSettings.DefaultAccent);

    public static bool IsDark { get; private set; } = true;
    public static Color AccentColor => _accent;
    public static event Action? Changed;
    public static event Action? Changing;

    public static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Бордовый", "#9E1B32"),
        ("Винный", "#7A1F3D"),
        ("Рубиновый", "#C4253F"),
        ("Медный", "#B5532A"),
        ("Сапфировый", "#2F5BD3"),
        ("Изумрудный", "#1E8A5A"),
        ("Графит", "#5A6170"),
    ];

    public static SolidColorBrush Brush(string key) => Brushes[key];

    public static void Init(ResourceDictionary res, AppTheme mode, string accent)
    {
        _mode = mode;
        _accent = ParseColor(accent);
        IsDark = ResolveDark(mode);
        var colors = Palette(IsDark, _accent);
        foreach (var key in Keys)
        {
            var b = new SolidColorBrush(colors[key]);
            b.Freeze();
            Brushes[key] = b;
            res["B." + key] = b;
        }
        res["C.Shadow"] = colors["Shadow"];
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _mode == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(_mode, _accent.ToString()));
        };
    }

    /// <summary>
    /// Применяет тему. Кисти в ресурсах приложения WPF замораживает, поэтому они заменяются целиком;
    /// плавность обеспечивают окна: по событию <see cref="Changing"/> они делают снимок и растворяют его.
    /// </summary>
    public static void Apply(AppTheme mode, string accent, bool animate = true)
    {
        var newAccent = ParseColor(accent);
        bool newDark = ResolveDark(mode);
        _mode = mode;
        if (newAccent == _accent && newDark == IsDark && Brushes.Count > 0) return;

        if (animate) Changing?.Invoke();
        _accent = newAccent;
        IsDark = newDark;
        var colors = Palette(IsDark, _accent);
        var res = Application.Current?.Resources;
        foreach (var key in Keys)
        {
            var b = new SolidColorBrush(colors[key]);
            b.Freeze();
            Brushes[key] = b;
            if (res is not null) res["B." + key] = b;
        }
        if (res is not null) res["C.Shadow"] = colors["Shadow"];
        Changed?.Invoke();
    }
    public static Color ParseColor(string? hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex ?? AppSettings.DefaultAccent); }
        catch { return (Color)ColorConverter.ConvertFromString(AppSettings.DefaultAccent); }
    }

    private static bool ResolveDark(AppTheme mode) => mode switch
    {
        AppTheme.Light => false,
        AppTheme.Dark => true,
        _ => !SystemUsesLightTheme(),
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static Dictionary<string, Color> Palette(bool dark, Color accent)
    {
        var white = Colors.White;
        return dark
            ? new()
            {
                ["Bg"] = Hex("#111216"),
                ["Surface"] = Hex("#1A1C21"),
                ["Surface2"] = Hex("#24262D"),
                ["Line"] = Hex("#2F323B"),
                ["Fg"] = Hex("#ECEDF0"),
                ["FgMuted"] = Hex("#8E93A0"),
                ["Hover"] = Hex("#14FFFFFF"),
                ["Pressed"] = Hex("#22FFFFFF"),
                ["Danger"] = Hex("#E5484D"),
                ["Accent"] = accent,
                ["AccentSoft"] = WithAlpha(accent, 0x4A),
                ["AccentFg"] = Mix(accent, white, 0.42),  // светлее — читается на тёмном фоне
                ["OnAccent"] = white,
                ["Disc"] = Hex("#EB16171B"),
                ["Sector"] = Hex("#26282F"),
                ["SectorLine"] = Hex("#33363F"),
                ["Shadow"] = Hex("#000000"),
            }
            : new()
            {
                ["Bg"] = Hex("#F2F2F5"),
                ["Surface"] = Hex("#FFFFFF"),
                ["Surface2"] = Hex("#EDEEF2"),
                ["Line"] = Hex("#DADCE2"),
                ["Fg"] = Hex("#17181C"),
                ["FgMuted"] = Hex("#676C7A"),
                ["Hover"] = Hex("#0D000000"),
                ["Pressed"] = Hex("#18000000"),
                ["Danger"] = Hex("#D1343A"),
                ["Accent"] = accent,
                ["AccentSoft"] = WithAlpha(accent, 0x26),
                ["AccentFg"] = accent,
                ["OnAccent"] = white,
                ["Disc"] = Hex("#F2FFFFFF"),
                ["Sector"] = Hex("#F1F2F5"),
                ["SectorLine"] = Hex("#DDE0E6"),
                ["Shadow"] = Hex("#5A000000"),
            };
    }
}
