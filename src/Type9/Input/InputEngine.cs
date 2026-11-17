using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using Type9.Core;
using Type9.Native;
using Type9.Prediction;

namespace Type9.Input;

/// <summary>Получатель событий движка ввода. Все методы вызываются в UI-потоке.</summary>
internal interface IInputSink
{
    void OnContextChanged(int version, TypingContext ctx);
    void OnSelectionChanged(int selected);
    void OnHideSuggestions();
    void OnLearn(CompletedWord word);
    void OnRadialOpen();
    void OnRadialCommit();
    void OnRadialCancel();
    void OnRadialDigit(int index);
    void OnRadialPage(int delta);
    void OnTogglePause();
}

internal sealed record SuggestionSnapshot(int Version, IReadOnlyList<Suggestion> Items, int Selected);

/// <summary>Параметры, которые читает поток хуков (неизменяемый снимок настроек).</summary>
internal sealed record EngineOptions(
    bool SuggestionsEnabled, bool LearnEnabled, bool SmartPunctuation, bool NewlineAsShiftEnter, bool HideInFullscreen,
    Hotkey RadialHotkey, RadialTrigger RadialTrigger, Hotkey PauseHotkey, HashSet<string> Excluded)
{
    public static EngineOptions From(AppSettings s) => new(
        s.SuggestionsEnabled, s.LearnEnabled, s.SmartPunctuation, s.NewlineAsShiftEnter, s.HideInFullscreen,
        Hotkey.Parse(s.RadialHotkey), s.RadialTrigger, Hotkey.Parse(s.PauseHotkey),
        new HashSet<string>(s.ExcludedProcesses.Select(p => p.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)),
            StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Сердце программы: разбирает глобальный ввод (в потоке хуков), ведёт буфер набора,
/// решает, какие клавиши поглотить, и сообщает UI о смене контекста и командах меню.
/// </summary>
internal sealed class InputEngine : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly IInputSink _sink;
    private readonly HookThread _hooks;
    private readonly object _sync = new();
    private readonly TypingTracker _tracker = new();
    private readonly byte[] _keyState = new byte[256];
    private readonly StringBuilder _charBuf = new(8);
    private readonly Dictionary<uint, string> _procNames = new();

    private int _version;
    private volatile SuggestionSnapshot? _snap;
    private volatile EngineOptions _opt;
    private volatile bool _paused;
    private volatile bool _suppressed;
    private volatile bool _radialOpen;
    private volatile bool _captureMode;
    private Win32.RECT _popupRect;
    private readonly object _rectSync = new();

    private bool _capsOn;
    private bool _autoSpace;
    private bool _hotkeyHeld;
    private int _hotkeyKey;
    private bool _swallowLUp, _swallowRUp;

    public InputEngine(Dispatcher ui, IInputSink sink, EngineOptions options)
    {
        _ui = ui;
        _sink = sink;
        _opt = options;
        _capsOn = (Win32.GetKeyState(Win32.VK_CAPITAL) & 1) != 0;
        _hooks = new HookThread(OnKey, OnMouse, OnWinEvent);
    }

    public void Start() => _hooks.Start();

    public EngineOptions Options { get => _opt; set { _opt = value; EvaluateSuppression(); } }
    public bool Paused { get => _paused; set { _paused = value; ResetTracking(); } }
    public bool RadialOpen { get => _radialOpen; set => _radialOpen = value; }

    /// <summary>Режим записи хоткея в настройках: весь ввод пропускается без обработки.</summary>
    public bool CaptureMode { get => _captureMode; set => _captureMode = value; }

    /// <summary>Поле ввода оказалось паролем (определено через UI Automation).</summary>
    public void MarkPasswordField()
    {
        _suppressed = true;
        ResetTracking();
    }

    public Win32.RECT PopupRect
    {
        get { lock (_rectSync) return _popupRect; }
        set { lock (_rectSync) _popupRect = value; }
    }

    public int Version => Volatile.Read(ref _version);

    /// <summary>UI сообщает рассчитанные подсказки. Принимаются, только если контекст не успел измениться.</summary>
    public bool SetSuggestions(int version, IReadOnlyList<Suggestion> items)
    {
        if (version != Version) return false;
        _snap = items.Count > 0 ? new SuggestionSnapshot(version, items, 0) : null;
        return true;
    }

    public void ClearSuggestions() => _snap = null;

    /// <summary>Принять подсказку кликом мыши (UI-поток).</summary>
    public void AcceptFromUi(int index)
    {
        var snap = _snap;
        if (snap is null || snap.Version != Version || index < 0 || index >= snap.Items.Count) return;
        Accept(snap, index);
    }

    /// <summary>Текст вставлен программой (фраза из меню) — учитываем его в контексте.</summary>
    public void NotifyInserted(string text)
    {
        lock (_sync)
        {
            foreach (var c in text) _tracker.Feed(c);
        }
        _autoSpace = false;
        Changed();
    }

    // ------------------------------------------------------------------ клавиатура

    private bool OnKey(int vk, uint scan, bool down, uint flags)
    {
        if (_captureMode) return false;

        if (vk == Win32.VK_CAPITAL && down && (flags & Win32.LLKHF_INJECTED) == 0) _capsOn = !_capsOn;

        var opt = _opt;
        if (!down) return OnKeyUp(vk, opt);

        // автоповтор зажатого хоткея меню
        if (_hotkeyHeld && vk == _hotkeyKey) return true;

        var mods = Hotkey.CurrentModifiers();

        if (opt.PauseHotkey.Matches(vk, mods))
        {
            Post(_sink.OnTogglePause);
            if (mods.HasFlag(HotkeyModifiers.Alt) || mods.HasFlag(HotkeyModifiers.Win)) TextInjector.Dummy();
            return true;
        }
        if (_paused) return false;

        if (_radialOpen && HandleRadialKey(vk)) return true;

        if (!opt.RadialHotkey.IsMouse && opt.RadialHotkey.Matches(vk, mods))
        {
            TriggerRadial(vk, opt);
            if (mods.HasFlag(HotkeyModifiers.Alt) || mods.HasFlag(HotkeyModifiers.Win)) TextInjector.Dummy();
            return true;
        }

        if (!opt.SuggestionsEnabled || _suppressed) return false;
        return HandleTyping(vk, scan, mods, opt);
    }

    private bool OnKeyUp(int vk, EngineOptions opt)
    {
        if (_hotkeyHeld && vk == _hotkeyKey)
        {
            _hotkeyHeld = false;
            if (opt.RadialTrigger == RadialTrigger.Hold && _radialOpen) Post(_sink.OnRadialCommit);
            return true;
        }
        // В режиме удержания отпускание модификатора хоткея тоже завершает выбор
        if (_hotkeyHeld && opt.RadialTrigger == RadialTrigger.Hold && _radialOpen)
        {
            var m = Hotkey.ModifierOf(vk);
            if (m != HotkeyModifiers.None && opt.RadialHotkey.Modifiers.HasFlag(m)) Post(_sink.OnRadialCommit);
        }
        return false;
    }

    private void TriggerRadial(int key, EngineOptions opt)
    {
        _hotkeyHeld = true;
        _hotkeyKey = key;
        if (opt.RadialTrigger == RadialTrigger.Toggle && _radialOpen) Post(_sink.OnRadialCancel);
        else
        {
            ClearSuggestionsAndHide();
            Post(_sink.OnRadialOpen);
        }
    }

    private bool HandleRadialKey(int vk)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                Post(_sink.OnRadialCancel);
                return true;
            case >= 0x31 and <= 0x38:
                { int i = vk - 0x31; Post(() => _sink.OnRadialDigit(i)); return true; }
            case >= Win32.VK_NUMPAD0 + 1 and <= Win32.VK_NUMPAD0 + 8:
                { int i = vk - Win32.VK_NUMPAD0 - 1; Post(() => _sink.OnRadialDigit(i)); return true; }
            case Win32.VK_LEFT:
                Post(() => _sink.OnRadialPage(-1));
                return true;
            case Win32.VK_RIGHT:
                Post(() => _sink.OnRadialPage(1));
                return true;
            case Win32.VK_RETURN:
            case Win32.VK_SPACE:
                Post(_sink.OnRadialCommit);
                return true;
        }
        return false;
    }

    private bool HandleTyping(int vk, uint scan, HotkeyModifiers mods, EngineOptions opt)
    {
        var snap = _snap;
        bool visible = snap is not null && snap.Version == Version;

        if (visible && mods == HotkeyModifiers.None)
        {
            switch (vk)
            {
                case Win32.VK_TAB:
                    Accept(snap!, snap!.Selected);
                    return true;
                case Win32.VK_DOWN or Win32.VK_UP when snap!.Items.Count > 1:
                    int n = snap.Items.Count;
                    int sel = (snap.Selected + (vk == Win32.VK_DOWN ? 1 : n - 1)) % n;
                    _snap = snap with { Selected = sel };
                    Post(() => _sink.OnSelectionChanged(sel));
                    return true;
                case Win32.VK_ESCAPE:
                    ClearSuggestionsAndHide();
                    return true;
            }
        }

        if (Hotkey.IsModifierKey(vk) || vk == Win32.VK_CAPITAL) return false;

        bool altGr = mods.HasFlag(HotkeyModifiers.Ctrl) && mods.HasFlag(HotkeyModifiers.Alt);
        if ((mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) != 0 && !altGr)
        {
            ResetTracking(); // Ctrl+V, Ctrl+Backspace, Alt+Tab... — текст изменился непредсказуемо
            return false;
        }

        switch (vk)
        {
            case Win32.VK_BACK:
                lock (_sync) _tracker.Backspace();
                _autoSpace = false;
                Changed();
                return false;
            case Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN or Win32.VK_HOME or Win32.VK_END
                or Win32.VK_PRIOR or Win32.VK_NEXT or Win32.VK_DELETE or Win32.VK_INSERT or Win32.VK_ESCAPE or Win32.VK_TAB:
                ResetTracking();
                return false;
            case >= Win32.VK_F1 and <= Win32.VK_F24:
                return false;
        }

        var ch = TranslateKey(vk, scan, mods, altGr);
        if (ch is null) return false;
        char c = ch.Value;

        // «Слово_» + запятая → «Слово,» : убираем авто-пробел перед знаком препинания
        if (_autoSpace && opt.SmartPunctuation && c is ',' or '.' or '!' or '?' or ':' or ';' or ')' && _tracker.LastCharIs(' '))
        {
            _autoSpace = false;
            TextInjector.Type(c.ToString(), 1, false);
            CompletedWord? done;
            lock (_sync)
            {
                _tracker.Backspace();
                done = _tracker.Feed(c);
            }
            if (done is { } w0 && opt.LearnEnabled) Post(() => _sink.OnLearn(w0));
            Changed();
            return true;
        }

        _autoSpace = false;
        CompletedWord? completed;
        lock (_sync) completed = _tracker.Feed(c);
        if (completed is { } w && opt.LearnEnabled) Post(() => _sink.OnLearn(w));
        Changed();
        return false;
    }

    private char? TranslateKey(int vk, uint scan, HotkeyModifiers mods, bool altGr)
    {
        Array.Clear(_keyState);
        if (mods.HasFlag(HotkeyModifiers.Shift)) _keyState[Win32.VK_SHIFT] = 0x80;
        if (_capsOn) _keyState[Win32.VK_CAPITAL] = 0x01;
        if (altGr)
        {
            _keyState[Win32.VK_CONTROL] = 0x80;
            _keyState[Win32.VK_MENU] = 0x80;
        }

        var fg = Win32.GetForegroundWindow();
        var hkl = Win32.GetKeyboardLayout(Win32.GetWindowThreadProcessId(fg, out _));
        _charBuf.Clear();
        // флаг 0x4: не менять состояние клавиатуры ядра (не ломает мёртвые клавиши в целевом приложении)
        int n = Win32.ToUnicodeEx((uint)vk, scan, _keyState, _charBuf, _charBuf.Capacity, 0x4, hkl);
        if (n != 1) return null;
        char c = _charBuf[0];
        if (c == '\r') return '\n';
        if (c < 0x20 && c != '\t') return null;
        return c;
    }

    private void Accept(SuggestionSnapshot snap, int index)
    {
        var s = snap.Items[index];
        var opt = _opt;
        TextInjector.Type(s.Insert, s.Backspaces, opt.NewlineAsShiftEnter);

        var learned = new List<CompletedWord>(2);
        lock (_sync)
        {
            for (int i = 0; i < s.Backspaces; i++) _tracker.Backspace();
            foreach (var c in s.Insert)
                if (_tracker.Feed(c) is { } w) learned.Add(w);
        }
        if (opt.LearnEnabled && learned.Count > 0)
            Post(() => { foreach (var w in learned) _sink.OnLearn(w); });

        _autoSpace = s.Insert.EndsWith(' ');
        _snap = null;
        Changed();
    }

    // ------------------------------------------------------------------ мышь

    private bool OnMouse(int msg, Win32.POINT pt, uint mouseData)
    {
        if (_captureMode) return false;
        var opt = _opt;

        int buttonVk = msg switch
        {
            Win32.WM_MBUTTONDOWN or Win32.WM_MBUTTONUP => Win32.VK_MBUTTON,
            Win32.WM_XBUTTONDOWN or Win32.WM_XBUTTONUP => (mouseData >> 16) == 1 ? Win32.VK_XBUTTON1 : Win32.VK_XBUTTON2,
            _ => 0,
        };

        if (buttonVk != 0 && !_paused && opt.RadialHotkey.IsMouse)
        {
            bool isDown = msg is Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN;
            if (isDown && opt.RadialHotkey.Matches(buttonVk, Hotkey.CurrentModifiers()))
            {
                TriggerRadial(buttonVk, opt);
                return true;
            }
            if (!isDown && _hotkeyHeld && buttonVk == _hotkeyKey)
            {
                _hotkeyHeld = false;
                if (opt.RadialTrigger == RadialTrigger.Hold && _radialOpen) Post(_sink.OnRadialCommit);
                return true;
            }
        }

        if (_radialOpen)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEWHEEL:
                    int delta = (short)(mouseData >> 16);
                    Post(() => _sink.OnRadialPage(delta > 0 ? -1 : 1));
                    return true;
                case Win32.WM_LBUTTONDOWN:
                    _swallowLUp = true;
                    Post(_sink.OnRadialCommit);
                    return true;
                case Win32.WM_RBUTTONDOWN:
                    _swallowRUp = true;
                    Post(_sink.OnRadialCancel);
                    return true;
            }
        }
        if (msg == Win32.WM_LBUTTONUP && _swallowLUp) { _swallowLUp = false; return true; }
        if (msg == Win32.WM_RBUTTONUP && _swallowRUp) { _swallowRUp = false; return true; }

        if (msg is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
        {
            if (PopupRect.Contains(pt.X, pt.Y)) return false; // клик по самой подсказке
            ResetTracking();
        }
        return false;
    }

    // ------------------------------------------------------------------ фокус

    private void OnWinEvent(uint evt, IntPtr hwnd)
    {
        ResetTracking();
        EvaluateSuppression();
    }

    private void EvaluateSuppression()
    {
        try
        {
            var opt = _opt;
            var fg = Win32.GetForegroundWindow();
            uint tid = Win32.GetWindowThreadProcessId(fg, out var pid);

            bool excluded = opt.Excluded.Count > 0 && opt.Excluded.Contains(ProcessName(pid));

            bool password = false;
            var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
            if (Win32.GetGUIThreadInfo(tid, ref gti) && gti.hwndFocus != IntPtr.Zero)
            {
                long style = Win32.GetWindowLongPtr(gti.hwndFocus, Win32.GWL_STYLE).ToInt64();
                password = (style & Win32.ES_PASSWORD) != 0 &&
                           Win32.GetClassName(gti.hwndFocus).Contains("edit", StringComparison.OrdinalIgnoreCase);
            }

            bool fullscreen = opt.HideInFullscreen && IsFullscreen(fg);
            _suppressed = excluded || password || fullscreen;
        }
        catch (Exception ex) { Log.Error("EvaluateSuppression", ex); }
    }

    private string ProcessName(uint pid)
    {
        lock (_procNames)
        {
            if (_procNames.TryGetValue(pid, out var name)) return name;
            try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
            catch { name = ""; }
            if (_procNames.Count > 256) _procNames.Clear();
            return _procNames[pid] = name;
        }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || hwnd == Win32.GetShellWindow() || hwnd == Win32.GetDesktopWindow()) return false;
        var cls = Win32.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        var mi = Win32.GetMonitorInfoAt(r.Left + r.Width / 2, r.Top + r.Height / 2);
        // Окно без рамок, покрывающее весь монитор (игры, презентации, видео)
        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        bool hasCaption = (style & 0x00C00000) == 0x00C00000;
        return !hasCaption && r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ------------------------------------------------------------------ служебное

    private void ResetTracking()
    {
        bool hadSomething;
        lock (_sync)
        {
            hadSomething = _tracker.Tail.Length > 0;
            _tracker.Reset();
        }
        _autoSpace = false;
        if (hadSomething || _snap is not null) Changed();
    }

    private void ClearSuggestionsAndHide()
    {
        _snap = null;
        Interlocked.Increment(ref _version);
        Post(_sink.OnHideSuggestions);
    }

    private void Changed()
    {
        TypingContext ctx;
        int v;
        lock (_sync)
        {
            v = Interlocked.Increment(ref _version);
            ctx = _tracker.GetContext();
        }
        Post(() => _sink.OnContextChanged(v, ctx));
    }

    private void Post(Action a) => _ui.BeginInvoke(DispatcherPriority.Normal, a);

    public void Dispose() => _hooks.Dispose();
}
