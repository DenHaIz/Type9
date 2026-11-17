using System.Globalization;
using System.Windows;

namespace Type9.Core;

public enum AppLanguage { Russian, English }

/// <summary>
/// Локализация RU/EN. Строки кладутся в ресурсы приложения с ключами «S.*», поэтому XAML
/// ({DynamicResource S.…}) перерисовывается мгновенно при смене языка. Из кода — <see cref="T"/>.
/// </summary>
public static class Loc
{
    public static AppLanguage Current { get; private set; } = AppLanguage.Russian;
    public static event Action? Changed;

    public static AppLanguage Default =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk"
            ? AppLanguage.Russian : AppLanguage.English;

    public static string T(string key) =>
        Strings.TryGetValue(key, out var s) ? (Current == AppLanguage.English ? s.En : s.Ru) : key;

    public static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static void Apply(AppLanguage lang, ResourceDictionary? res = null)
    {
        Current = lang;
        res ??= Application.Current?.Resources;
        if (res is not null)
            foreach (var key in Strings.Keys) res["S." + key] = T(key);
        Changed?.Invoke();
    }

    private static readonly Dictionary<string, (string Ru, string En)> Strings = new()
    {
        // Окно и навигация
        ["Window.Title"] = ("Type9 — настройки", "Type9 — settings"),
        ["Nav.General"] = ("Общие", "General"),
        ["Nav.Phrases"] = ("Круговое меню", "Phrase wheel"),
        ["Nav.Abbrev"] = ("Сокращения", "Abbreviations"),
        ["Nav.Dict"] = ("Словарь", "Dictionary"),
        ["Nav.Excl"] = ("Исключения", "Exclusions"),
        ["Nav.About"] = ("О программе", "About"),
        ["Version"] = ("версия {0}", "version {0}"),
        ["Status.Running"] = ("Работает", "Running"),
        ["Status.Paused"] = ("На паузе", "Paused"),
        ["Status.PausedHint"] = ("Подсказки и меню отключены", "Suggestions and wheel are off"),
        ["Status.Hint"] = ("Меню фраз: {0}", "Phrase wheel: {0}"),
        ["Btn.Pause"] = ("Пауза", "Pause"),
        ["Btn.Resume"] = ("Продолжить", "Resume"),
        ["Tip.Minimize"] = ("Свернуть", "Minimize"),
        ["Tip.Close"] = ("Закрыть (программа продолжит работать в трее)", "Close (the app keeps running in the tray)"),
        ["Tip.Up"] = ("Выше", "Move up"),
        ["Tip.Down"] = ("Ниже", "Move down"),
        ["Tip.Delete"] = ("Удалить", "Delete"),
        ["Tip.DeleteGroup"] = ("Удалить группу", "Delete group"),

        // Общие
        ["G.Appearance"] = ("Оформление", "Appearance"),
        ["G.Theme"] = ("Тема", "Theme"),
        ["G.ThemeHint"] = ("«Системная» следует настройке Windows", "“System” follows your Windows setting"),
        ["G.Dark"] = ("Тёмная", "Dark"),
        ["G.Light"] = ("Светлая", "Light"),
        ["G.System"] = ("Системная", "System"),
        ["G.Language"] = ("Язык", "Language"),
        ["G.LanguageHint"] = ("Язык интерфейса программы", "Interface language"),
        ["G.Scale"] = ("Масштаб интерфейса", "Interface scale"),
        ["G.ScaleHint"] = ("Размер окон и текста. «Авто» подбирает его под разрешение экрана", "Size of windows and text. “Auto” fits it to your screen resolution"),
        ["G.Auto"] = ("Авто", "Auto"),
        ["G.Accent"] = ("Акцентный цвет", "Accent color"),
        ["G.Suggestions"] = ("Подсказки при наборе (T9)", "Typing suggestions (T9)"),
        ["G.ShowSugg"] = ("Показывать подсказки", "Show suggestions"),
        ["G.ShowSuggHint"] = ("Tab — вставить, ↑/↓ — выбрать вариант, Esc — скрыть", "Tab — insert, ↑/↓ — choose a variant, Esc — hide"),
        ["G.NextWord"] = ("Предсказывать следующее слово", "Predict the next word"),
        ["G.NextWordHint"] = ("После пробела предлагает слово, которое вы часто пишете дальше. Tab, Tab, Tab — и привычная фраза готова",
                              "After a space it offers the word you often type next. Tab, Tab, Tab — and your usual phrase is done"),
        ["G.MinPrefixHint"] = ("«Пробела» — сразу предлагать следующее слово по смыслу, ещё до первой буквы", "“A space” — suggest the next word by meaning right away, before the first letter"),
        ["G.SuggestLang"] = ("Язык подсказок", "Suggestion language"),
        ["G.SuggestLangHint"] = ("Слова на других языках предлагаться не будут", "Words in other languages won’t be suggested"),
        ["G.Both"] = ("Оба", "Both"),
        ["G.AddSpace"] = ("Ставить пробел после вставленного слова", "Add a space after an inserted word"),
        ["G.SmartPunct"] = ("Умная пунктуация", "Smart punctuation"),
        ["G.SmartPunctHint"] = ("Запятая или точка сразу после подсказки «притягиваются» к слову", "A comma or period typed right after a suggestion snaps to the word"),
        ["G.Learn"] = ("Запоминать мои слова и фразы", "Learn my words and phrases"),
        ["G.LearnHint"] = ("Словарь хранится только на этом компьютере. В полях паролей обучение отключено", "The dictionary stays on this computer. Learning is off in password fields"),
        ["G.HideFull"] = ("Не показывать подсказки в полноэкранных приложениях и играх", "Hide suggestions in full-screen apps and games"),
        ["G.MinPrefix"] = ("Подсказывать после", "Suggest after"),
        ["G.L0"] = ("пробела", "a space"),
        ["G.L1"] = ("1 буквы", "1 letter"),
        ["G.L2"] = ("2 букв", "2 letters"),
        ["G.L3"] = ("3 букв", "3 letters"),
        ["G.L4"] = ("4 букв", "4 letters"),
        ["G.Count"] = ("Количество вариантов", "Number of suggestions"),
        ["G.Where"] = ("Где показывать", "Position"),
        ["G.WhereHint"] = ("Если в каком-то приложении подсказка «прыгает», выберите угол экрана", "If the popup jumps around in some app, use the screen corner"),
        ["G.AtCaret"] = ("У курсора ввода", "At text cursor"),
        ["G.Corner"] = ("В углу экрана", "Screen corner"),
        ["G.Hotkeys"] = ("Горячие клавиши и вставка", "Hotkeys & insertion"),
        ["G.Wheel"] = ("Круговое меню фраз", "Phrase wheel"),
        ["G.WheelHint"] = ("Можно назначить боковую кнопку мыши", "A side mouse button works too"),
        ["G.WheelMode"] = ("Как работает меню", "Wheel behavior"),
        ["G.WheelModeHint"] = ("Удерживать: зажали — навели — отпустили. Нажатие: открыли — кликнули. Цифры 1–8 работают всегда",
                               "Hold: press — point — release. Press: open — click. Digits 1–8 always work"),
        ["G.Hold"] = ("Удерживать", "Hold"),
        ["G.Toggle"] = ("Нажатие", "Press"),
        ["G.PauseKey"] = ("Пауза / продолжить", "Pause / resume"),
        ["G.ShiftEnter"] = ("Перенос строки во фразах как Shift+Enter", "Send line breaks in phrases as Shift+Enter"),
        ["G.ShiftEnterHint"] = ("Чтобы мессенджеры не отправляли сообщение посреди многострочной фразы", "So messengers don’t send the message in the middle of a multi-line phrase"),
        ["G.Insert"] = ("Способ вставки фраз", "Phrase insertion"),
        ["G.InsertHint"] = ("Авто: короткие фразы печатаются, длинные (>200 символов) вставляются через буфер обмена", "Auto: short phrases are typed, long ones (>200 chars) are pasted via the clipboard"),
        ["G.Typing"] = ("Печать", "Typing"),
        ["G.Clipboard"] = ("Буфер", "Clipboard"),
        ["G.Startup"] = ("Запуск", "Startup"),
        ["U.Title"] = ("Обновления", "Updates"),
        ["U.Auto"] = ("Обновлять автоматически", "Update automatically"),
        ["U.AutoHint"] = ("Новые версии ставятся с GitHub в фоне. Ваш словарь, фразы и настройки сохраняются", "New versions are installed from GitHub in the background. Your dictionary, phrases and settings are kept"),
        ["U.Check"] = ("Проверить сейчас", "Check now"),
        ["U.Install"] = ("Установить", "Install"),
        ["U.Checking"] = ("Проверяю…", "Checking…"),
        ["U.Latest"] = ("Установлена последняя версия ({0})", "You have the latest version ({0})"),
        ["U.Available"] = ("Доступна версия {0}", "Version {0} is available"),
        ["U.Downloading"] = ("Загрузка… {0:P0}", "Downloading… {0:P0}"),
        ["U.Installing"] = ("Перезапуск…", "Restarting…"),
        ["U.Failed"] = ("Не удалось обновиться: {0}", "Update failed: {0}"),
        ["U.DevBuild"] = ("Автообновление работает только в собранном .exe", "Self-update only works in the published .exe"),
        ["U.Updated"] = ("Обновлено до версии {0}", "Updated to version {0}"),
        ["G.Autostart"] = ("Запускать вместе с Windows", "Start with Windows"),
        ["G.AutostartHint"] = ("Программа стартует свёрнутой в трей", "The app starts minimized to the tray"),
        ["G.Mode"] = ("Режим работы", "Run mode"),
        ["G.ModeHint"] = ("«Администратор» нужен, чтобы подсказки и меню работали в окнах, запущенных от имени администратора. Автозапуск тогда идёт через Планировщик заданий — без запроса UAC при входе",
                          "“Administrator” lets suggestions and the wheel work in windows run as administrator. Autostart then uses Task Scheduler — no UAC prompt at sign-in"),
        ["G.ModeNormal"] = ("Обычный", "Normal"),
        ["G.ModeAdmin"] = ("Администратор", "Administrator"),
        ["Mode.IsAdmin"] = ("Сейчас программа работает с правами администратора.", "The app is running with administrator rights."),
        ["Mode.AdminFailed"] = ("Права администратора не получены — программа работает в обычном режиме.", "Administrator rights were not granted — running in normal mode."),
        ["Mode.IsNormal"] = ("Сейчас программа работает без прав администратора.", "The app is running without administrator rights."),
        ["Mode.UacDenied"] = ("Запрос прав администратора отклонён — режим не изменён.", "The administrator request was declined — mode unchanged."),
        ["Mode.StillAdmin"] = ("Режим изменён. Сейчас программа ещё работает с правами администратора — это сбросится при следующем запуске.",
                               "Mode changed. The app still has administrator rights until the next start."),
        ["Autostart.On"] = ("Автозапуск включён", "Autostart is on"),
        ["Autostart.Off"] = ("Автозапуск выключен", "Autostart is off"),
        ["Autostart.NeedAdmin"] = ("Для автозапуска с правами администратора программа должна быть запущена от администратора.",
                                   "To autostart as administrator, the app must be running as administrator."),

        // Круговое меню
        ["P.Preview"] = ("Превью — наведите курсор", "Preview — hover over it"),
        ["P.Groups"] = ("Группы (страницы меню)", "Groups (wheel pages)"),
        ["P.GroupsHint"] = ("Переключение в меню — колесом мыши или ←/→", "Switch pages with the mouse wheel or ←/→"),
        ["P.AddGroup"] = ("+ Группа", "+ Group"),
        ["P.GroupName"] = ("Название группы", "Group name"),
        ["P.GroupNameHint"] = ("Например: Приветствия", "e.g. Greetings"),
        ["P.AddPhrase"] = ("+ Добавить фразу", "+ Add phrase"),
        ["P.Help"] = ("До 8 фраз в группе. Подстановки: {date} — дата, {time} — время, {clipboard} — буфер обмена. Подпись — короткий текст в секторе (если пусто — первая строка фразы).",
                      "Up to 8 phrases per group. Placeholders: {date}, {time}, {clipboard}. The label is the short text shown in the sector (first line of the phrase if empty)."),
        ["P.TextHint"] = ("Текст фразы", "Phrase text"),
        ["P.LabelHint"] = ("Подпись в меню (необязательно)", "Wheel label (optional)"),
        ["P.NewGroup"] = ("Группа {0}", "Group {0}"),
        ["P.Reset"] = ("Стандартные фразы", "Default phrases"),
        ["P.ResetConfirm"] = ("Заменить все фразы? Нажмите ещё раз", "Replace all phrases? Click again"),
        ["P.ResetTip"] = ("Заменить группы и сокращения стандартным набором деловых фраз", "Replace groups and abbreviations with the default set of business phrases"),

        // Сокращения
        ["A.Title"] = ("Сокращения", "Abbreviations"),
        ["A.Help"] = ("Наберите сокращение (например, «здр») — в подсказке появится полный текст, Tab вставит его вместо сокращения. Поддерживаются {date}, {time}, {clipboard} и переносы строк.",
                      "Type an abbreviation (e.g. “brb”) — the full text appears as a suggestion and Tab replaces the abbreviation with it. {date}, {time}, {clipboard} and line breaks are supported."),
        ["A.Add"] = ("+ Добавить сокращение", "+ Add abbreviation"),
        ["A.KeyHint"] = ("сокращение", "abbreviation"),
        ["A.TextHint"] = ("Полный текст", "Full text"),

        // Словарь
        ["D.Learned"] = ("Выученные слова", "Learned words"),
        ["D.Stats"] = ("Слов в личном словаре: {0:N0} · в базовом: {1:N0}", "Personal dictionary: {0:N0} words · built-in: {1:N0}"),
        ["D.Search"] = ("Поиск…", "Search…"),
        ["D.Delete"] = ("Удалить слово", "Delete word"),
        ["D.Export"] = ("Экспорт…", "Export…"),
        ["D.Import"] = ("Импорт…", "Import…"),
        ["D.Clear"] = ("Очистить всё", "Clear all"),
        ["D.ClearConfirm"] = ("Точно очистить? Нажмите ещё раз", "Sure? Click again to clear"),
        ["D.Train"] = ("Обучить на тексте", "Learn from text"),
        ["D.TrainHelp"] = ("Вставьте типовые письма или ответы — программа запомнит слова и порядок, в котором вы их пишете. Так подсказки станут точными сразу, без долгого привыкания.",
                           "Paste typical letters or replies — the app learns your words and the order you write them in, so suggestions are accurate right away."),
        ["D.TrainBtn"] = ("Обучить", "Learn"),
        ["D.TrainFile"] = ("Из файла .txt…", "From .txt file…"),
        ["D.TrainSample"] = ("Например: Добрый день! Благодарим за обращение. Ваша заявка принята в работу…", "e.g. Good afternoon! Thank you for reaching out. Your request is being processed…"),
        ["D.Trained"] = ("Изучено слов: {0:N0}", "Words learned: {0:N0}"),
        ["D.NoText"] = ("Нет текста", "No text"),
        ["D.Error"] = ("Ошибка: {0}", "Error: {0}"),
        ["D.ImportFail"] = ("Не удалось импортировать: {0}", "Import failed: {0}"),
        ["D.FilterDict"] = ("Словарь Type9 (*.tsv)|*.tsv|Все файлы|*.*", "Type9 dictionary (*.tsv)|*.tsv|All files|*.*"),
        ["D.FilterTxt"] = ("Текст (*.txt)|*.txt|Все файлы|*.*", "Text (*.txt)|*.txt|All files|*.*"),

        // Исключения
        ["E.Title"] = ("Программы-исключения", "Excluded apps"),
        ["E.Help"] = ("В этих программах подсказки и обучение отключены (менеджеры паролей, игры и т.п.). Указывается имя процесса без .exe.",
                      "Suggestions and learning are off in these apps (password managers, games, etc.). Use the process name without .exe."),
        ["E.Input"] = ("Имя процесса, например: Telegram", "Process name, e.g. Telegram"),
        ["E.Add"] = ("Добавить", "Add"),
        ["E.Pick"] = ("Выбрать из запущенных…", "Pick a running app…"),
        ["E.Remove"] = ("Удалить", "Remove"),
        ["E.NoWindows"] = ("Нет окон", "No windows"),

        // О программе
        ["Ab.Version"] = ("Версия {0} · {1}", "Version {0} · {1}"),
        ["Ab.Admin"] = ("запущена от администратора", "running as administrator"),
        ["Ab.Normal"] = ("обычный режим", "normal mode"),
        ["Ab.Desc"] = ("Подсказки при наборе в любом поле Windows — как T9 на телефоне — и круговое меню готовых фраз по горячей клавише. Программа учится на вашем тексте и хранит всё только на этом компьютере.",
                       "Typing suggestions in any Windows text field — like T9 on a phone — plus a hotkey wheel of ready-made phrases. The app learns from your text and keeps everything on this computer."),
        ["Ab.Cheat"] = ("Шпаргалка", "Cheat sheet"),
        ["Ab.Data"] = ("Данные", "Data"),
        ["Ab.OpenFolder"] = ("Открыть папку данных", "Open data folder"),
        ["Ab.Exit"] = ("Выйти из программы", "Quit Type9"),
        ["Ab.DictHint"] = ("Дополнительные частотные словари можно положить в папку данных\\dict (по слову в строке, от частых к редким).",
                           "Extra frequency dictionaries can be placed in the data folder\\dict (one word per line, most frequent first)."),
        ["C.Tab"] = ("вставить подсказку", "insert the suggestion"),
        ["C.Arrows"] = ("выбрать другой вариант", "choose another variant"),
        ["C.Esc"] = ("скрыть подсказку", "hide the suggestion"),
        ["C.Hold"] = ("{0} (удерживать)", "{0} (hold)"),
        ["C.Wheel"] = ("круговое меню фраз: навести и отпустить", "phrase wheel: point and release"),
        ["C.Digits"] = ("мгновенный выбор фразы в меню", "instant phrase pick in the wheel"),
        ["C.PagesKey"] = ("Колесо / ← →", "Wheel / ← →"),
        ["C.Pages"] = ("другая группа фраз", "another phrase group"),
        ["C.Pause"] = ("пауза / продолжить", "pause / resume"),

        // Поле хоткея, подсказка, меню, трей
        ["H.Press"] = ("Нажмите сочетание…", "Press a shortcut…"),
        ["H.NeedMod"] = ("Нужен Ctrl, Alt или Win", "Ctrl, Alt or Win required"),
        ["H.Tip"] = ("Нажмите поле и затем нужное сочетание. Esc — отмена.", "Click the field, then press the shortcut. Esc cancels."),
        ["Pop.Hint"] = ("Tab — вставить · ↑↓ — выбор · Esc — скрыть", "Tab — insert · ↑↓ — choose · Esc — hide"),
        ["R.Hover"] = ("Наведите на фразу", "Point at a phrase"),
        ["R.Empty"] = ("Нет фраз — добавьте их в настройках", "No phrases — add some in settings"),
        ["T.Settings"] = ("Настройки", "Settings"),
        ["T.Phrases"] = ("Фразы", "Phrases"),
        ["T.Light"] = ("Светлая тема", "Light theme"),
        ["T.Dark"] = ("Тёмная тема", "Dark theme"),
        ["T.Exit"] = ("Выход", "Quit"),
        ["T.TipPaused"] = ("Type9 — пауза", "Type9 — paused"),
        ["T.Tip"] = ("Type9 — меню фраз: {0}", "Type9 — phrase wheel: {0}"),
    };
}
