using System.Text;

namespace Type9.Input;

/// <summary>Контекст набора: текущее (недописанное) слово и предыдущие слова того же предложения.</summary>
/// <param name="SentenceStart">Перед словом точно конец предложения («. », «! », перевод строки) — нужна заглавная.</param>
/// <param name="Gap">Слово ещё не начато: что стоит после предыдущего слова (обычно пробел) — для исправления опечатки в нём.</param>
public readonly record struct TypingContext(string Word, string? Prev1, string? Prev2, bool NeedsLeadingSpace,
    bool SentenceStart = false, string Gap = "")
{
    public static readonly TypingContext Empty = new("", null, null, false);
    public bool IsEmpty => Word.Length == 0 && Prev1 is null;
}

/// <summary>Слово, завершённое вводом разделителя (пробел, знак препинания, Enter).</summary>
/// <param name="Learnable">false — часть адреса/почты/пути или смесь алфавитов: такое не запоминаем.</param>
/// <param name="Tentative">Слово оборвано кликом/сменой поля, а не разделителем: могло быть недописано —
/// запоминаем, только если оно уже известно (словарю или пользователю).</param>
public readonly record struct CompletedWord(string Word, string? Prev1, string? Prev2, bool SentenceStart, bool Learnable = true,
    bool Tentative = false);

/// <summary>
/// Держит хвост набранного текста с момента последнего сброса (клик, навигация, смена окна)
/// и вычисляет из него текущее слово и контекст. Чистая логика — легко тестируется.
/// </summary>
public sealed class TypingTracker
{
    private const int MaxTail = 256;
    private readonly StringBuilder _tail = new();

    public string Tail => _tail.ToString();

    /// <summary>Абсолютная позиция набора (растёт при вводе, уменьшается при Backspace) — для отложенного обучения.</summary>
    public long Position { get; private set; }

    public void Reset() => _tail.Clear();

    public static bool IsWordChar(char c) => char.IsLetter(c) || c == '́';

    private static bool IsJoiner(char c) => c is '-' or '\'' or '’';

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '\n' or '…';

    /// <summary>Добавляет символ; возвращает завершённое слово, если символ его завершил.</summary>
    public CompletedWord? Feed(char c)
    {
        if (c == '\r') c = '\n';
        CompletedWord? completed = null;

        if (!IsWordChar(c) && !(IsJoiner(c) && EndsWithWordChar()))
        {
            var ctx = GetContext();
            var word = ctx.Word.TrimEnd('-', '\'', '’');
            if (word.Length > 0)
            {
                int start = _tail.Length - ctx.Word.Length;
                char before = start > 0 ? _tail[start - 1] : ' ';
                bool learnable = !IsAddressChar(before) && before != '.' && !IsAddressChar(c) && !MixedScripts(word);
                completed = new CompletedWord(word, ctx.Prev1, ctx.Prev2, ctx.Prev1 is null && IsAtSentenceStart(ctx.Word.Length), learnable);
            }
        }

        _tail.Append(c);
        Position++;
        if (_tail.Length > MaxTail) _tail.Remove(0, _tail.Length - MaxTail);
        return completed;
    }

    public void Backspace()
    {
        if (_tail.Length == 0) return;
        _tail.Length--;
        Position--;
    }

    /// <summary>Символы, которые «склеивают» слово с адресом, почтой, путём или идентификатором.</summary>
    private static bool IsAddressChar(char c) => c is '@' or '/' or '\\' or '_' or ':' or '#' or '=' or '&' or '<' or '>';

    private static bool MixedScripts(string w)
    {
        bool cyr = false, lat = false;
        foreach (var ch in w)
        {
            if (ch is >= 'Ѐ' and <= 'ӿ') cyr = true;
            else if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') lat = true;
        }
        return cyr && lat;
    }

    public bool LastCharIs(char c) => _tail.Length > 0 && _tail[^1] == c;

    private bool EndsWithWordChar() => _tail.Length > 0 && IsWordChar(_tail[^1]);

    private bool IsAtSentenceStart(int wordLen)
    {
        // Перед словом в хвосте нет ничего, кроме пробелов, или стоит конец предложения
        for (int i = _tail.Length - wordLen - 1; i >= 0; i--)
        {
            var ch = _tail[i];
            if (ch == ' ' || ch == '\t') continue;
            return IsSentenceEnd(ch);
        }
        return true;
    }

    public TypingContext GetContext()
    {
        int end = _tail.Length;
        int i = end;
        while (i > 0 && (IsWordChar(_tail[i - 1]) || (IsJoiner(_tail[i - 1]) && i - 2 >= 0 && IsWordChar(_tail[i - 2]))))
            i--;
        var word = _tail.ToString(i, end - i);

        // Предыдущие слова — только внутри текущего предложения
        string? p1 = null, p2 = null, gap = "";
        int j = i;
        for (int n = 0; n < 2; n++)
        {
            // пропускаем разделители (кроме конца предложения)
            while (j > 0 && !IsWordChar(_tail[j - 1]))
            {
                if (IsSentenceEnd(_tail[j - 1])) { j = -1; break; }
                j--;
            }
            if (j <= 0) break;
            int we = j;
            while (j > 0 && (IsWordChar(_tail[j - 1]) || (IsJoiner(_tail[j - 1]) && j - 2 >= 0 && IsWordChar(_tail[j - 2]))))
                j--;
            var w = _tail.ToString(j, we - j);
            if (n == 0) { p1 = w; gap = _tail.ToString(we, i - we); }
            else p2 = w;
        }

        bool needsSpace = word.Length == 0 && _tail.Length > 0 && !char.IsWhiteSpace(_tail[^1]);
        return new TypingContext(word, p1, p2, needsSpace, p1 is null && AfterSentenceEnd(i), word.Length == 0 && p1 is not null ? gap : "");
    }

    /// <summary>
    /// Перед позицией — явный конец предложения: «.», «!», «?», «…» и пробел, или перевод строки.
    /// Пустой хвост (после клика) не считается: что стоит в поле левее, неизвестно.
    /// Точка после одной буквы («т.е.», «г.») — сокращение, а не конец предложения.
    /// </summary>
    private bool AfterSentenceEnd(int pos)
    {
        int k = pos;
        bool space = false;
        while (k > 0 && _tail[k - 1] is ' ' or '\t' or ' ') { k--; space = true; }
        if (k == 0) return false;
        char ch = _tail[k - 1];
        if (ch == '\n') return true;
        if (!space || !IsSentenceEnd(ch)) return false;
        if (ch != '.') return true;
        // «т.е.» / «г.»: перед точкой одиночная буква
        int letters = 0;
        for (int m = k - 2; m >= 0 && IsWordChar(_tail[m]); m--) letters++;
        return letters != 1;
    }
}
