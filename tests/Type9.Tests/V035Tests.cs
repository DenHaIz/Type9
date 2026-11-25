using Type9.Input;
using Type9.Prediction;
using Xunit;

namespace Type9.Tests;

public class TypoCorrectionTests
{
    private static readonly Lazy<PredictionEngine> Engine = new(() => new PredictionEngine
    {
        Russian = LanguageModel.LoadEmbedded("ru"),
        Supplement = BaseDictionary.LoadEmbedded("ru.business"),
    });

    private static IReadOnlyList<Suggestion> Predict(string tail, PredictionOptions? o = null)
    {
        var t = new TypingTracker();
        foreach (var c in tail) t.Feed(c);
        return Engine.Value.Predict(t.GetContext(), o ?? new PredictionOptions());
    }

    [Theory]
    [InlineData("Приет", "Привет,")]
    [InlineData("пожалуйта", "пожалуйста")]
    [InlineData("докумнеты", "документы")]
    [InlineData("Здраствуйте", "Здравствуйте,")]
    [InlineData("контрагнет", "контрагент")]   // слово делового словаря
    public void FixesTypoInWord(string typed, string expected)
    {
        var r = Predict(typed);
        Assert.Equal(SuggestionKind.Correction, r[0].Kind);
        Assert.Equal(expected, r[0].Display);
        Assert.Equal(typed.Length, r[0].Backspaces);
    }

    [Fact]
    public void OffersExpectedFixAmongVariants() =>
        Assert.Contains(Predict("Выедал"), s => s.Kind == SuggestionKind.Correction && s.Display == "Выехал");

    [Fact]
    public void FixesPreviousWordRightAfterSpace()
    {
        var r = Predict("Приет ");
        Assert.Equal(SuggestionKind.Correction, r[0].Kind);
        Assert.Equal("Привет, ", r[0].Insert);
        Assert.Equal("Приет ".Length, r[0].Backspaces);
    }

    [Theory]
    [InlineData("мена")]        // короткое настоящее слово
    [InlineData("документы")]   // правильное слово
    [InlineData("прие")]        // начало частого слова
    [InlineData("контраг")]     // начало слова из делового словаря
    public void LeavesCorrectWordsAlone(string typed) =>
        Assert.DoesNotContain(Predict(typed), s => s.Kind == SuggestionKind.Correction);

    [Fact]
    public void CanBeTurnedOff() =>
        Assert.DoesNotContain(Predict("Приет", new PredictionOptions(Corrections: false)), s => s.Kind == SuggestionKind.Correction);

    [Fact]
    public void NeverSuggestsObscenities()
    {
        Assert.DoesNotContain(Predict("выеда"), s => Profanity.IsObscene(CaseHelper.Normalize(s.Display)));
        Assert.True(Profanity.IsObscene("выебать"));
        Assert.False(Profanity.IsObscene("мандарин"));
        Assert.False(Profanity.IsObscene("небо"));
        Assert.False(Profanity.IsObscene("похудеть"));
    }

    [Theory]
    [InlineData('л', 'д', true)]
    [InlineData('д', 'х', false)]
    [InlineData('g', 't', true)]
    [InlineData('g', 'r', false)]
    public void KeyboardNeighbours(char a, char b, bool expected) => Assert.Equal(expected, Corrector.Adjacent(a, b));
}

public class CasingTests
{
    private static readonly Lazy<PredictionEngine> Engine = new(() => new PredictionEngine { Russian = LanguageModel.LoadEmbedded("ru") });

    private static Suggestion First(string tail, bool autoCap = true)
    {
        var t = new TypingTracker();
        foreach (var c in tail) t.Feed(c);
        return Engine.Value.Predict(t.GetContext(), new PredictionOptions(AutoCapitalize: autoCap))[0];
    }

    [Fact]
    public void CapsTypedPrefixGivesNormalWordReplacingWhole()
    {
        var s = First("ДОБ");
        Assert.Equal("Доб", s.Typed);
        Assert.DoesNotContain(s.Display, c => char.IsUpper(c) && c != 'Д');
        Assert.Equal(3, s.Backspaces);
        Assert.StartsWith("Доб", s.Insert);
    }

    [Fact]
    public void CapitalizesAfterSentenceEnd()
    {
        var s = First("Привет. доб");
        Assert.StartsWith("Доб", s.Insert);
        Assert.Equal(3, s.Backspaces);
    }

    [Theory]
    [InlineData("т.е. доб")]     // сокращение, а не конец предложения
    [InlineData("доб")]          // после клика неизвестно, что левее
    [InlineData("и доб")]
    public void KeepsLowercaseMidSentence(string tail)
    {
        var s = First(tail);
        Assert.Equal(0, s.Backspaces);
        Assert.True(char.IsLower(s.Display[0]));
    }

    [Fact]
    public void AutoCapitalizeCanBeTurnedOff() => Assert.Equal(0, First("Привет. доб", autoCap: false).Backspaces);

    [Fact]
    public void SentenceStartFlag()
    {
        TypingContext Ctx(string tail) { var t = new TypingTracker(); foreach (var c in tail) t.Feed(c); return t.GetContext(); }
        Assert.True(Ctx("Да! нет").SentenceStart);
        Assert.True(Ctx("Да\nнет").SentenceStart);
        Assert.False(Ctx("site.ru").SentenceStart);
        Assert.False(Ctx("т.е. да").SentenceStart);
        Assert.Equal(", ", Ctx("Привет, ").Gap);
    }
}
