using Type9.Core;
using Type9.Input;
using Type9.Prediction;
using Xunit;

namespace Type9.Tests;

public class GreetingCommaTests
{
    private static readonly Lazy<LanguageModel> Ru = new(() => LanguageModel.LoadEmbedded("ru")!);

    private static Suggestion First(string text, bool comma = true, int minPrefix = 2)
    {
        var t = new TypingTracker();
        foreach (var c in text) t.Feed(c);
        var e = new PredictionEngine { Russian = Ru.Value };
        return e.Predict(t.GetContext(), new PredictionOptions(MinPrefix: minPrefix, GreetingComma: comma))[0];
    }

    [Theory]
    [InlineData("здравствуйте", null, null, true)]
    [InlineData("привет", "всем", null, true)]
    [InlineData("день", "добрый", null, true)]
    [InlineData("утро", "доброе", "всем", true)]
    [InlineData("morning", "good", null, true)]
    [InlineData("здравствуйте", "сказал", null, false)]   // не начало фразы
    [InlineData("день", "добрый", "очень", false)]
    [InlineData("день", "рабочий", null, false)]
    [InlineData("дня", "доброго", null, false)]           // «Доброго дня!» — прощание
    public void Rules(string key, string? p1, string? p2, bool expected) =>
        Assert.Equal(expected, Greeting.NeedsComma(key, p1, p2));

    [Fact]
    public void CompletionAddsComma()
    {
        var s = First("Здравст");
        Assert.Equal("Здравствуйте,", s.Display);
        Assert.Equal("вуйте, ", s.Insert);
    }

    [Fact]
    public void SecondWordOfGreeting() => Assert.Equal("уйте, ", First("Добрый день. Здравств").Insert);

    [Fact]
    public void DisabledOption() => Assert.Equal("вуйте ", First("Здравст", comma: false).Insert);

    [Fact]
    public void NextWordAfterDobryi() => Assert.Equal("день, ", First("Добрый ", minPrefix: 0).Insert);

    [Fact]
    public void NoCommaMidSentence() => Assert.DoesNotContain(",", First("я передал здравств").Insert);
}

public class ThreeGroupDefaultsTests
{
    [Theory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.English)]
    public void ThreeGroupsOfEight(AppLanguage lang)
    {
        var book = PhraseBook.CreateDefault(lang);
        Assert.Equal(3, book.Groups.Count);
        Assert.All(book.Groups, g => Assert.Equal(PhraseGroup.MaxPhrases, g.Phrases.Count));
    }

    [Fact]
    public void UntouchedV03BookIsMigrated()
    {
        var v3 = new PhraseBook
        {
            Version = 3,
            Groups =
            [
                new PhraseGroup("Благодарность", new Phrase("Большое спасибо! "), new Phrase("Спасибо за понимание. ")),
                new PhraseGroup("Извинения", new Phrase("Уже разбираемся, скоро вернёмся с ответом. ")),
            ],
            Abbreviations = [new Abbreviation("бсп", "Большое спасибо! ")],
        };
        Assert.True(v3.IsUntouchedLegacyDefaults());

        v3.Groups[0].Phrases.Add(new Phrase("Своя фраза"));
        Assert.False(v3.IsUntouchedLegacyDefaults());
    }
}
