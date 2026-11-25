using Type9.Input;
using Type9.Prediction;
using Xunit;

namespace Type9.Tests;

/// <summary>Личный словарь не запоминает случайный капс.</summary>
public class LearnedCaseTests
{
    private static string? Known(string key) => key switch
    {
        "через" => "через",
        "это" => "это",
        "инн" => "ИНН",
        _ => null,
    };

    [Theory]
    [InlineData("ЧЕРЕЗ", "через")]   // слово словаря капсом — CapsLock
    [InlineData("дА", "Да")]         // CapsLock + Shift на первой букве
    [InlineData("dEEZI", "Deezi")]
    [InlineData("инн", "инн")]
    [InlineData("ИНН", "ИНН")]       // аббревиатура из словаря
    [InlineData("СНИЛС", "СНИЛС")]   // короткое незнакомое — похоже на аббревиатуру
    [InlineData("КОМПЬЮТЕРНЫЙ", "компьютерный")]
    [InlineData("iPhone", "iPhone")]
    [InlineData("Москва", "Москва")]
    public void CleansAccidentalCaps(string typed, string expected) =>
        Assert.Equal(expected, CaseHelper.CleanCase(typed, Known));

    [Fact]
    public void LearnStoresNormalForm()
    {
        var m = new UserModel { KnownForm = Known };
        m.Train("Я пишу ЧЕРЕЗ сервис, и ЭТО удобно. ");
        Assert.Equal("через", m.Get("через")!.Form);
        Assert.Equal("это", m.Get("это")!.Form);
    }

    [Fact]
    public void FixFormsRepairsOldDictionary()
    {
        var m = UserModel.Parse("#type9-userdict v1\nW\tчерез\tЧЕРЕЗ\t14\t0\nW\tда\tдА\t11\t0\nW\tинн\tИНН\t3\t0\n");
        m.KnownForm = Known;
        Assert.Equal(2, m.FixForms());
        Assert.Equal("через", m.Get("через")!.Form);
        Assert.Equal("Да", m.Get("да")!.Form);
        Assert.Equal("ИНН", m.Get("инн")!.Form);
    }

    [Fact]
    public void LowercaseUseReplacesCapsForm()
    {
        var m = new UserModel { KnownForm = Known };
        m.Train("пишу ЧЕРЕЗ сервис. пишу через сервис. ");
        Assert.Equal("через", m.Get("через")!.Form);
    }

    [Fact]
    public void InvertedPrefixStillCapitalizes() =>
        Assert.Equal("Привет", CaseHelper.Apply("пРИВ", "привет"));
}

public class LearningTests
{
    [Fact]
    public void TentativeUnknownWordIsNotLearned()
    {
        var m = new UserModel { KnownForm = k => k == "договор" ? "договор" : null };
        long now = UserModel.NowMinutes();
        m.Learn(new CompletedWord("догов", null, null, false, Tentative: true), now);     // оборвано кликом
        m.Learn(new CompletedWord("договор", null, null, false, Tentative: true), now);   // известное слово
        Assert.Null(m.Get("догов"));
        Assert.Equal(1, m.Get("договор")!.Count);
    }

    [Fact]
    public void OwnNewWordIsSuggestedAfterFirstUse()
    {
        var e = new PredictionEngine { Russian = LanguageModel.LoadEmbedded("ru") };
        e.User.Train("Сегодня обновим Битрикс24 и Мегаплан. ");
        var t = new TypingTracker();
        foreach (var c in "мегап") t.Feed(c);
        Assert.Contains(e.Predict(t.GetContext(), new PredictionOptions()), s => s.Insert.TrimEnd() == "Мегаплан");
    }
}

public class CorrectionOrderTests
{
    private static readonly Lazy<PredictionEngine> Engine = new(() => new PredictionEngine
    {
        Russian = LanguageModel.LoadEmbedded("ru"),
        Supplement = BaseDictionary.LoadEmbedded("ru.business"),
    });

    private static IReadOnlyList<Suggestion> Predict(string tail, int max = 3)
    {
        var t = new TypingTracker();
        foreach (var c in tail) t.Feed(c);
        return Engine.Value.Predict(t.GetContext(), new PredictionOptions(MaxResults: max));
    }

    [Theory]
    [InlineData("докумне")]
    [InlineData("пожалуйт")]
    [InlineData("Приет")]
    public void CorrectionFirstAndCountKept(string tail)
    {
        for (int max = 1; max <= 5; max++)
        {
            var r = Predict(tail, max);
            Assert.InRange(r.Count, 1, max);
            Assert.Equal(SuggestionKind.Correction, r[0].Kind);
        }
    }

    [Fact]
    public void CompletionsFillTheRest()
    {
        // Правильно набранное начало слова: только дополнения, без «исправлений»
        var r = Predict("инвентаризаци");
        Assert.DoesNotContain(r, s => s.Kind == SuggestionKind.Correction);
        Assert.True(r.Count >= 2);
    }

    [Fact]
    public void AbbreviationKeepsCaseFromBusinessDictionary()
    {
        Assert.Equal("ИНН", Engine.Value.KnownForm("инн"));
        Assert.Equal("ООО", Engine.Value.KnownForm("ооо"));
        Assert.Equal("через", Engine.Value.KnownForm("через"));
    }
}
