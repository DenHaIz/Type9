using Type9.Core;
using Type9.Input;
using Type9.Prediction;
using Xunit;
using Xunit.Abstractions;

namespace Type9.Tests;

/// <summary>Проверки на встроенных языковых моделях (реальные данные).</summary>
public class ModelTests(ITestOutputHelper output)
{
    private static readonly Lazy<LanguageModel> Ru = new(() => LanguageModel.LoadEmbedded("ru")!);
    private static readonly Lazy<LanguageModel> En = new(() => LanguageModel.LoadEmbedded("en")!);

    private static PredictionEngine Engine(SuggestLanguage lang = SuggestLanguage.Russian) => new()
    {
        Russian = lang != SuggestLanguage.English ? Ru.Value : null,
        English = lang != SuggestLanguage.Russian ? En.Value : null,
    };

    private static TypingContext Ctx(string text)
    {
        var t = new TypingTracker();
        foreach (var c in text) t.Feed(c);
        return t.GetContext();
    }

    private static IReadOnlyList<string> Words(PredictionEngine e, string text, int minPrefix = 1, SuggestLanguage lang = SuggestLanguage.Russian) =>
        e.Predict(Ctx(text), new PredictionOptions(MinPrefix: minPrefix, MaxResults: 3, AddSpace: false, Language: lang))
            .Select(s => s.Display).ToList();

    [Fact]
    public void ModelsLoad()
    {
        Assert.True(Ru.Value.Count > 100_000);
        Assert.True(En.Value.Count > 50_000);
    }

    [Fact]
    public void CaseAgreementAfterAdjective() =>
        Assert.Equal("месте", Words(Engine(), "привет как дела что думаешь о этом мес")[0]);

    [Theory]
    [InlineData("я живу в мос", "москве")]
    [InlineData("спасибо за пом", "помощь")]
    [InlineData("добрый д", "день")]
    [InlineData("с уваж", "уважением")]
    public void ContextCompletions(string text, string expected) =>
        Assert.Contains(expected, Words(Engine(), text));

    [Fact]
    public void NextWordAfterSpace() =>
        Assert.Contains("день", Words(Engine(), "добрый ", minPrefix: 0));

    [Fact]
    public void RussianModeHidesEnglish()
    {
        Assert.Empty(Words(Engine(SuggestLanguage.Both), "hel", lang: SuggestLanguage.Russian));
        Assert.NotEmpty(Words(Engine(SuggestLanguage.Both), "hel", lang: SuggestLanguage.English));
    }

    [Fact]
    public void PredictionIsFast()
    {
        var e = Engine();
        var ctxs = new[] { "п", "по", "спасибо за о", "в этом мес", "добрый ", "я не з", "с" }.Select(Ctx).ToArray();
        var opt = new PredictionOptions(MinPrefix: 0);
        foreach (var c in ctxs) e.Predict(c, opt); // прогрев
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        for (int r = 0; r < 30; r++)
            foreach (var c in ctxs) { e.Predict(c, opt); n++; }
        double ms = sw.Elapsed.TotalMilliseconds / n;
        output.WriteLine($"среднее время подсказки: {ms:F2} мс");
        Assert.True(ms < 15, $"{ms:F2} мс");
    }

    [Theory]
    [InlineData("v0.2", "0.2.0")]
    [InlineData("v1.4.2", "1.4.2")]
    [InlineData("0.1", "0.1.0")]
    public void ParsesReleaseTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), Updater.ParseTag(tag));

    /// <summary>Не проверка, а отчёт для ручной оценки качества (вывод теста).</summary>
    [Fact]
    public void Report()
    {
        var e = Engine();
        string[] samples =
        [
            "привет как дела что думаешь о этом мес", "я живу в мос", "мы поговорили с колл", "заявка принята в раб",
            "спасибо за обра", "у меня нет вре", "давай встретимся завтра в", "я не зн", "по этому воп",
            "добрый ", "спасибо за ", "я хочу ", "в течение ", "не могли бы вы ", "с уважением", "хорошего ",
        ];
        foreach (var s in samples)
            output.WriteLine($"{s,-45} → {string.Join(" | ", Words(e, s, minPrefix: 0))}");
    }
}
