namespace Type9.Core;

public enum AppTheme { Dark, Light, System }
public enum RunMode { Normal, Elevated }
public enum RadialTrigger { Hold, Toggle }
public enum InsertMethod { Auto, Typing, Clipboard }
public enum PopupPlacement { Caret, Fixed }
public enum SuggestLanguage { Russian, English, Both }

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
    /// <summary>С какой буквы подсказывать; 0 — сразу после пробела (следующее слово по смыслу).</summary>
    public int MinPrefix { get; set; } = 2;
    public SuggestLanguage SuggestLanguage { get; set; } = SuggestLanguage.Russian;
    public int MaxSuggestions { get; set; } = 3;
    public bool AddSpaceAfterWord { get; set; } = true;
    public bool SmartPunctuation { get; set; } = true;
    public bool GreetingComma { get; set; } = true;
    /// <summary>Предлагать исправление опечаток («приет» → «привет»).</summary>
    public bool FixTypos { get; set; } = true;
    /// <summary>Заглавная буква в начале предложения (после «. », «! », «? », перевода строки).</summary>
    public bool AutoCapitalize { get; set; } = true;
    /// <summary>CapsLock при видимой подсказке переключает вариант (по кругу), а не регистр.</summary>
    public bool CapsLockCycles { get; set; }
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

    // Обновления
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }

    public List<string> ExcludedProcesses { get; set; } =
        ["KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass"];
}
