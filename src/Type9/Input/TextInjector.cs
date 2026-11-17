using System.Runtime.InteropServices;
using Type9.Native;

namespace Type9.Input;

/// <summary>Ввод текста в активное окно через SendInput. Все события помечены, чтобы наш хук их игнорировал.</summary>
internal static class TextInjector
{
    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();

    private static Win32.INPUT Key(ushort vk, bool up, bool extended = false) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? Win32.KEYEVENTF_KEYUP : 0) | (extended ? Win32.KEYEVENTF_EXTENDEDKEY : 0),
                dwExtraInfo = Win32.InjectionMarker,
            },
        },
    };

    private static Win32.INPUT Unicode(char c, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        U = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wScan = c,
                dwFlags = Win32.KEYEVENTF_UNICODE | (up ? Win32.KEYEVENTF_KEYUP : 0),
                dwExtraInfo = Win32.InjectionMarker,
            },
        },
    };

    private static void Send(List<Win32.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        Win32.SendInput((uint)inputs.Count, inputs.ToArray(), InputSize);
    }

    /// <summary>Стирает <paramref name="backspaces"/> символов и печатает <paramref name="text"/>.</summary>
    public static void Type(string text, int backspaces = 0, bool newlineAsShiftEnter = true)
    {
        var list = new List<Win32.INPUT>(backspaces * 2 + text.Length * 2 + 4);
        for (int i = 0; i < backspaces; i++)
        {
            list.Add(Key(Win32.VK_BACK, false));
            list.Add(Key(Win32.VK_BACK, true));
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case '\r':
                    continue;
                case '\n':
                    if (newlineAsShiftEnter) list.Add(Key(Win32.VK_SHIFT, false));
                    list.Add(Key(Win32.VK_RETURN, false));
                    list.Add(Key(Win32.VK_RETURN, true));
                    if (newlineAsShiftEnter) list.Add(Key(Win32.VK_SHIFT, true));
                    break;
                case '\t':
                    list.Add(Key(Win32.VK_TAB, false));
                    list.Add(Key(Win32.VK_TAB, true));
                    break;
                default:
                    list.Add(Unicode(c, false));
                    list.Add(Unicode(c, true));
                    break;
            }
        }
        Send(list);
    }

    public static void Backspaces(int count) => Type("", count);

    public static void CtrlV()
    {
        Send([Key(Win32.VK_CONTROL, false), Key(Win32.VK_V, false), Key(Win32.VK_V, true), Key(Win32.VK_CONTROL, true)]);
    }

    /// <summary>«Пустая» клавиша: после Alt/Win не даёт открыться меню окна / меню Пуск.</summary>
    public static void Dummy() => Send([Key(Win32.VK_DUMMY, false), Key(Win32.VK_DUMMY, true)]);
}

public static class CaseHelper
{
    /// <summary>Подгоняет регистр варианта под набранный префикс: «Здр»+«здравствуйте» → «Здравствуйте».</summary>
    public static string Apply(string typed, string candidate)
    {
        if (typed.Length == 0 || candidate.Length == 0) return candidate;
        int letters = typed.Count(char.IsLetter);
        bool allUpper = letters >= 2 && typed.Where(char.IsLetter).All(char.IsUpper);
        if (allUpper) return candidate.ToUpperInvariant();
        if (char.IsUpper(typed[0]) && char.IsLower(candidate[0]))
            return char.ToUpperInvariant(candidate[0]) + candidate[1..];
        return candidate;
    }

    public static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');
}
