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

    private static PhraseBook CreateRussian() => new()
    {
        Groups =
        [
            new PhraseGroup("Приветствия",
                new Phrase("Здравствуйте, "),
                new Phrase("Добрый день, "),
                new Phrase("Доброе утро, "),
                new Phrase("Добрый вечер, "),
                new Phrase("Здравствуйте, коллеги! ", "Коллеги"),
                new Phrase("Уважаемые коллеги, ", "Уважаемые коллеги"),
                new Phrase("Добрый день! Благодарим за обращение. ", "Спасибо за обращение"),
                new Phrase("Приветствую! ")),
            new PhraseGroup("Благодарность",
                new Phrase("Спасибо! "),
                new Phrase("Благодарю за информацию. ", "За информацию"),
                new Phrase("Спасибо за оперативный ответ! ", "За быстрый ответ"),
                new Phrase("Спасибо за ожидание! ", "За ожидание"),
                new Phrase("Спасибо за терпение и понимание. ", "За понимание"),
                new Phrase("Благодарим за сотрудничество! ", "За сотрудничество"),
                new Phrase("Заранее спасибо! "),
                new Phrase("Спасибо, хорошего дня! ", "Спасибо, хорошего дня")),
            new PhraseGroup("Ответы",
                new Phrase("Принято, спасибо. ", "Принято"),
                new Phrase("Хорошо, договорились. ", "Договорились"),
                new Phrase("Да, всё верно. ", "Всё верно"),
                new Phrase("Взял(а) в работу. ", "В работе"),
                new Phrase("Уточню и вернусь с ответом. ", "Уточню и вернусь"),
                new Phrase("Минуту, пожалуйста, уточняю информацию. ", "Минуту"),
                new Phrase("Вопрос решён, проверьте, пожалуйста. ", "Вопрос решён"),
                new Phrase("Ответим в течение рабочего дня. ", "В течение дня")),
            new PhraseGroup("Просьбы",
                new Phrase("Подскажите, пожалуйста, "),
                new Phrase("Уточните, пожалуйста, "),
                new Phrase("Не могли бы вы "),
                new Phrase("Пришлите, пожалуйста, скриншот ошибки. ", "Пришлите скриншот"),
                new Phrase("Направьте, пожалуйста, документы на почту. ", "Пришлите документы"),
                new Phrase("Прошу подтвердить получение письма. ", "Подтвердите получение"),
                new Phrase("Прошу согласовать до конца недели. ", "Согласовать"),
                new Phrase("Направьте, пожалуйста, реквизиты для оплаты. ", "Реквизиты")),
            new PhraseGroup("Извинения",
                new Phrase("Извините за задержку с ответом. ", "За задержку"),
                new Phrase("Приносим извинения за доставленные неудобства. ", "За неудобства"),
                new Phrase("Прошу прощения за ожидание. ", "За ожидание"),
                new Phrase("Извините за беспокойство. ", "За беспокойство"),
                new Phrase("К сожалению, "),
                new Phrase("Сожалеем, что так получилось. Уже разбираемся. ", "Уже разбираемся")),
            new PhraseGroup("Завершение",
                new Phrase("С уважением,\n", "С уважением"),
                new Phrase("Хорошего дня!"),
                new Phrase("Всего доброго!"),
                new Phrase("Если остались вопросы — пишите, буду рад(а) помочь.", "Остались вопросы?"),
                new Phrase("Будем рады помочь! "),
                new Phrase("Хороших выходных!"),
                new Phrase("До связи!"),
                new Phrase("Буду признателен(на) за обратную связь. ", "Жду обратной связи")),
        ],
        Abbreviations =
        [
            new Abbreviation("здр", "Здравствуйте, "),
            new Abbreviation("ддн", "Добрый день, "),
            new Abbreviation("дут", "Доброе утро, "),
            new Abbreviation("двеч", "Добрый вечер, "),
            new Abbreviation("спс", "Спасибо! "),
            new Abbreviation("спсо", "Спасибо за обращение! "),
            new Abbreviation("пжл", "пожалуйста"),
            new Abbreviation("пдск", "Подскажите, пожалуйста, "),
            new Abbreviation("утч", "Уточните, пожалуйста, "),
            new Abbreviation("изв", "Извините за задержку с ответом. "),
            new Abbreviation("суваж", "С уважением,\n"),
            new Abbreviation("хд", "Хорошего дня!"),
            new Abbreviation("всдоб", "Всего доброго!"),
        ],
    };

    private static PhraseBook CreateEnglish() => new()
    {
        Groups =
        [
            new PhraseGroup("Greetings",
                new Phrase("Hello, "),
                new Phrase("Good morning, "),
                new Phrase("Good afternoon, "),
                new Phrase("Good evening, "),
                new Phrase("Dear colleagues, ", "Dear colleagues"),
                new Phrase("Hi team, ", "Hi team"),
                new Phrase("Thank you for reaching out! ", "Thanks for reaching out"),
                new Phrase("I hope you are doing well. ", "Hope you're well")),
            new PhraseGroup("Thanks",
                new Phrase("Thank you! "),
                new Phrase("Thanks for the information. ", "For the info"),
                new Phrase("Thank you for your quick reply! ", "Quick reply"),
                new Phrase("Thank you for your patience. ", "Patience"),
                new Phrase("Thank you for your understanding. ", "Understanding"),
                new Phrase("Thanks in advance! "),
                new Phrase("I appreciate your help. ", "Appreciate it")),
            new PhraseGroup("Replies",
                new Phrase("Noted, thank you. ", "Noted"),
                new Phrase("Sounds good. "),
                new Phrase("Yes, that is correct. ", "Correct"),
                new Phrase("Got it, I'm on it. ", "On it"),
                new Phrase("Let me check and get back to you. ", "Will check"),
                new Phrase("One moment, please. ", "One moment"),
                new Phrase("The issue has been resolved, please check. ", "Resolved"),
                new Phrase("We will reply within one business day. ", "Within a day")),
            new PhraseGroup("Requests",
                new Phrase("Could you please "),
                new Phrase("Could you please clarify "),
                new Phrase("Please send me a screenshot of the error. ", "Send a screenshot"),
                new Phrase("Please find attached ", "Attached"),
                new Phrase("Please confirm receipt of this email. ", "Confirm receipt"),
                new Phrase("Please let me know your availability. ", "Availability")),
            new PhraseGroup("Apologies",
                new Phrase("Sorry for the delay in getting back to you. ", "Sorry for the delay"),
                new Phrase("We apologize for the inconvenience. ", "Inconvenience"),
                new Phrase("Unfortunately, "),
                new Phrase("Sorry to bother you. ", "Sorry to bother")),
            new PhraseGroup("Closing",
                new Phrase("Best regards,\n", "Best regards"),
                new Phrase("Kind regards,\n", "Kind regards"),
                new Phrase("Have a great day!"),
                new Phrase("Have a nice weekend!"),
                new Phrase("If you have any questions, feel free to reach out.", "Any questions?"),
                new Phrase("Looking forward to hearing from you.", "Looking forward"),
                new Phrase("Talk soon!")),
        ],
        Abbreviations =
        [
            new Abbreviation("gm", "Good morning, "),
            new Abbreviation("ty", "Thank you! "),
            new Abbreviation("tia", "Thanks in advance! "),
            new Abbreviation("pls", "please"),
            new Abbreviation("br", "Best regards,\n"),
            new Abbreviation("hagd", "Have a great day!"),
        ],
    };

    /// <summary>Заменяет содержимое стандартным набором (кнопка «Стандартные фразы»).</summary>
    public void ResetTo(PhraseBook defaults)
    {
        Groups.Clear();
        foreach (var g in defaults.Groups) Groups.Add(g);
        Abbreviations.Clear();
        foreach (var a in defaults.Abbreviations) Abbreviations.Add(a);
    }
}