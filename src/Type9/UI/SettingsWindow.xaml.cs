using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Type9.Core;
using Type9.Prediction;
using Type9.Themes;

namespace Type9.UI;

public sealed record WordRow(string Form, int Count);

internal partial class SettingsWindow : Window
{
    private const double BaseWidth = 1020, BaseHeight = 700, BaseMinWidth = 900, BaseMinHeight = 580, BaseCaption = 48;

    private readonly AppController _app;
    private readonly ObservableCollection<string> _excluded;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _logoTimer;
    private FrameworkElement? _currentSection;
    private string _section = "general";
    private int _logoStep;
    private bool _clearArmed;

    public SettingsWindow(AppController app)
    {
        _app = app;
        InitializeComponent();
        UiScale.PixelAlign(this);
        DataContext = app.Settings;
        SecPhrases.DataContext = app.Phrases;
        SecAbbrev.DataContext = app.Phrases;
        _excluded = new ObservableCollection<string>(app.Settings.ExcludedProcesses);
        ExclList.ItemsSource = _excluded;
        DataPath.Text = Paths.DataDir;

        BuildAccentSwatches();
        UpdateLogos();
        RefreshTexts();
        ApplyScale(animate: false);
        FitToScreen(center: true);

        // Любое изменение настроек применяется сразу
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnSettingToggled));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnSettingToggled));
        Controls.HotkeyBox.CaptureChanged += OnHotkeyCapture;

        _app.PhrasesChanged += RefreshPreview;
        _app.PausedChanged += UpdateStatus;
        _app.DictionaryChanged += RefreshWords;
        _app.UpdateStateChanged += RefreshUpdate;
        ThemeManager.Changed += UpdateLogos;
        ThemeManager.Changing += CaptureForCrossfade;
        ThemeManager.Changed += Crossfade;
        Loc.Changed += OnLanguageChanged;
        UiScale.Changed += OnScaleChanged;

        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            RefreshWords();
        }, Dispatcher);
        _searchTimer.Stop();

        _logoTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(70), DispatcherPriority.Render, (_, _) => LogoTick(), Dispatcher);
        _logoTimer.Stop();

        Loaded += (_, _) =>
        {
            RootGrid.Opacity = 0;
            RootGrid.Animate(OpacityProperty, 1, Motion.Slow, Motion.EaseOut);
            if (_currentSection is null) Navigate("general");
            if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
            RefreshWords();
        };
        StateChanged += (_, _) => Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        Closed += (_, _) =>
        {
            _app.PhrasesChanged -= RefreshPreview;
            _app.PausedChanged -= UpdateStatus;
            _app.DictionaryChanged -= RefreshWords;
            _app.UpdateStateChanged -= RefreshUpdate;
            ThemeManager.Changed -= UpdateLogos;
            ThemeManager.Changing -= CaptureForCrossfade;
            ThemeManager.Changed -= Crossfade;
            Loc.Changed -= OnLanguageChanged;
            UiScale.Changed -= OnScaleChanged;
            Controls.HotkeyBox.CaptureChanged -= OnHotkeyCapture;
            _app.Engine.CaptureMode = false;
            _logoTimer.Stop();
        };
    }

    // ------------------------------------------------------------------ масштаб и размер окна

    private void ApplyScale(bool animate)
    {
        UiScale.AnimateTo(RootScale, animate);
        Chrome.CaptionHeight = BaseCaption * UiScale.Current;
    }

    private void OnScaleChanged()
    {
        ApplyScale(animate: true);
        FitToScreen(center: false);
    }

    /// <summary>Размер окна — от базового с учётом масштаба, но не больше 92% рабочей области экрана.</summary>
    private void FitToScreen(bool center)
    {
        var wa = SystemParameters.WorkArea;
        if (WindowState == WindowState.Normal && PresentationSource.FromVisual(this) is { } src)
        {
            // Монитор, на котором окно сейчас (в DIP)
            var mi = Native.Win32.GetMonitorInfoAt((int)((Left + Width / 2) * src.CompositionTarget.TransformToDevice.M11),
                (int)((Top + Height / 2) * src.CompositionTarget.TransformToDevice.M22));
            var m = src.CompositionTarget.TransformFromDevice;
            var tl = m.Transform(new Point(mi.rcWork.Left, mi.rcWork.Top));
            var br = m.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
            wa = new Rect(tl, br);
        }

        double s = UiScale.Current;
        double maxW = wa.Width * 0.92, maxH = wa.Height * 0.92;
        MinWidth = Math.Min(BaseMinWidth * s, maxW);
        MinHeight = Math.Min(BaseMinHeight * s, maxH);
        double w = Math.Clamp(BaseWidth * s, MinWidth, maxW);
        double h = Math.Clamp(BaseHeight * s, MinHeight, maxH);

        if (center)
        {
            Width = w;
            Height = h;
            Left = wa.Left + (wa.Width - w) / 2;
            Top = wa.Top + (wa.Height - h) / 2;
            return;
        }
        if (WindowState != WindowState.Normal) return;
        double cx = Left + Width / 2, cy = Top + Height / 2;
        Width = w;
        Height = h;
        Left = Math.Clamp(cx - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
        Top = Math.Clamp(cy - h / 2, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
    }

    // ------------------------------------------------------------------ смена темы

    /// <summary>Снимок окна в старой теме — поверх нового оформления, затем плавно растворяется.</summary>
    private void CaptureForCrossfade()
    {
        if (!IsVisible || RootGrid.ActualWidth < 1) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var parent = (FrameworkElement)ThemeOverlay.Parent;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(parent.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(parent.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(RootGrid);
        ThemeOverlay.Source = rtb;
        ThemeOverlay.BeginAnimation(OpacityProperty, null);
        ThemeOverlay.Opacity = 1;
        ThemeOverlay.Visibility = Visibility.Visible;
    }

    private void Crossfade()
    {
        if (ThemeOverlay.Visibility != Visibility.Visible) return;
        ThemeOverlay.Animate(OpacityProperty, 0, Motion.Slow, Motion.EaseOut, completed: (_, _) =>
        {
            ThemeOverlay.Visibility = Visibility.Collapsed;
            ThemeOverlay.Source = null;
        });
    }

    // ------------------------------------------------------------------ навигация

    public void Navigate(string section)
    {
        var nav = section switch
        {
            "phrases" => NavPhrases, "abbrev" => NavAbbrev, "dict" => NavDict,
            "excl" => NavExcl, "about" => NavAbout, _ => NavGeneral,
        };
        if (nav.IsChecked == true) ShowSection(section);
        else nav.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string section }) ShowSection(section);
        e.Handled = true; // не считаем это изменением настроек
    }

    private static string SectionKey(string section) => section switch
    {
        "phrases" => "Nav.Phrases", "abbrev" => "Nav.Abbrev", "dict" => "Nav.Dict",
        "excl" => "Nav.Excl", "about" => "Nav.About", _ => "Nav.General",
    };

    private void ShowSection(string section)
    {
        if (SectionHost is null) return;
        _section = section;
        var target = SectionHost.Children.OfType<FrameworkElement>().First(c => (string)c.Tag == section);
        SectionTitle.Text = Loc.T(SectionKey(section));
        Anim.Play(SectionTitle, 4);

        if (_currentSection == target) return;
        if (_currentSection is not null) _currentSection.Visibility = Visibility.Collapsed;
        _currentSection = target;
        UiWatchdog.Context = "настройки: " + section;
        target.Visibility = Visibility.Visible;
        // При программной отрисовке (удалённый рабочий стол) прозрачность всего раздела — дорогой слой: только сдвиг
        if (SoftwareRendering) Anim.Slide(target, 12);
        else Anim.Play(target, 12);

        if (section == "about") { _logoStep = 0; _logoTimer.Start(); }
        if (section == "dict" && _wordsDirty) RefreshWords();
        if (section == "phrases") RefreshPreview();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) =>
        RootGrid.Animate(OpacityProperty, 0, Motion.Fast, Motion.EaseIn, completed: (_, _) => Close());

    // ------------------------------------------------------------------ тексты и язык

    private void OnLanguageChanged()
    {
        // Мягкая смена языка: контент на мгновение приглушается и проявляется уже с новыми строками
        RefreshTexts();
        RootGrid.Animate(OpacityProperty, 1, Motion.Slow, Motion.EaseOut, 0.35);
    }

    private void RefreshTexts()
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3);
        VersionText.Text = Loc.F("Version", version ?? "");
        AboutVersion.Text = Loc.F("Ab.Version", version ?? "", Loc.T(Elevation.IsElevated ? "Ab.Admin" : "Ab.Normal"));
        SectionTitle.Text = Loc.T(SectionKey(_section));
        AccentName.Text = ThemeManager.AccentPresets.FirstOrDefault(p => string.Equals(p.Hex, _app.Settings.Accent, StringComparison.OrdinalIgnoreCase)).Name is { } n
            ? AccentDisplayName(n) : _app.Settings.Accent;
        if (!_clearArmed) ClearDictButton.Content = Loc.T("D.Clear");
        BuildCheatSheet();
        UpdateStatus();
        UpdateMode();
        RefreshWords();
        RefreshPreview();
        RefreshUpdate();
        UpdateHotkeyWarnings();
    }

    // ------------------------------------------------------------------ обновления

    private void RefreshUpdate()
    {
        var text = _app.UpdateStatus;
        if (string.IsNullOrEmpty(text) && _app.Settings.LastUpdateCheck > DateTime.MinValue)
            text = Loc.F("U.Latest", Updater.Current.ToString(3));
        if (UpdateStatusText.Text != text)
        {
            UpdateStatusText.Text = text;
            Anim.Play(UpdateStatusText, 3);
        }
        bool canInstall = _app.AvailableUpdate is not null && Updater.CanSelfUpdate && _app.UpdateProgress < 0;
        if (canInstall && InstallUpdateButton.Visibility != Visibility.Visible)
        {
            InstallUpdateButton.Visibility = Visibility.Visible;
            Anim.Play(InstallUpdateButton, 4);
        }
        else if (!canInstall) InstallUpdateButton.Visibility = Visibility.Collapsed;

        if (_app.UpdateProgress >= 0)
        {
            UpdateProgressHost.Visibility = Visibility.Visible;
            UpdateProgressScale.Animate(ScaleTransform.ScaleXProperty, _app.UpdateProgress, Motion.Normal, Motion.EaseOut);
        }
        else UpdateProgressHost.Visibility = Visibility.Collapsed;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try { await _app.CheckForUpdatesAsync(manual: true); }
        finally { CheckUpdateButton.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        try { await _app.InstallUpdateAsync(); }
        finally { InstallUpdateButton.IsEnabled = true; }
    }

    private static string AccentDisplayName(string ruName) => Loc.Current == AppLanguage.Russian ? ruName : ruName switch
    {
        "Бордовый" => "Burgundy", "Винный" => "Wine", "Лиловый" => "Lilac", "Медный" => "Copper",
        "Сапфировый" => "Sapphire", "Изумрудный" => "Emerald", "Графит" => "Graphite", _ => ruName,
    };

    // ------------------------------------------------------------------ общие

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is RadioButton rb && (rb.Name.StartsWith("Nav") || rb.Name.StartsWith("Mode") || rb.Name.StartsWith("Accent"))) return;
        // Привязка обновляет источник сразу после события — применяем на следующем такте
        Dispatcher.BeginInvoke(() =>
        {
            _app.SettingsChanged();
            UpdateStatus();
        }, DispatcherPriority.Background);
    }

    private void OnHotkeyCapture(bool capturing)
    {
        _app.Engine.CaptureMode = capturing;
        if (!capturing) Dispatcher.BeginInvoke(() =>
        {
            _app.SettingsChanged();
            BuildCheatSheet();
            UpdateStatus();
            UpdateHotkeyWarnings();
        }, DispatcherPriority.Background);
    }

    /// <summary>Предупреждение, если клавиша пересекается со стандартными сочетаниями (Ctrl+C, F5, Mouse4…).</summary>
    private void UpdateHotkeyWarnings()
    {
        Show(RadialHotkeyWarning, RadialHotkeyWarningText, Input.HotkeyAdvisor.Check(Input.Hotkey.Parse(_app.Settings.RadialHotkey)));
        Show(PauseHotkeyWarning, PauseHotkeyWarningText, Input.HotkeyAdvisor.Check(Input.Hotkey.Parse(_app.Settings.PauseHotkey)));

        static void Show(Border box, TextBlock text, string? warning)
        {
            if (warning is null)
            {
                if (box.Visibility == Visibility.Visible)
                    box.Animate(OpacityProperty, 0, Motion.Fast, Motion.EaseIn, completed: (_, _) => { if (text.Text.Length == 0) box.Visibility = Visibility.Collapsed; });
                text.Text = "";
                return;
            }
            bool changed = text.Text != warning;
            text.Text = warning;
            if (box.Visibility != Visibility.Visible || changed)
            {
                box.Visibility = Visibility.Visible;
                Anim.Play(box, 6);
            }
        }
    }

    private void BuildAccentSwatches()
    {
        foreach (var (name, hex) in ThemeManager.AccentPresets)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(ThemeManager.ParseColor(hex)),
                ToolTip = AccentDisplayName(name),
                GroupName = "accent",
                IsChecked = string.Equals(_app.Settings.Accent, hex, StringComparison.OrdinalIgnoreCase),
                Name = "Accent" + AccentPanel.Children.Count,
            };
            rb.Checked += (_, _) =>
            {
                _app.Settings.Accent = hex;
                AccentName.Text = AccentDisplayName(name);
                Anim.Pulse(AccentName);
                _app.SettingsChanged();
            };
            AccentPanel.Children.Add(rb);
        }
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var err = _app.ApplyAutostart();
            SetModeStatus(err ?? Loc.T(_app.Settings.Autostart ? "Autostart.On" : "Autostart.Off"));
        }, DispatcherPriority.Background);
    }

    private void SetModeStatus(string text)
    {
        ModeStatus.Text = text;
        Anim.Play(ModeStatus, 4);
    }

    private void UpdateMode()
    {
        ModeNormal.IsChecked = _app.Settings.Mode == RunMode.Normal;
        ModeElevated.IsChecked = _app.Settings.Mode == RunMode.Elevated;
        ModeStatus.Text = Loc.T(Elevation.IsElevated ? "Mode.IsAdmin"
            : _app.Settings.Mode == RunMode.Elevated ? "Mode.AdminFailed" : "Mode.IsNormal");
        if (_app.Settings.Mode == RunMode.Elevated && Autostart.ExeFolderIsUserWritable())
            ModeStatus.Text += Environment.NewLine + Loc.T("Mode.WritableWarn");
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        var mode = sender == ModeElevated ? RunMode.Elevated : RunMode.Normal;
        if (mode == _app.Settings.Mode && (mode == RunMode.Normal || Elevation.IsElevated)) { UpdateMode(); return; }

        if (mode == RunMode.Elevated && !Elevation.IsElevated)
        {
            _app.Settings.Mode = RunMode.Elevated;
            _app.SaveSettingsNow();
            if (Elevation.RelaunchElevated("--restart"))
            {
                _app.Exit();
                return;
            }
            _app.Settings.Mode = RunMode.Normal; // отказ в UAC
            _app.SaveSettingsNow();
            UpdateMode();
            SetModeStatus(Loc.T("Mode.UacDenied"));
            return;
        }

        _app.Settings.Mode = mode;
        var err = _app.ApplyAutostart();
        UpdateMode();
        if (err is not null) SetModeStatus(err);
        else if (mode == RunMode.Normal && Elevation.IsElevated) SetModeStatus(Loc.T("Mode.StillAdmin"));
    }

    private void UpdateStatus()
    {
        bool paused = _app.Paused;
        StatusDot.SetResourceReference(Shape.FillProperty, paused ? "B.FgMuted" : "B.Accent");
        StatusText.Text = Loc.T(paused ? "Status.Paused" : "Status.Running");
        StatusHint.Text = paused ? Loc.T("Status.PausedHint") : Loc.F("Status.Hint", _app.Settings.RadialHotkey);
        PauseButton.Content = Loc.T(paused ? "Btn.Resume" : "Btn.Pause");
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _app.SetPaused(!_app.Paused);
        Anim.Pulse(StatusText);
        Anim.Pulse(StatusDot);
    }

    // ------------------------------------------------------------------ фразы

    private PhraseGroup? SelectedGroup => GroupList.SelectedItem as PhraseGroup;

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        GroupEditor.DataContext = SelectedGroup;
        GroupEditor.IsEnabled = SelectedGroup is not null;
        if (SelectedGroup is not null) Anim.Play(GroupEditor, 6);
        RefreshPreview();
        if (IsLoaded) Preview.PlayPage(1);
    }

    private void RefreshPreview()
    {
        if (Preview is null) return;
        var groups = _app.Phrases.Groups;
        int idx = SelectedGroup is { } g ? groups.IndexOf(g) : -1;
        Preview.Build(idx >= 0 ? groups[idx] : null, Math.Max(0, idx), groups.Count);
        AddPhraseButton.IsEnabled = SelectedGroup is { } sg && sg.Phrases.Count < PhraseGroup.MaxPhrases;
    }

    private void AddGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = new PhraseGroup(Loc.F("P.NewGroup", _app.Phrases.Groups.Count + 1));
        _app.Phrases.Groups.Add(g);
        GroupList.SelectedItem = g;
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } g) return;
        var container = GroupList.ItemContainerGenerator.ContainerFromItem(g) as FrameworkElement;
        Anim.Remove(container, () =>
        {
            int i = _app.Phrases.Groups.IndexOf(g);
            _app.Phrases.Groups.Remove(g);
            if (_app.Phrases.Groups.Count > 0) GroupList.SelectedIndex = Math.Clamp(i, 0, _app.Phrases.Groups.Count - 1);
        });
    }

    private void GroupUp_Click(object sender, RoutedEventArgs e) => MoveGroup(-1);
    private void GroupDown_Click(object sender, RoutedEventArgs e) => MoveGroup(1);

    private void MoveGroup(int d)
    {
        if (SelectedGroup is not { } g) return;
        int i = _app.Phrases.Groups.IndexOf(g), j = i + d;
        if (j < 0 || j >= _app.Phrases.Groups.Count) return;
        _app.Phrases.Groups.Move(i, j);
        GroupList.SelectedItem = g;
        if (GroupList.ItemContainerGenerator.ContainerFromItem(g) is FrameworkElement fe) Anim.Play(fe, d * 6);
    }

    private bool _resetArmed;

    private void ResetPhrases_Click(object sender, RoutedEventArgs e)
    {
        if (!_resetArmed)
        {
            _resetArmed = true;
            ResetPhrasesButton.Content = Loc.T("P.ResetConfirm");
            Anim.Pulse(ResetPhrasesButton);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _resetArmed = false;
                ResetPhrasesButton.SetResourceReference(ContentProperty, "S.P.Reset");
                Anim.Pulse(ResetPhrasesButton);
            };
            t.Start();
            return;
        }
        _resetArmed = false;
        ResetPhrasesButton.SetResourceReference(ContentProperty, "S.P.Reset");
        SecPhrases.Animate(OpacityProperty, 0, Motion.Normal, Motion.EaseIn, completed: (_, _) =>
        {
            _app.Phrases.ResetTo(PhraseBook.CreateDefault(_app.Settings.Language));
            if (GroupList.Items.Count > 0) GroupList.SelectedIndex = 0;
            RefreshPreview();
            Anim.Play(SecPhrases, 10);
            Preview.PlayOpen();
        });
    }

    private void AddPhrase_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } g || g.Phrases.Count >= PhraseGroup.MaxPhrases) return;
        g.Phrases.Add(new Phrase(""));
        RefreshPreview();
        Preview.PlayOpen();
    }

    private static Phrase? PhraseOf(object sender) => (sender as FrameworkElement)?.DataContext as Phrase;

    private void PhraseUp_Click(object sender, RoutedEventArgs e) => MovePhrase(sender, -1);
    private void PhraseDown_Click(object sender, RoutedEventArgs e) => MovePhrase(sender, 1);

    private void MovePhrase(object sender, int d)
    {
        if (SelectedGroup is not { } g || PhraseOf(sender) is not { } p) return;
        int i = g.Phrases.IndexOf(p), j = i + d;
        if (j < 0 || j >= g.Phrases.Count) return;
        g.Phrases.Move(i, j);
        if (PhraseItems.ItemContainerGenerator.ContainerFromItem(p) is FrameworkElement fe) Anim.Play(fe, d * 10);
    }

    private void PhraseDelete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup is not { } g || PhraseOf(sender) is not { } p) return;
        Anim.Remove(sender as FrameworkElement, () =>
        {
            g.Phrases.Remove(p);
            RefreshPreview();
        });
    }

    // ------------------------------------------------------------------ сокращения

    private void AddAbbrev_Click(object sender, RoutedEventArgs e) => _app.Phrases.Abbreviations.Add(new Abbreviation("", ""));

    private void AbbrevDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Abbreviation a)
            Anim.Remove(sender as FrameworkElement, () => _app.Phrases.Abbreviations.Remove(a));
    }

    // ------------------------------------------------------------------ словарь

    private bool _wordsDirty = true;

    /// <summary>Список выученных слов (до 2000 самых частых). Пока раздел скрыт — только помечаем, что список устарел.</summary>
    private void RefreshWords()
    {
        if (WordList is null) return;
        if (_section != "dict") { _wordsDirty = true; return; }
        _wordsDirty = false;
        var user = _app.Prediction.User;
        var q = Input.CaseHelper.Normalize(DictSearch.Text.Trim());
        var top = new PriorityQueue<(string Form, int Count), int>(2001);
        foreach (var w in user.AllWords())
        {
            if (q.Length > 0 && !w.Key.Contains(q, StringComparison.Ordinal)) continue;
            top.Enqueue((w.Form, w.Count), w.Count);
            if (top.Count > 2000) top.Dequeue();
        }
        var rows = new List<WordRow>(top.Count);
        while (top.Count > 0) { var (f, c) = top.Dequeue(); rows.Add(new WordRow(f, c)); }
        rows.Reverse();
        WordList.ItemsSource = rows;
        DictStats.Text = Loc.F("D.Stats", user.WordCount, (_app.Prediction.Russian?.Count ?? 0) + (_app.Prediction.English?.Count ?? 0));
        if (_section == "dict") Anim.Pulse(WordList);
    }

    private void DictSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void DeleteWord_Click(object sender, RoutedEventArgs e)
    {
        var rows = WordList.SelectedItems.OfType<WordRow>().ToList();
        if (rows.Count == 0) return;
        var containers = rows.Select(r => WordList.ItemContainerGenerator.ContainerFromItem(r) as FrameworkElement).ToList();
        int pending = containers.Count;
        foreach (var c in containers)
        {
            Anim.Remove(c, () =>
            {
                if (--pending > 0) return;
                _app.Prediction.User.RemoveMany(rows.Select(r => r.Form));
                RefreshWords();
            });
        }
    }

    private void ClearDict_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            ClearDictButton.Content = Loc.T("D.ClearConfirm");
            Anim.Pulse(ClearDictButton);
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                _clearArmed = false;
                ClearDictButton.Content = Loc.T("D.Clear");
                Anim.Pulse(ClearDictButton);
            };
            t.Start();
            return;
        }
        _clearArmed = false;
        ClearDictButton.Content = Loc.T("D.Clear");
        WordList.Animate(OpacityProperty, 0, Motion.Normal, Motion.EaseIn, completed: (_, _) =>
        {
            _app.Prediction.User.Clear();
            _app.SaveDictionary(true);
            RefreshWords();
        });
    }

    private async void ExportDict_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = "type9-dictionary.tsv", Filter = Loc.T("D.FilterDict") };
        if (dlg.ShowDialog(this) != true) return;
        var text = _app.Prediction.User.Serialize();
        try { await Task.Run(() => File.WriteAllText(dlg.FileName, text)); }
        catch (Exception ex) { DictStats.Text = Loc.F("D.Error", ex.Message); Anim.Play(DictStats, 4); }
    }

    // Импорт и обучение разбираются в фоне в отдельную модель (личный словарь трогаем только из UI-потока),
    // затем одним слиянием добавляются к словарю — большой файл не подвешивает подсказки и колесо
    private async void ImportDict_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("D.FilterDict") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var parsed = await Task.Run(() => UserModel.Parse(File.ReadAllText(dlg.FileName)));
            _app.Prediction.User.MergeFrom(parsed);
            _app.SaveDictionary(false);
            RefreshWords();
        }
        catch (Exception ex) { DictStats.Text = Loc.F("D.ImportFail", ex.Message); }
    }

    private void Train_Click(object sender, RoutedEventArgs e) => Train(TrainText.Text, null);

    private void TrainFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("D.FilterTxt"), Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        var files = dlg.FileNames;
        Train(null, files);
    }

    /// <param name="text">Текст из поля; или <paramref name="files"/> — файлы, прочитанные в фоне.</param>
    private async void Train(string? text, string[]? files)
    {
        try
        {
            var knownForm = _app.Prediction.User.KnownForm;
            var (model, n) = await Task.Run(() =>
            {
                var t = text ?? string.Join("\n", files!.Select(File.ReadAllText));
                var m = new UserModel { KnownForm = knownForm };
                return (m, string.IsNullOrWhiteSpace(t) ? -1 : m.Train(t));
            });
            if (n < 0) { TrainResult.Text = Loc.T("D.NoText"); Anim.Play(TrainResult, 4); return; }
            _app.Prediction.User.MergeFrom(model);
            _app.SaveDictionary(false);
            TrainResult.Text = Loc.F("D.Trained", n);
            Anim.Play(TrainResult, 4);
            RefreshWords();
        }
        catch (Exception ex) { TrainResult.Text = Loc.F("D.Error", ex.Message); Anim.Play(TrainResult, 4); }
    }

    // ------------------------------------------------------------------ исключения

    private void SaveExcluded()
    {
        _app.Settings.ExcludedProcesses = _excluded.ToList();
        _app.SettingsChanged();
    }

    private void AddExcluded(string name)
    {
        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0 || _excluded.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        _excluded.Add(name);
        ExclList.ScrollIntoView(name);
        SaveExcluded();
    }

    private void AddExcl_Click(object sender, RoutedEventArgs e)
    {
        AddExcluded(ExclInput.Text);
        ExclInput.Clear();
    }

    private void ExclInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddExcl_Click(sender, e);
    }

    private void RemoveExcl_Click(object sender, RoutedEventArgs e)
    {
        if (ExclList.SelectedItem is not string s) return;
        Anim.Remove(ExclList.ItemContainerGenerator.ContainerFromItem(s) as FrameworkElement, () =>
        {
            _excluded.Remove(s);
            SaveExcluded();
        });
    }

    private static bool SoftwareRendering =>
        SystemParameters.IsRemoteSession || (RenderCapability.Tier >> 16) == 0;

    private void PickProcess_Click(object sender, RoutedEventArgs e)
    {
        // Процессы с видимыми окнами верхнего уровня: один проход EnumWindows вместо MainWindowHandle у каждого процесса
        // (на сервере терминалов процессов сотни, и каждый MainWindowHandle перебирает все окна)
        var pids = Native.Win32.VisibleTopLevelProcessIds();
        pids.Remove((uint)Environment.ProcessId);
        var names = new List<string>();
        foreach (var pid in pids)
        {
            try { using var p = Process.GetProcessById((int)pid); names.Add(p.ProcessName); }
            catch { /* процесс уже завершился */ }
        }
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var menu = new ContextMenu { PlacementTarget = PickProcessButton, Placement = PlacementMode.Bottom, MaxHeight = 420 };
        foreach (var n in names)
        {
            var mi = new MenuItem { Header = n };
            mi.Click += (_, _) => AddExcluded(n);
            menu.Items.Add(mi);
        }
        if (names.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("E.NoWindows"), IsEnabled = false });
        menu.Opened += (_, _) => Anim.Play(menu, -6);
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ о программе

    private void UpdateLogos()
    {
        var accent = ThemeManager.ParseColor(_app.Settings.Accent);
        NavLogo.Source = LogoRenderer.CreateImage(1, false, accent);
        AboutLogo.Source = LogoRenderer.CreateImage(1, false, accent);
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(LogoRenderer.Render(64, false, accent));
    }

    /// <summary>Анимация логотипа: выделение пробегает по кругу и останавливается на «своём» секторе.</summary>
    private void LogoTick()
    {
        var accent = ThemeManager.ParseColor(_app.Settings.Accent);
        _logoStep++;
        AboutLogo.Source = LogoRenderer.CreateImage((1 + _logoStep) % 6, false, accent);
        if (_logoStep >= 6)
        {
            _logoTimer.Stop();
            AboutLogo.Source = LogoRenderer.CreateImage(1, false, accent);
            var st = new ScaleTransform(1, 1);
            AboutLogo.RenderTransform = st;
            st.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Slow, Motion.Spring, 0.92);
            st.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Slow, Motion.Spring, 0.92);
        }
    }

    private void BuildCheatSheet()
    {
        var s = _app.Settings;
        (string key, string what)[] rows =
        [
            ("Tab", Loc.T("C.Tab")),
            ("↑ / ↓", Loc.T("C.Arrows")),
            ("Esc", Loc.T("C.Esc")),
            (s.RadialTrigger == RadialTrigger.Hold ? Loc.F("C.Hold", s.RadialHotkey) : s.RadialHotkey, Loc.T("C.Wheel")),
            ("1–8", Loc.T("C.Digits")),
            (Loc.T("C.PagesKey"), Loc.T("C.Pages")),
            (s.PauseHotkey, Loc.T("C.Pause")),
        ];
        CheatSheet.Children.Clear();
        CheatSheet.RowDefinitions.Clear();
        CheatSheet.ColumnDefinitions.Clear();
        CheatSheet.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
        CheatSheet.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < rows.Length; i++)
        {
            CheatSheet.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(0, 3, 16, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            key.SetResourceReference(Border.BorderBrushProperty, "B.Line");
            key.SetResourceReference(Border.BackgroundProperty, "B.Surface2");
            key.Child = new TextBlock { Text = rows[i].key, FontSize = 12, FontWeight = FontWeights.Medium };
            Grid.SetRow(key, i);
            CheatSheet.Children.Add(key);
            var what = new TextBlock { Text = rows[i].what, Style = (Style)FindResource("T.Body"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 1);
            CheatSheet.Children.Add(what);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Paths.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.DataDir}\"") { UseShellExecute = true });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.Exit();
}
