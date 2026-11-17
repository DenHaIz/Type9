using Type9.Core;
using Type9.Native;

namespace Type9.Input;

/// <summary>
/// Предупреждения о том, что назначенная клавиша пересекается со стандартными сочетаниями Windows и программ
/// (одиночный Ctrl и Ctrl+C, F5 — обновить страницу, Mouse4 — «Назад» в браузере и т.п.).
/// </summary>
public static class HotkeyAdvisor
{
    private static string L(string ru, string en) => Loc.Current == AppLanguage.English ? en : ru;

    private static readonly Dictionary<char, (string Ru, string En)> CtrlLetters = new()
    {
        ['C'] = ("копировать", "copy"), ['V'] = ("вставить", "paste"), ['X'] = ("вырезать", "cut"),
        ['Z'] = ("отменить", "undo"), ['Y'] = ("повторить", "redo"), ['A'] = ("выделить всё", "select all"),
        ['S'] = ("сохранить", "save"), ['F'] = ("поиск", "find"), ['P'] = ("печать", "print"),
        ['N'] = ("новый документ/окно", "new document/window"), ['O'] = ("открыть", "open"), ['W'] = ("закрыть вкладку", "close tab"),
        ['T'] = ("новая вкладка", "new tab"), ['R'] = ("обновить", "refresh"), ['H'] = ("история / замена", "history / replace"),
        ['L'] = ("адресная строка", "address bar"), ['D'] = ("закладка", "bookmark"), ['B'] = ("жирный", "bold"),
        ['I'] = ("курсив", "italic"), ['U'] = ("подчёркнутый", "underline"), ['E'] = ("поиск / по центру", "search / center"),
        ['K'] = ("вставить ссылку", "insert link"), ['Q'] = ("выход из программы", "quit app"), ['J'] = ("загрузки", "downloads"),
        ['G'] = ("найти далее", "find next"), ['M'] = ("свернуть / новое письмо", "minimize / new message"),
    };

    private static readonly Dictionary<int, (string Ru, string En)> SingleKeys = new()
    {
        [Win32.VK_F1] = ("справка", "help"), [Win32.VK_F1 + 1] = ("переименование", "rename"),
        [Win32.VK_F1 + 2] = ("поиск", "search"), [Win32.VK_F1 + 3] = ("адресная строка", "address bar"),
        [Win32.VK_F1 + 4] = ("обновление страницы", "page refresh"), [Win32.VK_F1 + 5] = ("переход по панелям", "cycle panes"),
        [Win32.VK_F1 + 6] = ("проверка орфографии", "spell check"), [Win32.VK_F1 + 9] = ("меню программы", "app menu"),
        [Win32.VK_F1 + 10] = ("полный экран", "full screen"), [Win32.VK_F1 + 11] = ("инструменты разработчика / «Сохранить как»", "developer tools / Save As"),
        [Win32.VK_CAPITAL] = ("переключение регистра (CapsLock перестанет работать)", "caps toggle (CapsLock will stop working)"),
        [Win32.VK_INSERT] = ("режим вставки/замены", "insert/overwrite mode"),
        [Win32.VK_HOME] = ("переход в начало строки", "go to line start"), [Win32.VK_END] = ("переход в конец строки", "go to line end"),
        [Win32.VK_PRIOR] = ("прокрутка страницы", "page scroll"), [Win32.VK_NEXT] = ("прокрутка страницы", "page scroll"),
        [Win32.VK_DELETE] = ("удаление", "delete"), [Win32.VK_APPS] = ("контекстное меню", "context menu"),
        [0x90] = ("цифровая клавиатура (NumLock)", "numeric keypad (NumLock)"),
        [Win32.VK_MBUTTON] = ("средняя кнопка: открыть ссылку в новой вкладке, автопрокрутка", "middle click: open link in new tab, autoscroll"),
        [Win32.VK_XBUTTON1] = ("«Назад» в браузере и проводнике", "Back in browsers and Explorer"),
        [Win32.VK_XBUTTON2] = ("«Вперёд» в браузере и проводнике", "Forward in browsers and Explorer"),
    };

    /// <summary>Текст предупреждения или null, если клавиша безопасна.</summary>
    public static string? Check(Hotkey h)
    {
        if (h.IsEmpty) return null;
        var m = h.Modifiers;
        string name = h.ToString();

        if (h.IsSingleModifier)
        {
            var family = Hotkey.ModifierOf(h.Key) switch
            {
                HotkeyModifiers.Ctrl => "Ctrl+C, Ctrl+V",
                HotkeyModifiers.Alt => "Alt+Tab, Alt+F4",
                HotkeyModifiers.Shift => L("Shift+буква, выделение текста", "Shift+letter, text selection"),
                _ => "Win+D, Win+E",
            };
            return L($"{name} участвует в сочетаниях ({family}). Меню откроется только при удержании клавиши без других нажатий, а сами сочетания продолжат работать — но возможны случайные срабатывания.",
                     $"{name} is part of shortcuts ({family}). The wheel opens only when the key is held alone and shortcuts keep working — but accidental triggers are possible.");
        }

        if (m == HotkeyModifiers.None && SingleKeys.TryGetValue(h.Key, out var single))
            return L($"{name} в других программах — {single.Ru}. Пока Type9 запущен, это действие работать не будет.",
                     $"In other apps {name} means {single.En}. It won’t work there while Type9 is running.");

        if (m == HotkeyModifiers.Ctrl && h.Key is >= 'A' and <= 'Z' && CtrlLetters.TryGetValue((char)h.Key, out var ctrl))
            return L($"{name} — стандартное сочетание «{ctrl.Ru}». Пока Type9 запущен, оно не будет работать в других программах.",
                     $"{name} is the standard “{ctrl.En}” shortcut. It won’t work in other apps while Type9 is running.");

        if (m == HotkeyModifiers.Ctrl && h.Key is Win32.VK_TAB or Win32.VK_SPACE)
            return L($"{name} используется для переключения вкладок / подсказок ввода.", $"{name} is used for tab switching / input suggestions.");

        if (m == HotkeyModifiers.Alt && (h.Key is Win32.VK_TAB or Win32.VK_SPACE or Win32.VK_ESCAPE or Win32.VK_LEFT or Win32.VK_RIGHT
                                         || h.Key == Win32.VK_F1 + 3 || h.Key is 'F' or 'E' or 'V' or 'D'))
            return L($"{name} — системное сочетание или меню программ (Alt+Tab, Alt+F4, Alt+F — «Файл»).",
                     $"{name} is a system shortcut or app menu (Alt+Tab, Alt+F4, Alt+F — File).");

        if (m.HasFlag(HotkeyModifiers.Win))
            return L($"Сочетания с Win зарезервированы Windows ({name} может открыть системную функцию).",
                     $"Win shortcuts are reserved by Windows ({name} may trigger a system feature).");

        if (m == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift) && h.Key is 'T' or 'N' or Win32.VK_ESCAPE)
            return L($"{name} — стандартное сочетание браузеров/Windows.", $"{name} is a standard browser/Windows shortcut.");

        return null;
    }
}
