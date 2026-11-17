namespace Type9.Core;

public enum AppTheme { Dark, Light, System }
public enum RunMode { Normal, Elevated }
public enum RadialTrigger { Hold, Toggle }
public enum InsertMethod { Auto, Typing, Clipboard }
public enum PopupPlacement { Caret, Fixed }

public sealed class AppSettings
{
    public const string DefaultAccent = "#9E1B32"; // бордовый

    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public string Accent { get; set; } = DefaultAccent;
    public AppLanguage Language { get; set; } = Loc.Default;

    /// <summary>Масштаб интерфейса в процентах; 0 — автоматически по разрешению экрана.</summary>
    public int UiScale { get; set; }

    public bool Autostart { get; set; }
    public RunMode Mode { get; set; } = RunMode.Normal;

    // Подсказки
    public bool SuggestionsEnabled { get; set; } = true;
    public int MinPrefix { get; set; } = 2;
    public int MaxSuggestions { get; set; } = 3;
    public bool AddSpaceAfterWord { get; set; } = true;
    public bool SmartPunctuation { get; set; } = true;
    public bool NextWordPrediction { get; set; } = true;
    public int NextWordMinCount { get; set; } = 2;
    public bool LearnEnabled { get; set; } = true;
    public PopupPlacement Placement { get; set; } = PopupPlacement.Caret;
    public bool HideInFullscreen { get; set; } = true;

    // Круговое меню
    public string RadialHotkey { get; set; } = "Alt+Q";
    public RadialTrigger RadialTrigger { get; set; } = RadialTrigger.Hold;
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";

    // Вставка
    public bool NewlineAsShiftEnter { get; set; } = true;
    public InsertMethod InsertMethod { get; set; } = InsertMethod.Auto;
    public int ClipboardThreshold { get; set; } = 200;

    public List<string> ExcludedProcesses { get; set; } =
        ["KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass"];
}
