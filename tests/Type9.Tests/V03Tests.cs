using System.Text.Json;
using Type9.Core;
using Type9.Input;
using Type9.Native;
using Type9.UI;
using Xunit;

namespace Type9.Tests;

public class SingleKeyHotkeyTests
{
    [Theory]
    [InlineData("RCtrl")]
    [InlineData("RAlt")]
    [InlineData("CapsLock")]
    [InlineData("F8")]
    [InlineData("Mouse5")]
    public void RoundTrips(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Fact]
    public void RightCtrlIsSingleModifier()
    {
        var h = Hotkey.Parse("RCtrl");
        Assert.True(h.IsSingleModifier);
        Assert.True(h.Matches(Win32.VK_RCONTROL, HotkeyModifiers.None));
        Assert.True(h.Matches(Win32.VK_RCONTROL, HotkeyModifiers.Ctrl));   // собственный Ctrl не мешает
        Assert.False(h.Matches(Win32.VK_RCONTROL, HotkeyModifiers.Shift)); // а чужой — да
        Assert.False(h.Matches(Win32.VK_LCONTROL, HotkeyModifiers.None));
    }

    [Theory]
    [InlineData("Ctrl+C", true)]
    [InlineData("RCtrl", true)]
    [InlineData("F5", true)]
    [InlineData("CapsLock", true)]
    [InlineData("Mouse4", true)]
    [InlineData("Alt+Q", false)]
    [InlineData("F9", false)]
    [InlineData("Ctrl+Alt+P", false)]
    public void WarnsAboutConflicts(string hotkey, bool warns) =>
        Assert.Equal(warns, HotkeyAdvisor.Check(Hotkey.Parse(hotkey)) is not null);
}

public class LearningFilterTests
{
    private static List<CompletedWord> Words(string text)
    {
        var t = new TypingTracker();
        var list = new List<CompletedWord>();
        foreach (var c in text + " ") if (t.Feed(c) is { } w) list.Add(w);
        return list;
    }

    [Fact]
    public void SkipsAddressesAndMixedScripts()
    {
        var words = Words("пишите на user@mail почта site.ru и прgивет");
        Assert.True(words.Single(w => w.Word == "пишите").Learnable);
        Assert.False(words.Single(w => w.Word == "user").Learnable);
        Assert.False(words.Single(w => w.Word == "mail").Learnable);
        Assert.False(words.Single(w => w.Word == "ru").Learnable);
        Assert.False(words.Single(w => w.Word == "прgивет").Learnable);
    }

    [Fact]
    public void PositionFollowsEditing()
    {
        var t = new TypingTracker();
        foreach (var c in "абв") t.Feed(c);
        t.Backspace();
        Assert.Equal(2, t.Position);
    }
}

public class RadialPlacementTests
{
    private static readonly Win32.RECT Work = new() { Left = 1920, Top = 0, Right = 3840, Bottom = 1040 }; // второй монитор

    [Fact]
    public void StaysOnMonitorNearEdge()
    {
        var p = RadialMenuWindow.Place(new Win32.POINT(3830, 1030), 500, Work, null, 1.0);
        Assert.InRange(p.X + 500, Work.Right, Work.Right + 30);   // упирается в правый край (только поле тени за краем)
        Assert.InRange(p.Y + 500, Work.Bottom, Work.Bottom + 30);
        Assert.True(p.X >= Work.Left);
    }

    [Fact]
    public void DoesNotCoverCaretLine()
    {
        var caret = new Win32.RECT { Left = 2500, Top = 500, Right = 2501, Bottom = 520 };
        var p = RadialMenuWindow.Place(new Win32.POINT(2500, 505), 500, Work, caret, 1.0);
        int inset = 18;
        bool above = p.Y + 500 - inset <= caret.Top;
        bool below = p.Y + inset >= caret.Bottom;
        Assert.True(above || below);
    }
}

public class DefaultPhrasesTests
{
    [Fact]
    public void RussianDefaultsHaveNoLatinOrEmoji()
    {
        var book = PhraseBook.CreateDefault(AppLanguage.Russian);
        foreach (var text in book.Groups.SelectMany(g => g.Phrases).SelectMany(p => new[] { p.Text, p.Label })
                     .Concat(book.Abbreviations.Select(a => a.Text)))
        {
            var withoutPlaceholders = text.Replace("{date}", "").Replace("{time}", "").Replace("{clipboard}", "");
            Assert.DoesNotMatch("[A-Za-z]", withoutPlaceholders);
            Assert.DoesNotContain(withoutPlaceholders, ch => char.IsSurrogate(ch) || ch is >= '☀' and <= '➿');
            Assert.DoesNotContain("(а)", text);
        }
    }

    [Fact]
    public void RecognizesUntouchedLegacyBook()
    {
        var legacy = new PhraseBook
        {
            Groups = [new PhraseGroup("Приветствия", new Phrase("Здравствуйте, "), new Phrase("Hello, "))],
            Abbreviations = [new Abbreviation("спс", "Спасибо! ")],
        };
        Assert.True(legacy.IsUntouchedLegacyDefaults());

        legacy.Groups[0].Phrases.Add(new Phrase("Моя фраза"));
        Assert.False(legacy.IsUntouchedLegacyDefaults());
        Assert.False(PhraseBook.CreateDefault(AppLanguage.Russian).IsUntouchedLegacyDefaults());
    }

    [Fact]
    public void VersionSurvivesSerialization()
    {
        var json = JsonSerializer.Serialize(PhraseBook.CreateDefault(AppLanguage.Russian), JsonCtx.Default.PhraseBook);
        Assert.Equal(PhraseBook.CurrentDefaultsVersion, JsonSerializer.Deserialize(json, JsonCtx.Default.PhraseBook)!.Version);
    }
}

public class ImportValidationTests
{
    [Fact]
    public void RejectsBadRowsAndNormalizesKeys()
    {
        var m = new Type9.Prediction.UserModel();
        m.Merge("W\tWRONGKEY\tЁлка\t3\t0\nW\tx\t\t5\t0\nW\tдом\tдом\t-7\t0\nB\tДобрый\tДень\t2\n");
        Assert.Equal(1, m.WordCount);                    // только «Ёлка»
        Assert.NotNull(m.Get("елка"));                   // ключ пересчитан из формы
        Assert.Equal(3, m.TotalCount);                   // отрицательный счётчик отброшен
        Assert.Equal(2, m.Bigram("добрый", "день"));     // ключи биграмм нормализованы
    }
}