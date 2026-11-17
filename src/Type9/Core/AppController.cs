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
            UserModel user;
            try { user = File.Exists(Paths.UserDict) ? UserModel.Parse(File.ReadAllText(Paths.UserDict)) : new UserModel(); }
            catch (Exception ex) { Log.Error("Не удалось загрузить пользовательский словарь", ex); user = new UserModel(); }
            _ui.BeginInvoke(() =>
            {
                Prediction.Extra = extras;
                Prediction.User = user;
                DictionaryChanged?.Invoke();
                Log.Info($"Личный словарь: {user.WordCount} слов, дополнительный: {extras.Count}");
            });
        });
        EnsureLanguageModels();

        Engine.Start();
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
        Settings.NextWordPrediction, Settings.NextWordMinCount, Settings.SuggestLanguage);

    public void OnContextChanged(int version, TypingContext ctx)
    {
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
            if (v != Engine.Version) return;
            if (info.IsPassword)
            {
                Engine.MarkPasswordField();
                HideSuggestions();
                return;
            }
            _popup.ShowAt(info, Settings.Placement == PopupPlacement.Fixed);
            Engine.PopupRect = _popup.ScreenRect;
        });
    }

    public void OnSelectionChanged(int selected) => _popup.Select(selected);

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

    public void OnRadialOpen()
    {
        HideSuggestions();
        _radial.Open(Phrases);
        Engine.RadialOpen = _radial.IsOpen;
    }

    public void OnRadialCommit()
    {
        var phrase = _radial.Commit();
        Engine.RadialOpen = false;
        if (phrase is not null) InsertPhrase(phrase.Text);
    }

    public void OnRadialCancel()
    {
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
            if (clipboard) await PasteViaClipboard(text);
            else TextInjector.Type(text, 0, Settings.NewlineAsShiftEnter);

            Engine.NotifyInserted(text);
        }
        catch (Exception ex) { Log.Error("Ошибка вставки фразы", ex); }
    }

    private static async Task PasteViaClipboard(string text)
    {
        string? backup = null;
        try { if (Clipboard.ContainsText()) backup = Clipboard.GetText(); } catch { /* буфер занят */ }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(text, true); break; }
            catch { await Task.Delay(30); }
        }
        TextInjector.CtrlV();
        await Task.Delay(400);
        if (backup is not null)
        {
            try { Clipboard.SetDataObject(backup, true); } catch { /* ignore */ }
        }
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

    public void SaveDictionary(bool sync)
    {
        var user = Prediction.User;
        if (!user.Dirty) return;
        var text = user.Serialize();
        user.Dirty = false;
        if (sync) Storage.WriteAtomic(Paths.UserDict, text);
        else Task.Run(() => Storage.WriteAtomic(Paths.UserDict, text));
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
