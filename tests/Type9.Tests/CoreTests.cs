using Type9.Core;
using Type9.Input;
using Type9.Prediction;
using Type9.UI;
using Xunit;

namespace Type9.Tests;

public class TypingTrackerTests
{
    private static TypingTracker Typed(string text)
    {
        var t = new TypingTracker();
        foreach (var c in text) t.Feed(c);
        return t;
    }

    [Fact]
    public void CurrentWordAndPreviousWords()
    {
        var ctx = Typed("Добрый день, как де").GetContext();
        Assert.Equal("де", ctx.Word);
        Assert.Equal("как", ctx.Prev1);
        Assert.Equal("день", ctx.Prev2);
    }

    [Fact]
    public void SentenceEndClearsContext()
    {
        var ctx = Typed("Спасибо. Хор").GetContext();
        Assert.Equal("Хор", ctx.Word);
        Assert.Null(ctx.Prev1);
    }

    [Fact]
    public void SeparatorCompletesWord()
    {
        var t = Typed("добрый ден");
        var done = t.Feed('ь');
        Assert.Null(done);
        done = t.Feed(' ');
        Assert.NotNull(done);
        Assert.Equal("день", done!.Value.Word);
        Assert.Equal("добрый", done.Value.Prev1);
    }

    [Fact]
    public void BackspaceEditsWord()
    {
        var t = Typed("привет");
        t.Backspace();
        t.Backspace();
        Assert.Equal("прив", t.GetContext().Word);
    }

    [Fact]
    public void HyphenatedWordsStayTogether()
    {
        Assert.Equal("кто-то", Typed("кто-то").GetContext().Word);
    }

    [Fact]
    public void LeadingSpaceNeededAfterComma()
    {
        var ctx = Typed("день,").GetContext();
        Assert.Equal("", ctx.Word);
        Assert.True(ctx.NeedsLeadingSpace);
        Assert.False(Typed("день, ").GetContext().NeedsLeadingSpace);
    }
}

public class CaseHelperTests
{
    [Theory]
    [InlineData("Здр", "здравствуйте", "Здравствуйте")]
    [InlineData("ЗДР", "здравствуйте", "ЗДРАВСТВУЙТЕ")]
    [InlineData("здр", "здравствуйте", "здравствуйте")]
    [InlineData("iph", "iPhone", "iPhone")]
    public void AppliesCase(string typed, string candidate, string expected) =>
        Assert.Equal(expected, CaseHelper.Apply(typed, candidate));

    [Fact]
    public void CompletionInsertsOnlySuffix()
    {
        var s = PredictionEngine.MakeCompletion("Здр", "здравствуйте", " ");
        Assert.Equal("авствуйте ", s.Insert);
        Assert.Equal(0, s.Backspaces);
    }

    [Fact]
    public void CompletionReplacesWordWhenPrefixDiffers()
    {
        var s = PredictionEngine.MakeCompletion("еще", "ещё", "");
        Assert.Equal("ещё", s.Insert);
        Assert.Equal(3, s.Backspaces);
    }
}

public class PredictionTests
{
    private static PredictionEngine Engine()
    {
        var e = new PredictionEngine
        {
            Base = BaseDictionary.Build([["здравствуйте", "здание", "здоровье", "добрый", "день"]]),
        };
        e.User.Train("Добрый день, коллеги. Добрый день, коллеги. Добрый день, коллеги.");
        return e;
    }

    [Fact]
    public void CompletesFromBaseDictionary()
    {
        var r = Engine().Predict(new TypingContext("здр", null, null, false), new PredictionOptions());
        Assert.Contains(r, s => s.Display == "здравствуйте");
    }

    [Fact]
    public void LearnedWordsWinOverBase()
    {
        var e = Engine();
        e.User.Train("здание здание здание здание");
        var r = e.Predict(new TypingContext("зд", null, null, false), new PredictionOptions());
        Assert.Equal("здание", r[0].Display);
    }

    [Fact]
    public void PredictsNextWordFromBigrams()
    {
        var r = Engine().Predict(new TypingContext("", "день", "добрый", false), new PredictionOptions());
        Assert.Equal("коллеги", r[0].Rest);
        Assert.Equal(SuggestionKind.NextWord, r[0].Kind);
    }

    [Fact]
    public void AbbreviationReplacesTypedText()
    {
        var e = Engine();
        e.Abbreviations = [new Abbreviation("здр", "Здравствуйте, ")];
        var r = e.Predict(new TypingContext("здр", null, null, false), new PredictionOptions());
        Assert.Equal(SuggestionKind.Abbreviation, r[0].Kind);
        Assert.Equal("Здравствуйте, ", r[0].Insert);
        Assert.Equal(3, r[0].Backspaces);
    }

    [Fact]
    public void UserModelRoundTrips()
    {
        var e = Engine();
        var copy = UserModel.Parse(e.User.Serialize());
        Assert.Equal(e.User.WordCount, copy.WordCount);
        Assert.Equal(e.User.Bigram("добрый", "день"), copy.Bigram("добрый", "день"));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Q")]
    [InlineData("Ctrl+Alt+P")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Mouse4")]
    [InlineData("Win+Space")]
    public void RoundTrips(string text) => Assert.Equal(text, Hotkey.Parse(text).ToString());

    [Fact]
    public void MatchesExactModifiers()
    {
        var h = Hotkey.Parse("Alt+Q");
        Assert.True(h.Matches('Q', HotkeyModifiers.Alt));
        Assert.False(h.Matches('Q', HotkeyModifiers.Alt | HotkeyModifiers.Ctrl));
        Assert.False(h.Matches('W', HotkeyModifiers.Alt));
    }
}

public class RadialGeometryTests
{
    [Theory]
    [InlineData(0, -100, 8, 0)]   // вверх — первый сектор
    [InlineData(100, 0, 8, 2)]    // вправо — третий (90°)
    [InlineData(0, 100, 8, 4)]    // вниз
    [InlineData(-100, 0, 8, 6)]   // влево
    [InlineData(0, 100, 2, 1)]
    [InlineData(5, 5, 8, -1)]     // мёртвая зона
    public void IndexFromVector(double dx, double dy, int count, int expected) =>
        Assert.Equal(expected, RadialGeometry.IndexFromVector(dx, dy, count, 30));
}

public class PlaceholderTests
{
    [Fact]
    public void ExpandsClipboard()
    {
        Placeholders.ClipboardReader = () => "XYZ";
        Assert.Equal("a XYZ b", Placeholders.Expand("a {clipboard} b"));
    }
}
