using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Type9.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class Phrase : Observable
{
    private string _label = "";
    private string _text = "";

    /// <summary>Короткая подпись в секторе (необязательно).</summary>
    public string Label { get => _label; set { if (Set(ref _label, value ?? "")) Raise(nameof(Caption)); } }

    /// <summary>Вставляемый текст. Поддерживает {date}, {time}, {clipboard} и переносы строк.</summary>
    public string Text { get => _text; set { if (Set(ref _text, value ?? "")) Raise(nameof(Caption)); } }

    [JsonIgnore]
    public string Caption
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label.Trim();
            var t = Text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
            return t.Trim();
        }
    }

    public Phrase() { }
    public Phrase(string text, string label = "") { _text = text; _label = label; }
}

public sealed class PhraseGroup : Observable
{
    public const int MaxPhrases = 8;
    private string _name = "";

    public string Name { get => _name; set => Set(ref _name, value ?? ""); }
    public ObservableCollection<Phrase> Phrases { get; set; } = [];

    public PhraseGroup() { }
    public PhraseGroup(string name, params Phrase[] phrases) { _name = name; Phrases = new(phrases); }
}

public sealed class Abbreviation : Observable
{
    private string _key = "";
    private string _text = "";

    public string Key { get => _key; set => Set(ref _key, (value ?? "").Trim()); }
    public string Text { get => _text; set => Set(ref _text, value ?? ""); }

    public Abbreviation() { }
    public Abbreviation(string key, string text) { _key = key; _text = text; }
}

public sealed class PhraseBook
{
    public ObservableCollection<PhraseGroup> Groups { get; set; } = [];
    public ObservableCollection<Abbreviation> Abbreviations { get; set; } = [];

    public static PhraseBook CreateDefault(AppLanguage lang) => lang == AppLanguage.English ? CreateEnglish() : CreateRussian();

    private static PhraseBook CreateEnglish() => new()
    {
        Groups =
        [
            new PhraseGroup("Greetings",
                new Phrase("Hello, "),
                new Phrase("Good morning, "),
                new Phrase("Good afternoon, "),
                new Phrase("Good evening, "),
                new Phrase("Hi! "),
                new Phrase("Thank you for reaching out! ", "Thanks for reaching out"),
                new Phrase("Hope you're doing well. ", "Hope you're well"),
                new Phrase("Здравствуйте, ")),
            new PhraseGroup("Replies",
                new Phrase("Thank you! "),
                new Phrase("Sounds good, deal. ", "Sounds good"),
                new Phrase("Got it, I'm on it. ", "On it"),
                new Phrase("Let me check and get back to you. ", "Will check"),
                new Phrase("One moment, please. ", "One moment"),
                new Phrase("Thanks for the information. ", "Thanks for the info"),
                new Phrase("Yes, that's correct. ", "Correct"),
                new Phrase("Done ✅ ", "Done")),
            new PhraseGroup("Closing",
                new Phrase("Best regards,\n", "Best regards"),
                new Phrase("Have a nice day!"),
                new Phrase("All the best!"),
                new Phrase("If you have any questions, feel free to ask.", "Any questions?"),
                new Phrase("Thanks for your patience! ", "Thanks for waiting"),
                new Phrase("Talk soon!"),
                new Phrase("Today is {date}", "Date"),
                new Phrase("{time}", "Time")),
        ],
        Abbreviations =
        [
            new Abbreviation("brb", "Be right back"),
            new Abbreviation("ty", "Thank you! "),
            new Abbreviation("br", "Best regards,\n"),
            new Abbreviation("hand", "Have a nice day!"),
        ],
    };

    private static PhraseBook CreateRussian() => new()
    {
        Groups =
        [
            new PhraseGroup("Приветствия",
                new Phrase("Здравствуйте, "),
                new Phrase("Добрый день, "),
                new Phrase("Доброе утро, "),
                new Phrase("Добрый вечер, "),
                new Phrase("Привет! "),
                new Phrase("Hello, "),
                new Phrase("Благодарю за обращение! ", "Спасибо за обращение"),
                new Phrase("Рад(а) вас слышать! ", "Рад слышать")),
            new PhraseGroup("Ответы",
                new Phrase("Спасибо! "),
                new Phrase("Хорошо, договорились. "),
                new Phrase("Принято, взял(а) в работу. ", "Взял в работу"),
                new Phrase("Уточню и вернусь с ответом. ", "Уточню и вернусь"),
                new Phrase("Минуту, пожалуйста. ", "Минуту"),
                new Phrase("Благодарю за информацию. ", "Спасибо за информацию"),
                new Phrase("Да, всё верно. ", "Всё верно"),
                new Phrase("Готово ✅ ", "Готово")),
            new PhraseGroup("Завершение",
                new Phrase("С уважением,\n", "С уважением"),
                new Phrase("Хорошего дня!"),
                new Phrase("Всего доброго!"),
                new Phrase("Если остались вопросы — пишите, буду рад(а) помочь.", "Остались вопросы?"),
                new Phrase("Спасибо за ожидание! ", "Спасибо за ожидание"),
                new Phrase("До связи!"),
                new Phrase("Сегодня {date}", "Дата"),
                new Phrase("{time}", "Время")),
        ],
        Abbreviations =
        [
            new Abbreviation("здр", "Здравствуйте, "),
            new Abbreviation("ддн", "Добрый день, "),
            new Abbreviation("спс", "Спасибо! "),
            new Abbreviation("суваж", "С уважением,\n"),
            new Abbreviation("всдоб", "Всего доброго!"),
        ],
    };
}
