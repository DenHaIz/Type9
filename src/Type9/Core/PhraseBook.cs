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
    /// <summary>Версия стандартного набора, из которого создан файл (0 — старые версии программы).</summary>
    public int Version { get; set; }
    public const int CurrentDefaultsVersion = 4;

    public ObservableCollection<PhraseGroup> Groups { get; set; } = [];
    public ObservableCollection<Abbreviation> Abbreviations { get; set; } = [];

    private static readonly HashSet<string> LegacyPhraseTexts = ["{time}", "All the best!", "Best regards,\n", "Could you please ", "Could you please clarify ", "Dear colleagues, ", "Done ✅ ", "Good afternoon, ", "Good evening, ", "Good morning, ", "Got it, I'm on it. ", "Have a great day!", "Have a nice day!", "Have a nice weekend!", "Hello, ", "Hi team, ", "Hi! ", "Hope you're doing well. ", "I appreciate your help. ", "I hope you are doing well. ", "If you have any questions, feel free to ask.", "If you have any questions, feel free to reach out.", "Kind regards,\n", "Let me check and get back to you. ", "Looking forward to hearing from you.", "Noted, thank you. ", "One moment, please. ", "Please confirm receipt of this email. ", "Please find attached ", "Please let me know your availability. ", "Please send me a screenshot of the error. ", "Sorry for the delay in getting back to you. ", "Sorry to bother you. ", "Sounds good, deal. ", "Sounds good. ", "Talk soon!", "Thank you for reaching out! ", "Thank you for your patience. ", "Thank you for your quick reply! ", "Thank you for your understanding. ", "Thank you! ", "Thanks for the information. ", "Thanks for your patience! ", "Thanks in advance! ", "The issue has been resolved, please check. ", "Today is {date}", "Unfortunately, ", "We apologize for the inconvenience. ", "We will reply within one business day. ", "Yes, that is correct. ", "Yes, that's correct. ", "Благодарим за сотрудничество! ", "Благодарю за информацию. ", "Благодарю за обращение! ", "Будем рады помочь! ", "Буду признателен(на) за обратную связь. ", "Взял(а) в работу. ", "Вопрос решён, проверьте, пожалуйста. ", "Всего доброго!", "Готово ✅ ", "Да, всё верно. ", "До связи!", "Доброе утро, ", "Добрый вечер, ", "Добрый день! Благодарим за обращение. ", "Добрый день, ", "Если остались вопросы — пишите, буду рад(а) помочь.", "Заранее спасибо! ", "Здравствуйте, ", "Здравствуйте, коллеги! ", "Извините за беспокойство. ", "Извините за задержку с ответом. ", "К сожалению, ", "Минуту, пожалуйста, уточняю информацию. ", "Минуту, пожалуйста. ", "Направьте, пожалуйста, документы на почту. ", "Направьте, пожалуйста, реквизиты для оплаты. ", "Не могли бы вы ", "Ответим в течение рабочего дня. ", "Подскажите, пожалуйста, ", "Привет! ", "Приветствую! ", "Приносим извинения за доставленные неудобства. ", "Принято, взял(а) в работу. ", "Принято, спасибо. ", "Пришлите, пожалуйста, скриншот ошибки. ", "Прошу подтвердить получение письма. ", "Прошу прощения за ожидание. ", "Прошу согласовать до конца недели. ", "Рад(а) вас слышать! ", "С уважением,\n", "Сегодня {date}", "Сожалеем, что так получилось. Уже разбираемся. ", "Спасибо за ожидание! ", "Спасибо за оперативный ответ! ", "Спасибо за терпение и понимание. ", "Спасибо! ", "Спасибо, хорошего дня! ", "Уважаемые коллеги, ", "Уточните, пожалуйста, ", "Уточню и вернусь с ответом. ", "Хорошего дня!", "Хороших выходных!", "Хорошо, договорились. "];
    // Набор v0.3 (6 групп) — тоже «старый стандартный»: если его не меняли, заменяется набором из 3 групп
    private static readonly HashSet<string> V3PhraseTexts = ["Большое спасибо! ", "Будем рады помочь!", "Готово, проверьте, пожалуйста. ", "Добрый день, коллеги! ", "Если появятся вопросы — пишите, с радостью поможем.", "Ждём вашего ответа. ", "Здравствуйте! Спасибо, что обратились к нам. ", "Извините за ожидание. ", "Одну минуту, проверяю. ", "Подтвердите, пожалуйста, получение письма. ", "Приносим извинения за неудобства. ", "Принято в работу. ", "Пришлите, пожалуйста, документы на почту. ", "Сейчас уточню и вернусь с ответом. ", "Сообщите, пожалуйста, удобное время для звонка. ", "Спасибо за быстрый ответ! ", "Спасибо за информацию. ", "Спасибо за ожидание. ", "Спасибо за понимание. ", "Уже разбираемся, скоро вернёмся с ответом. "];
    private static readonly HashSet<string> LegacyAbbreviationTexts = ["Большое спасибо! ", "Be right back", "Best regards,\n", "Good morning, ", "Have a great day!", "Have a nice day!", "please", "Thank you! ", "Thanks in advance! ", "Всего доброго!", "Доброе утро, ", "Добрый вечер, ", "Добрый день, ", "Здравствуйте, ", "Извините за задержку с ответом. ", "Подскажите, пожалуйста, ", "пожалуйста", "С уважением,\n", "Спасибо за обращение! ", "Спасибо! ", "Уточните, пожалуйста, ", "Хорошего дня!"];
    private static readonly HashSet<string> LegacyGroupNames = ["Apologies", "Closing", "Greetings", "Replies", "Requests", "Thanks", "Благодарность", "Завершение", "Извинения", "Ответы", "Приветствия", "Просьбы"];


    /// <summary>
    /// Набор из прошлых версий, который пользователь не менял (только стандартные тексты, допускаются удаления
    /// и перестановки), — его можно молча заменить новым стандартным набором.
    /// </summary>
    public bool IsUntouchedLegacyDefaults() =>
        Version < CurrentDefaultsVersion &&
        Groups.All(g => LegacyGroupNames.Contains(g.Name) && g.Phrases.All(p => LegacyPhraseTexts.Contains(p.Text) || V3PhraseTexts.Contains(p.Text))) &&
        Abbreviations.All(a => LegacyAbbreviationTexts.Contains(a.Text));

    public static PhraseBook CreateDefault(AppLanguage lang) => lang == AppLanguage.English ? CreateEnglish() : CreateRussian();

    private static PhraseBook CreateRussian() => new()
    {
        Version = CurrentDefaultsVersion,
        Groups =
        [
            new PhraseGroup("Приветствия",
                new Phrase("Здравствуйте, "),
                new Phrase("Добрый день, "),
                new Phrase("Доброе утро, "),
                new Phrase("Добрый вечер, "),
                new Phrase("Здравствуйте, коллеги! "),
                new Phrase("Добрый день, коллеги! "),
                new Phrase("Здравствуйте! Спасибо, что обратились к нам. ", "Спасибо, что обратились"),
                new Phrase("Приветствую! ")),
            new PhraseGroup("Ответы",
                new Phrase("Спасибо! "),
                new Phrase("Спасибо за информацию. "),
                new Phrase("Хорошо, договорились. "),
                new Phrase("Да, всё верно. "),
                new Phrase("Принято в работу. "),
                new Phrase("Сейчас уточню и вернусь с ответом. ", "Уточню и вернусь"),
                new Phrase("Подскажите, пожалуйста, "),
                new Phrase("Извините за задержку с ответом. ", "Извините за задержку")),
            new PhraseGroup("Завершение",
                new Phrase("С уважением,\n", "С уважением"),
                new Phrase("Хорошего дня!"),
                new Phrase("Всего доброго!"),
                new Phrase("Заранее спасибо! "),
                new Phrase("Хороших выходных!"),
                new Phrase("Если появятся вопросы — пишите, с радостью поможем.", "Если появятся вопросы"),
                new Phrase("Ждём вашего ответа. "),
                new Phrase("До связи!")),
        ],
        Abbreviations =
        [
            new Abbreviation("здр", "Здравствуйте, "),
            new Abbreviation("ддн", "Добрый день, "),
            new Abbreviation("дут", "Доброе утро, "),
            new Abbreviation("двеч", "Добрый вечер, "),
            new Abbreviation("спс", "Спасибо! "),
            new Abbreviation("бсп", "Большое спасибо! "),
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
        Version = CurrentDefaultsVersion,
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
            new PhraseGroup("Replies",
                new Phrase("Thank you! "),
                new Phrase("Thanks for the information. ", "For the info"),
                new Phrase("Sounds good. "),
                new Phrase("Yes, that is correct. ", "Correct"),
                new Phrase("Got it, I'm on it. ", "On it"),
                new Phrase("Let me check and get back to you. ", "Will check"),
                new Phrase("Could you please "),
                new Phrase("Sorry for the delay in getting back to you. ", "Sorry for the delay")),
            new PhraseGroup("Closing",
                new Phrase("Best regards,\n", "Best regards"),
                new Phrase("Kind regards,\n", "Kind regards"),
                new Phrase("Have a great day!"),
                new Phrase("Thanks in advance! "),
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
        Version = defaults.Version;
        Groups.Clear();
        foreach (var g in defaults.Groups) Groups.Add(g);
        Abbreviations.Clear();
        foreach (var a in defaults.Abbreviations) Abbreviations.Add(a);
    }
}