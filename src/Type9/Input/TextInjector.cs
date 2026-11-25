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
    /// <summary>
    /// Подгоняет регистр варианта под набранное: «Здр»+«здравствуйте» → «Здравствуйте», «доб» → «добрый».
    /// Капс не переносим («ДОБ» → «Добрый»): регистр набранного известен неточно (CapsLock мог переключиться
    /// вне нашего хука), а слово капсом почти всегда не то, что нужно. Слова с заглавными внутри («iPhone») не трогаем.
    /// </summary>
    /// <param name="capitalize">Начало предложения: первая буква заглавная, даже если набрана строчная.</param>
    public static string Apply(string typed, string candidate, bool capitalize = false)
    {
        if (candidate.Length == 0 || !char.IsLower(candidate[0])) return candidate;
        if (candidate.Skip(1).Any(char.IsUpper)) return candidate;
        if (capitalize || typed.Length > 0 && char.IsUpper(typed[0]) || IsInverted(typed))
            return char.ToUpperInvariant(candidate[0]) + candidate[1..];
        return candidate;
    }

    /// <summary>«пРИВЕТ», «дА»: включён CapsLock и первая буква набрана с Shift — имелась в виду заглавная.</summary>
    public static bool IsInverted(string w)
    {
        if (w.Length < 2 || !char.IsLower(w[0])) return false;
        bool any = false;
        for (int i = 1; i < w.Length; i++)
        {
            if (!char.IsLetter(w[i])) continue;
            if (!char.IsUpper(w[i])) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Все буквы заглавные (не меньше двух).</summary>
    public static bool IsAllCaps(string w)
    {
        int letters = 0;
        foreach (var c in w)
        {
            if (!char.IsLetter(c)) continue;
            if (!char.IsUpper(c)) return false;
            letters++;
        }
        return letters >= 2;
    }

    /// <summary>
    /// Регистр слова для личного словаря. Капс почти всегда случайный (CapsLock, сбой отслеживания): «ЧЕРЕЗ» → «через»,
    /// «дА» → «Да». Капсом остаются аббревиатуры: известные словарю («ИНН») и короткие незнакомые («СНИЛС»).
    /// </summary>
    /// <param name="knownForm">Словарная форма по нормализованному ключу или null, если слово неизвестно.</param>
    public static string CleanCase(string word, Func<string, string?>? knownForm)
    {
        if (IsInverted(word)) return char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
        if (!IsAllCaps(word)) return word;
        var form = knownForm?.Invoke(Normalize(word));
        if (form is not null) return form.Skip(1).Any(char.IsUpper) ? form : form.ToLowerInvariant();
        return word.Count(char.IsLetter) <= 5 ? word : word.ToLowerInvariant();
    }

    public static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е');
}
