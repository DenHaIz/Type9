using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Type9.Input;
using Type9.Prediction;
using Type9.Themes;
using Type9.UI;

namespace Type9.Core;

/// <summary>Связывает движок ввода, предсказание и окна. Живёт в UI-потоке.</summary>
internal sealed class AppController : IInputSink, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly SuggestionPopup _popup = new();
    private readonly RadialMenuWindow _radial = new();
    private readonly CaretLocator _caret;
    private readonly DispatcherTimer _dictSaveTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
    private readonly DispatcherTimer _phrasesSaveTimer;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _paused;

    public AppSettings Settings { get; }
    public PhraseBook Phrases { get; }
    public PredictionEngine Prediction { get; } = new();
    public InputEngine Engine { get; }
    public bool Paused => _paused;

    public event Action? PhrasesChanged;
    public event Action? PausedChanged;
    public event Action? DictionaryChanged;

    public AppController(Dispatcher ui, AppSettings settings, PhraseBook phrases)
    {
        _ui = ui;
        Settings = settings;
        Phrases = phrases;
        Prediction.Abbreviations = phrases.Abbreviations;
        _caret = new CaretLocator(ui);
        Engine = new InputEngine(ui, this, EngineOptions.From(settings));
        Engine.Clicked = _caret.NoteClick;
        _popup.CapsLockCycles = settings.CapsLockCycles;

        _dictSaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => SaveDictionary(false), ui);
        _settingsSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _settingsSaveTimer!.Stop();
            Storage.Save(Paths.Settings, Settings, JsonCtx.Default.AppSettings);
        }, ui);
        _settingsSaveTimer.Stop();
        _phrasesSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) =>
        {
            _phrasesSaveTimer!.Stop();
            Storage.Save(Paths.Phrases, Phrases, JsonCtx.Default.PhraseBook);
        }, ui);
        _phrasesSaveTimer.Stop();

        HotkeyBox_Hook();
        WatchPhrases();
        _popup.ItemClicked += i => Engine.AcceptFromUi(i);
    }

    private void HotkeyBox_Hook() =>
        UI.Controls.HotkeyBox.CaptureChanged += capturing => Engine.CaptureMode = capturing;

    public void Start()
    {
        // Словари грузятся в фоне — программа доступна сразу
        Task.Run(() =>
        {
            var extras = BaseDictionary.LoadExtras();
            UserModel? user = null;
            // Файл может быть временно занят (антивирус, OneDrive) — несколько попыток; при неудаче словарь
            // не перезаписываем пустым до конца сеанса, а повреждённый файл сохраняем копией .broken
            for (int attempt = 0; attempt < 5 && user is null; attempt++)
            {
                try { user = File.Exists(Paths.UserDict) ? UserModel.Parse(File.ReadAllText(Paths.UserDict)) : new UserModel(); }
                catch (IOException) when (attempt < 4) { Thread.Sleep(400); }
                catch (Exception ex)
                {
                    Log.Error("Не удалось загрузить пользовательский словарь — сохранение отключено до перезапуска", ex);
                    try { File.Copy(Paths.UserDict, Paths.UserDict + ".broken", true); } catch { /* ignore */ }
                    _dictSaveBlocked = true;
                    user = new UserModel();
                }
            }
            _ui.BeginInvoke(() =>
            {
                Prediction.Extra = extras;
                // Слова, выученные, пока словарь грузился, не теряем — переносим в загруженный
                var early = Prediction.User;
                if (early.WordCount > 0) user!.Merge(early.Serialize());
                Prediction.User = user!;
                _dictLoaded = true;
                DictionaryChanged?.Invoke();
                Log.Info($"Личный словарь: {user!.WordCount} слов, дополнительный: {extras.Count}");
            });
        });
        EnsureLanguageModels();

        Engine.Start();
        OnFocusChanged(); // проверить поле, в котором пользователь находится сейчас
        _tray = new TrayIcon(this);
        _dictSaveTimer.Start();
        StartUpdateSchedule();
    }

    /// <summary>
    /// Держим в памяти только модели выбранного языка подсказок (~10–14 МБ каждая).
    /// Загрузка — в фоне; ненужная модель выгружается.
    /// </summary>
    private void EnsureLanguageModels()
    {
        bool needRu = Settings.SuggestLanguage is SuggestLanguage.Russian or SuggestLanguage.Both;
        bool needEn = Settings.SuggestLanguage is SuggestLanguage.English or SuggestLanguage.Both;
        bool unloaded = false;
        if (!needRu && Prediction.Russian is not null) { Prediction.Russian = null; unloaded = true; }
        if (!needEn && Prediction.English is not null) { Prediction.English = null; unloaded = true; }
        if (unloaded) _ui.BeginInvoke(TrimMemory, DispatcherPriority.ApplicationIdle);

        void Load(string lang, Action<LanguageModel> assign)
        {
            Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var m = LanguageModel.LoadEmbedded(lang);
                    if (m is null) return;
                    _ui.BeginInvoke(() =>
                    {
                        assign(m);
                        DictionaryChanged?.Invoke();
                        Log.Info($"Модель «{lang}» загружена за {sw.ElapsedMilliseconds} мс: {m.Count} слов");
                        _ui.BeginInvoke(() => { TrimMemory(); WarmUp(); }, DispatcherPriority.ApplicationIdle);
                    });
                }
                catch (Exception ex) { Log.Error($"Не удалось загрузить модель {lang}", ex); }
            });
        }

        if (needRu && Prediction.Russian is null && !_loadingRu)
        {
            _loadingRu = true;
            Load("ru", m => { if (Settings.SuggestLanguage != SuggestLanguage.English) Prediction.Russian = m; _loadingRu = false; });
        }
        if (needEn && Prediction.English is null && !_loadingEn)
        {
            _loadingEn = true;
            Load("en", m => { if (Settings.SuggestLanguage != SuggestLanguage.Russian) Prediction.English = m; _loadingEn = false; });
        }
    }

    private bool _loadingRu, _loadingEn;

    // ------------------------------------------------------------------ обновления

    private DispatcherTimer? _updateTimer;
    private bool _updating;

    public ReleaseInfo? AvailableUpdate { get; private set; }
    public string UpdateStatus { get; private set; } = "";
    public double UpdateProgress { get; private set; } = -1;
    public event Action? UpdateStateChanged;

    private void SetUpdateStatus(string text, double progress = -1)
    {
        UpdateStatus = text;
        UpdateProgress = progress;
        UpdateStateChanged?.Invoke();
    }

    private void StartUpdateSchedule()
    {
        Task.Run(Updater.CleanupOld);
        // Первая проверка — через 20 с после запуска, затем раз в 6 часов
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(6);
            if (Settings.AutoUpdate) await CheckForUpdatesAsync(manual: false);
        }, _ui);
    }

    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        try
        {
            SetUpdateStatus(Loc.T("U.Checking"));
            AvailableUpdate = await Updater.CheckAsync();
            Settings.LastUpdateCheck = DateTime.Now;
            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
            if (AvailableUpdate is null)
            {
                SetUpdateStatus(Loc.F("U.Latest", Updater.Current.ToString(3)));
                return;
            }
            SetUpdateStatus(Loc.F("U.Available", AvailableUpdate.Version.ToString(3)));
            if (!manual && Settings.AutoUpdate && Updater.CanSelfUpdate) await InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Проверка обновлений", ex);
            SetUpdateStatus(manual ? Loc.F("U.Failed", ex.Message) : "");
        }
    }

    public async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release || _updating) return;
        _updating = true;
        try
        {
            SaveDictionary(true);
            SaveSettingsNow();
            var progress = new Progress<double>(p => SetUpdateStatus(Loc.F("U.Downloading", p), p));
            await Updater.InstallAsync(release, progress);
            SetUpdateStatus(Loc.T("U.Installing"), 1);
            Exit(); // новая версия уже запущена и ждёт освобождения мьютекса
        }
        catch (Exception ex)
        {
            Log.Error("Установка обновления", ex);
            SetUpdateStatus(Loc.F("U.Failed", ex.Message));
        }
        finally { _updating = false; }
    }

    public void NotifyUpdated(string version) =>
        _tray?.ShowBalloon("Type9", Loc.F("U.Updated", version));

    // ------------------------------------------------------------------ подсказки

    private PredictionOptions Options => new(Settings.MinPrefix, Settings.MaxSuggestions, Settings.AddSpaceAfterWord,
        Settings.NextWordPrediction, Settings.NextWordMinCount, Settings.SuggestLanguage, Settings.GreetingComma);

    private DispatcherTimer? _learnIdleTimer;

    public void OnContextChanged(int version, TypingContext ctx)
    {
        // Пауза 1,5 с после последнего нажатия — фиксируем отложенное обучение (короткие сообщения тоже учатся)
        _learnIdleTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background,
            (_, _) => { _learnIdleTimer!.Stop(); Engine.CommitPendingLearning(); }, _ui);
        _learnIdleTimer.Stop();
        _learnIdleTimer.Start();

        if (_paused || _radial.IsOpen || !Settings.SuggestionsEnabled || ctx.IsEmpty)
        {
            HideSuggestions();
            return;
        }

        var items = Prediction.Predict(ctx, Options);
        if (!Engine.SetSuggestions(version, items)) return; // контекст уже устарел
        if (items.Count == 0)
        {
            HideSuggestions();
            return;
        }

        _popup.SetItems(items, 0);
        _caret.Request(version, (v, info) =>
        {
            if (info.IsPassword)
            {
                Engine.MarkPasswordField();
                HideSuggestions();
                return;
            }
            Engine.MarkFieldVerified();
            if (v != Engine.Version) return;
            _popup.ShowAt(info, Settings.Placement == PopupPlacement.Fixed);
            Engine.PopupRect = _popup.ScreenRect;
        });
    }

    public void OnSelectionChanged(int selected) => _popup.Select(selected);

    /// <summary>Новое поле ввода: проверяем его на «пароль» (UI Automation) до того, как что-то запоминать.</summary>
    public void OnFocusChanged() => _caret.Request(-2, (_, info) =>
    {
        if (info.IsPassword)
        {
            Engine.MarkPasswordField();
            HideSuggestions();
        }
        else Engine.MarkFieldVerified();
    });

    public void OnHideSuggestions() => HideSuggestions();

    private void HideSuggestions()
    {
        Engine.ClearSuggestions();
        Engine.PopupRect = default;
        _popup.HideAnimated();
    }

    public void OnLearn(CompletedWord word)
    {
        if (!Settings.LearnEnabled) return;
        Prediction.User.Learn(word, UserModel.NowMinutes());
    }

    // ------------------------------------------------------------------ круговое меню

    private int _radialToken;

    public void OnRadialOpen(bool delayed)
    {
        HideSuggestions();
        if (_radial.IsOpen) return;
        int token = ++_radialToken;
        Native.Win32.RECT? caret = null;
        bool caretKnown = false, minDelayPassed = !delayed;

        void TryOpen(bool force)
        {
            if (token != _radialToken || _radial.IsOpen) return;
            if (!minDelayPassed || (!caretKnown && !force)) return;
            // В режиме удержания клавишу могли уже отпустить (быстрое нажатие) — тогда не открываем
            if (Settings.RadialTrigger == RadialTrigger.Hold && !Engine.IsHotkeyHeld) return;
            _radialToken++;
            // Флаг ставим до показа: если клавишу отпустят, пока окно открывается, хук уже пришлёт «выбор»
            Engine.RadialOpen = true;
            _radial.Open(Phrases, caret);
            Engine.RadialOpen = _radial.IsOpen;
            if (_radial.IsOpen && Settings.RadialTrigger == RadialTrigger.Hold && !Engine.IsHotkeyHeld) OnRadialCancel();
        }

        // Ждём позицию каретки (чтобы не закрыть строку с текстом), но не дольше 90 мс
        _caret.Request(-1, (_, info) =>
        {
            if (!info.IsPassword) Engine.MarkFieldVerified();
            caret = info.Precise ? info.Rect : null;
            caretKnown = true;
            TryOpen(false);
        });
        var timer = new DispatcherTimer(DispatcherPriority.Input, _ui) { Interval = TimeSpan.FromMilliseconds(delayed ? 140 : 90) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            minDelayPassed = true;
            TryOpen(force: true);
        };
        timer.Start();
    }

    public void OnRadialCommit()
    {
        _radialToken++;
        var phrase = _radial.Commit();
        Engine.RadialOpen = false;
        if (phrase is not null) InsertPhrase(phrase.Text);
    }

    public void OnRadialCancel()
    {
        _radialToken++;
        _radial.Cancel();
        Engine.RadialOpen = false;
    }

    public void OnRadialDigit(int index)
    {
        var phrase = _radial.Commit(index);
        Engine.RadialOpen = false;
        if (phrase is not null) InsertPhrase(phrase.Text);
    }

    public void OnRadialPage(int delta) => _radial.Page(delta);

    /// <summary>Вставка фразы: ждём, пока пользователь отпустит модификаторы хоткея, иначе Alt+символы сломают ввод.</summary>
    public async void InsertPhrase(string raw)
    {
        try
        {
            var text = Placeholders.Expand(raw);
            if (text.Length == 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Hotkey.CurrentModifiers() != HotkeyModifiers.None && sw.ElapsedMilliseconds < 3000)
                await Task.Delay(10);
            await Task.Delay(15);

            bool clipboard = Settings.InsertMethod == InsertMethod.Clipboard ||
                             (Settings.InsertMethod == InsertMethod.Auto && text.Length > Settings.ClipboardThreshold);
            if (!clipboard || !await PasteViaClipboard(text))
                TextInjector.Type(text, 0, Settings.NewlineAsShiftEnter);

            Engine.NotifyInserted(text);
        }
        catch (Exception ex) { Log.Error("Ошибка вставки фразы", ex); }
    }

    /// <summary>
    /// Вставка через буфер обмена с сохранением ВСЕГО прежнего содержимого (картинки, файлы, форматированный текст).
    /// false — буфер занят другой программой: тогда фраза будет напечатана, а не вставлена старым содержимым.
    /// </summary>
    private static async Task<bool> PasteViaClipboard(string text)
    {
        DataObject? backup = null;
        try
        {
            if (Clipboard.GetDataObject() is { } current)
            {
                backup = new DataObject();
                foreach (var format in current.GetFormats(false))
                {
                    try { if (current.GetData(format, false) is { } data) backup.SetData(format, data, false); }
                    catch { /* некоторые форматы нельзя скопировать — пропускаем */ }
                }
            }
        }
        catch { backup = null; }

        bool set = false;
        for (int attempt = 0; attempt < 5 && !set; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); set = true; }
            catch { await Task.Delay(30); }
        }
        if (!set) return false;

        TextInjector.CtrlV();
        await Task.Delay(400);
        if (backup is not null)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetDataObject(backup, true); break; }
                catch { await Task.Delay(30); }
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ пауза / настройки

    public void OnTogglePause() => SetPaused(!_paused);

    public void SetPaused(bool paused)
    {
        _paused = paused;
        Engine.Paused = paused;
        if (paused)
        {
            HideSuggestions();
            if (_radial.IsOpen) OnRadialCancel();
        }
        _tray?.Update();
        PausedChanged?.Invoke();
    }

    /// <summary>Вызывается окном настроек при любом изменении.</summary>
    public void SettingsChanged()
    {
        Engine.Options = EngineOptions.From(Settings);
        _popup.CapsLockCycles = Settings.CapsLockCycles;
        ThemeManager.Apply(Settings.Theme, Settings.Accent);
        if (Loc.Current != Settings.Language) Loc.Apply(Settings.Language);
        UiScale.Apply(Settings.UiScale);
        EnsureLanguageModels();
        _tray?.Update();
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    public string? ApplyAutostart()
    {
        var err = Autostart.Apply(Settings.Autostart, Settings.Mode);
        SaveSettingsNow();
        return err;
    }

    public void SaveSettingsNow() => Storage.Save(Paths.Settings, Settings, JsonCtx.Default.AppSettings);

    private void WatchPhrases()
    {
        void OnAnyChange()
        {
            _phrasesSaveTimer.Stop();
            _phrasesSaveTimer.Start();
            PhrasesChanged?.Invoke();
        }

        void HookPhrase(Phrase p) => p.PropertyChanged += (_, _) => OnAnyChange();
        void HookGroup(PhraseGroup g)
        {
            g.PropertyChanged += (_, _) => OnAnyChange();
            foreach (var p in g.Phrases) HookPhrase(p);
            g.Phrases.CollectionChanged += (_, e) =>
            {
                foreach (var p in e.NewItems?.OfType<Phrase>() ?? []) HookPhrase(p);
                OnAnyChange();
            };
        }
        void HookAbbr(Abbreviation a) => a.PropertyChanged += (_, _) => OnAnyChange();

        foreach (var g in Phrases.Groups) HookGroup(g);
        foreach (var a in Phrases.Abbreviations) HookAbbr(a);
        Phrases.Groups.CollectionChanged += (_, e) =>
        {
            foreach (var g in e.NewItems?.OfType<PhraseGroup>() ?? []) HookGroup(g);
            OnAnyChange();
        };
        Phrases.Abbreviations.CollectionChanged += (_, e) =>
        {
            foreach (var a in e.NewItems?.OfType<Abbreviation>() ?? []) HookAbbr(a);
            OnAnyChange();
        };
    }

    private volatile bool _dictLoaded, _dictSaveBlocked;

    public void SaveDictionary(bool sync)
    {
        // До загрузки (или после неудачной загрузки) не сохраняем — иначе затёрли бы файл почти пустым словарём
        if (!_dictLoaded || _dictSaveBlocked) return;
        var user = Prediction.User;
        if (!user.Dirty) return;
        user.Prune(UserModel.NowMinutes());
        var text = user.Serialize();
        user.Dirty = false;
        void Write()
        {
            if (!Storage.WriteAtomic(Paths.UserDict, text)) _ui.BeginInvoke(() => user.Dirty = true); // повторим позже
        }
        if (sync) Write();
        else Task.Run(Write);
    }

    public void NotifyDictionaryChanged() => DictionaryChanged?.Invoke();

    public void ShowSettings(string? section = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
        if (section is not null) _settingsWindow.Navigate(section);
    }

    public void Exit() => Application.Current.Shutdown();

    /// <summary>
    /// Убирает временный мусор после загрузки модели или выгрузки языка. Рабочий набор принудительно
    /// не урезается: иначе первое нажатие после простоя подтягивало бы страницы с диска (приоритет — скорость).
    /// </summary>
    public static void TrimMemory() => GC.Collect(2, GCCollectionMode.Optimized, blocking: false);

    /// <summary>Прогрев: один пробный расчёт подсказок, чтобы первое реальное нажатие не ждало JIT/кеш.</summary>
    private void WarmUp()
    {
        try
        {
            foreach (var text in new[] { "добрый д", "в этом мес", "спасибо за ", "hello wor" })
            {
                var t = new TypingTracker();
                foreach (var ch in text) t.Feed(ch);
                Prediction.Predict(t.GetContext(), Options with { MinPrefix = 0 });
            }
        }
        catch (Exception ex) { Log.Error("Прогрев", ex); }
    }

    public void Dispose()
    {
        Engine.Dispose();
        _caret.Dispose();
        _dictSaveTimer.Stop();
        SaveDictionary(true);
        if (_settingsSaveTimer.IsEnabled) SaveSettingsNow();
        if (_phrasesSaveTimer.IsEnabled) Storage.Save(Paths.Phrases, Phrases, JsonCtx.Default.PhraseBook);
        _tray?.Dispose();
    }
}
