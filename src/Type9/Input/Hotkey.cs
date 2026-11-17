using Type9.Native;

namespace Type9.Input;

[Flags]
public enum HotkeyModifiers { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

/// <summary>Сочетание клавиш: модификаторы + клавиша (VK) или кнопка мыши (VK_MBUTTON / VK_XBUTTON1/2).</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    public static readonly Hotkey None = new(HotkeyModifiers.None, 0);

    public bool IsEmpty => Key == 0;
    public bool IsMouse => Key is Win32.VK_MBUTTON or Win32.VK_XBUTTON1 or Win32.VK_XBUTTON2;

    /// <summary>Одиночная клавиша-модификатор (например, правый Ctrl): сама по себе не поглощается, чтобы RCtrl+C работал.</summary>
    public bool IsSingleModifier => Modifiers == HotkeyModifiers.None && IsModifierKey(Key);

    private static readonly Dictionary<int, string> Names = new()
    {
        [Win32.VK_MBUTTON] = "MouseMiddle", [Win32.VK_XBUTTON1] = "Mouse4", [Win32.VK_XBUTTON2] = "Mouse5",
        [Win32.VK_SPACE] = "Space", [Win32.VK_TAB] = "Tab", [Win32.VK_RETURN] = "Enter", [Win32.VK_ESCAPE] = "Esc",
        [Win32.VK_BACK] = "Backspace", [Win32.VK_INSERT] = "Insert", [Win32.VK_DELETE] = "Delete",
        [Win32.VK_HOME] = "Home", [Win32.VK_END] = "End", [Win32.VK_PRIOR] = "PageUp", [Win32.VK_NEXT] = "PageDown",
        [Win32.VK_LEFT] = "Left", [Win32.VK_RIGHT] = "Right", [Win32.VK_UP] = "Up", [Win32.VK_DOWN] = "Down",
        [Win32.VK_PAUSE] = "Pause", [Win32.VK_CAPITAL] = "CapsLock", [Win32.VK_APPS] = "Menu",
        [0x91] = "ScrollLock", [0x90] = "NumLock",
        [Win32.VK_LCONTROL] = "LCtrl", [Win32.VK_RCONTROL] = "RCtrl", [Win32.VK_LMENU] = "LAlt", [Win32.VK_RMENU] = "RAlt",
        [Win32.VK_LSHIFT] = "LShift", [Win32.VK_RSHIFT] = "RShift", [Win32.VK_LWIN] = "LWin", [Win32.VK_RWIN] = "RWin",
        [0xC0] = "`", [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\",
        [0xBA] = ";", [0xDE] = "'", [0xBC] = ",", [0xBE] = ".", [0xBF] = "/",
    };

    public static string KeyName(int vk)
    {
        if (Names.TryGetValue(vk, out var n)) return n;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= Win32.VK_F1 and <= Win32.VK_F24) return "F" + (vk - Win32.VK_F1 + 1);
        if (vk is >= Win32.VK_NUMPAD0 and <= Win32.VK_NUMPAD9) return "Num" + (vk - Win32.VK_NUMPAD0);
        return "0x" + vk.ToString("X2");
    }

    private static int ParseKey(string s)
    {
        foreach (var (vk, name) in Names)
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return vk;
        if (s.Length == 1 && char.ToUpperInvariant(s[0]) is var c && (c is >= '0' and <= '9' or >= 'A' and <= 'Z')) return c;
        if (s.Length > 1 && (s[0] is 'F' or 'f') && int.TryParse(s[1..], out var f) && f is >= 1 and <= 24) return Win32.VK_F1 + f - 1;
        if (s.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[3..], out var d) && d is >= 0 and <= 9) return Win32.VK_NUMPAD0 + d;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex)) return hex;
        return 0;
    }

    public static Hotkey Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var mods = HotkeyModifiers.None;
        int key = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; break;
                case "alt": mods |= HotkeyModifiers.Alt; break;
                case "shift": mods |= HotkeyModifiers.Shift; break;
                case "win": mods |= HotkeyModifiers.Win; break;
                default: key = ParseKey(raw); break;
            }
        }
        // Текст "Ctrl++" после Split потеряет плюс — это сознательно не поддерживаем
        return key == 0 ? None : new Hotkey(mods, key);
    }

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(int vk) => vk is Win32.VK_SHIFT or Win32.VK_CONTROL or Win32.VK_MENU
        or Win32.VK_LSHIFT or Win32.VK_RSHIFT or Win32.VK_LCONTROL or Win32.VK_RCONTROL
        or Win32.VK_LMENU or Win32.VK_RMENU or Win32.VK_LWIN or Win32.VK_RWIN;

    public static HotkeyModifiers ModifierOf(int vk) => vk switch
    {
        Win32.VK_SHIFT or Win32.VK_LSHIFT or Win32.VK_RSHIFT => HotkeyModifiers.Shift,
        Win32.VK_CONTROL or Win32.VK_LCONTROL or Win32.VK_RCONTROL => HotkeyModifiers.Ctrl,
        Win32.VK_MENU or Win32.VK_LMENU or Win32.VK_RMENU => HotkeyModifiers.Alt,
        Win32.VK_LWIN or Win32.VK_RWIN => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    /// <summary>Текущие физически зажатые модификаторы.</summary>
    public static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        if (Win32.IsKeyDown(Win32.VK_CONTROL)) m |= HotkeyModifiers.Ctrl;
        if (Win32.IsKeyDown(Win32.VK_MENU)) m |= HotkeyModifiers.Alt;
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) m |= HotkeyModifiers.Shift;
        if (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) m |= HotkeyModifiers.Win;
        return m;
    }

    public bool Matches(int vk, HotkeyModifiers mods) =>
        !IsEmpty && vk == Key && (IsSingleModifier ? (mods & ~ModifierOf(vk)) == HotkeyModifiers.None : mods == Modifiers);
}
