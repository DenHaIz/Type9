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
    /// <param name="delayed">Одиночный модификатор: открыть, только если клавиша всё ещё зажата через ~140 мс.</param>
    void OnRadialOpen(bool delayed);
    void OnRadialCommit();
    void OnRadialCancel();
    void OnRadialDigit(int index);
    void OnRadialPage(int delta);
    void OnTogglePause();
    /// <summary>Сменилось окно/поле ввода: UI проверяет его на «пароль» через UI Automation.</summary>
    void OnFocusChanged();
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
    private bool _autoComma;   // запятая, поставленная нами после приветствия (Tab по «Здравствуйте»)
    private int _wheelAcc;     // накопленная дельта колеса над подсказкой

    /// <summary>Последняя вставка по Tab, пока Tab зажат: Tab+цифра заменяет её другим вариантом.</summary>
    private sealed record TabAccept(SuggestionSnapshot Snap, int Index, string Word);
    private TabAccept? _tabAccept;
    private bool _tabDown;
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

    /// <summary>Поле ввода оказалось паролем (определено через UI Automation): всё набранное выбрасываем, не обучаясь.</summary>
    public void MarkPasswordField()
    {
        _suppressed = true;
        lock (_sync)
        {
            _pending.Clear();
            _tracker.Reset();
        }
        _fieldVerified = true;
        _autoSpace = _autoComma = false;
        _snap = null;
        Changed();
    }

    /// <summary>Поле проверено (не пароль) — отложенное обучение можно фиксировать.</summary>
    public void MarkFieldVerified() => _fieldVerified = true;

    // Пока текущее поле не проверено на «пароль» через UI Automation, набранное не попадает в словарь
    private volatile bool _fieldVerified;
    private bool _capsDown, _pauseHeld;

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
        _autoSpace = _autoComma = false;
        Changed();
    }

    // ------------------------------------------------------------------ клавиатура

    private bool OnKey(int vk, uint scan, bool down, uint flags)
    {
        if (_captureMode) return false;

        var opt = _opt;
        var hk = opt.RadialHotkey;
        bool singleMod = hk.IsSingleModifier;

        // CapsLock переключает регистр по нажатию (не по автоповтору) и только если он сам не назначен на меню
        if (vk == Win32.VK_CAPITAL)
        {
            bool capsIsHotkey = hk.Key == Win32.VK_CAPITAL && hk.Modifiers == HotkeyModifiers.None && !_paused
                                && Hotkey.CurrentModifiers() == HotkeyModifiers.None;
            if (down && !_capsDown && !capsIsHotkey) _capsOn = !_capsOn;
            _capsDown = down;
        }

        if (vk == Win32.VK_TAB)
        {
            _tabDown = down;
            if (!down) _tabAccept = null;
        }

        if (!down)
        {
            if (_pauseHeld && vk == opt.PauseHotkey.Key) { _pauseHeld = false; return true; }
            return OnKeyUp(vk, opt);
        }
        if (_pauseHeld && vk == opt.PauseHotkey.Key) return true; // автоповтор клавиши паузы

        // автоповтор зажатого хоткея меню
        if (_hotkeyHeld && vk == _hotkeyKey) return !singleMod;

        var mods = Hotkey.CurrentModifiers();

        if (opt.PauseHotkey.Matches(vk, mods))
        {
            _pauseHeld = true;
            Post(_sink.OnTogglePause);
            if (mods.HasFlag(HotkeyModifiers.Alt) || mods.HasFlag(HotkeyModifiers.Win)) TextInjector.Dummy();
            return true;
        }
        if (_paused) return false;

        // Повторное нажатие хоткея в режиме «нажатие» закрывает меню (проверяем раньше клавиш управления меню)
        if (_radialOpen && opt.RadialTrigger == RadialTrigger.Toggle && !hk.IsMouse && !singleMod && hk.Matches(vk, mods))
        {
            TriggerRadial(vk, opt);
            return true;
        }
        if (_radialOpen && HandleRadialKey(vk)) return true;

        // Одиночный модификатор зажат и нажата другая клавиша — это обычное сочетание (RCtrl+C): меню не нужно
        if (_hotkeyHeld && singleMod && !Hotkey.IsModifierKey(vk))
        {
            _hotkeyHeld = false;
            _otherKeyDuringHold = true;
            if (_radialOpen) Post(_sink.OnRadialCancel);
        }

        if (!hk.IsMouse && hk.Matches(vk, mods))
        {
            TriggerRadial(vk, opt);
            if (singleMod)
            {
                // Сам модификатор пропускаем в систему; «пустая» клавиша гасит меню окна (Alt) и «Пуск» (Win)
                if (Hotkey.ModifierOf(vk) is HotkeyModifiers.Alt or HotkeyModifiers.Win) TextInjector.Dummy();
                return false;
            }
            if (mods.HasFlag(HotkeyModifiers.Alt) || mods.HasFlag(HotkeyModifiers.Win)) TextInjector.Dummy();
            return true;
        }

        // Tab зажат после вставки: цифра 1–9 — заменить вставленное этим вариантом; автоповтор Tab — не вставлять дальше
        if (_tabAccept is { } last && _tabDown)
        {
            if (vk == Win32.VK_TAB) return true;
            int digit = vk is >= '1' and <= '9' ? vk - '1'
                      : vk is > Win32.VK_NUMPAD0 and <= Win32.VK_NUMPAD9 ? vk - Win32.VK_NUMPAD0 - 1 : -1;
            if (digit >= 0 && mods == HotkeyModifiers.None)
            {
                if (digit < last.Snap.Items.Count && digit != last.Index) ReplaceAccepted(last, digit);
                return true;
            }
            if (!Hotkey.IsModifierKey(vk)) _tabAccept = null;
        }

        if (!opt.SuggestionsEnabled || _suppressed) return false;
        return HandleTyping(vk, scan, mods, opt);
    }

    /// <summary>Клавиша меню всё ещё зажата (для отложенного открытия при одиночном модификаторе).</summary>
    public bool IsHotkeyHeld => _hotkeyHeld;

    private bool _otherKeyDuringHold;

    private bool OnKeyUp(int vk, EngineOptions opt)
    {
        if (_hotkeyHeld && vk == _hotkeyKey)
        {
            _hotkeyHeld = false;
            bool singleMod = opt.RadialHotkey.IsSingleModifier;
            if (opt.RadialTrigger == RadialTrigger.Hold && _radialOpen) Post(_sink.OnRadialCommit);
            // Режим «нажатие» с одиночным модификатором: меню открывается по отпусканию, если это было «чистое» нажатие
            if (singleMod && opt.RadialTrigger == RadialTrigger.Toggle && !_otherKeyDuringHold)
                Post(_radialOpen ? _sink.OnRadialCancel : () => _sink.OnRadialOpen(false));
            return !singleMod;
        }
        if (vk == _hotkeyKey && _otherKeyDuringHold) return false;
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
        _otherKeyDuringHold = false;
        bool singleMod = opt.RadialHotkey.IsSingleModifier;
        if (singleMod && opt.RadialTrigger == RadialTrigger.Toggle) return; // решим по отпусканию
        if (opt.RadialTrigger == RadialTrigger.Toggle && _radialOpen) Post(_sink.OnRadialCancel);
        else
        {
            ClearSuggestionsAndHide();
            // Одиночный модификатор открывает меню с короткой задержкой: быстрые сочетания (RCtrl+C) его не вызывают
            Post(() => _sink.OnRadialOpen(singleMod));
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
                    string word;
                    lock (_sync) word = _tracker.GetContext().Word;
                    Accept(snap!, snap!.Selected);
                    _tabAccept = new TabAccept(snap, snap.Selected, word);
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
            // Ctrl+V, Alt+Tab... — текст изменился непредсказуемо. Ctrl+Backspace / Ctrl+Z — исправление: не учим
            ResetTracking(commit: vk is not (Win32.VK_BACK or 'Z'));
            return false;
        }

        switch (vk)
        {
            case Win32.VK_BACK:
                lock (_sync)
                {
                    _tracker.Backspace();
                    ForgetEdited();
                }
                _autoSpace = _autoComma = false;
                Changed();
                return false;
            case Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN or Win32.VK_HOME or Win32.VK_END
                or Win32.VK_PRIOR or Win32.VK_NEXT or Win32.VK_DELETE or Win32.VK_INSERT or Win32.VK_ESCAPE or Win32.VK_TAB:
                // ←, Home, Delete — обычно возврат к только что набранному для правки: такое не запоминаем
                ResetTracking(commit: vk is not (Win32.VK_LEFT or Win32.VK_HOME or Win32.VK_DELETE));
                return false;
            case >= Win32.VK_F1 and <= Win32.VK_F24:
                return false;
        }

        var ch = TranslateKey(vk, scan, mods, altGr);
        if (ch is null) return false;
        char c = ch.Value;

        // «Слово_» + запятая → «Слово,» : убираем авто-пробел перед знаком препинания.
        // «Здравствуйте,_» + «!» → «Здравствуйте!» : свою запятую тоже заменяем набранным знаком
        bool dropSpace = _autoSpace && _tracker.LastCharIs(' ');
        bool dropComma = _autoComma && c is ',' or '.' or '!' or '?' or ':' or ';';
        if (dropComma || (dropSpace && opt.SmartPunctuation && c is ',' or '.' or '!' or '?' or ':' or ';' or ')'))
        {
            int erase = (dropSpace ? 1 : 0) + (dropComma ? 1 : 0);
            _autoSpace = _autoComma = false;
            TextInjector.Type(c.ToString(), erase, false);
            lock (_sync)
            {
                for (int i = 0; i < erase; i++) _tracker.Backspace();
                ForgetEdited();
                Queue(_tracker.Feed(c));
            }
            FlushLearning(false);
            Changed();
            return true;
        }

        _autoSpace = _autoComma = false;
        lock (_sync) Queue(_tracker.Feed(c));
        FlushLearning(false);
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

        lock (_sync)
        {
            for (int i = 0; i < s.Backspaces; i++) _tracker.Backspace();
            ForgetEdited();
            foreach (var c in s.Insert) Queue(_tracker.Feed(c));
        }
        FlushLearning(false);

        _autoSpace = s.Insert.EndsWith(' ');
        _autoComma = s.Insert.TrimEnd(' ').EndsWith(',');
        _snap = null;
        Changed();
    }

    /// <summary>
    /// Tab+цифра: вместо только что вставленного варианта — вариант n. До вставки в поле было слово W;
    /// вариант i стёр последние Backspaces(i) букв W и напечатал Insert(i). Стираем вставку (и при необходимости
    /// ещё буквы W), возвращаем стёртые буквы, которые нужны варианту n, и печатаем его.
    /// </summary>
    private void ReplaceAccepted(TabAccept last, int n)
    {
        var s = last.Snap.Items[last.Index];
        var t = last.Snap.Items[n];
        var w = last.Word;
        int bs = Math.Min(s.Backspaces, w.Length), bt = Math.Min(t.Backspaces, w.Length);
        int erase = s.Insert.Length + Math.Max(0, bt - bs);
        string text = (bs > bt ? w.Substring(w.Length - bs, bs - bt) : "") + t.Insert;
        TextInjector.Type(text, erase, _opt.NewlineAsShiftEnter);

        lock (_sync)
        {
            for (int i = 0; i < erase; i++) _tracker.Backspace();
            ForgetEdited();
            foreach (var c in text) Queue(_tracker.Feed(c));
        }
        FlushLearning(false);

        _autoSpace = t.Insert.EndsWith(' ');
        _autoComma = t.Insert.TrimEnd(' ').EndsWith(',');
        _tabAccept = last with { Index = n };
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
        // Колесо над подсказкой листает варианты (страница под ней при этом не прокручивается).
        // Дельты копятся до «щелчка» 120 — тачпад с мелкими шагами не пролистывает всё разом
        if (msg == Win32.WM_MOUSEWHEEL && _snap is { } snap && snap.Version == Version && snap.Items.Count > 1 && PopupRect.Contains(pt.X, pt.Y))
        {
            _wheelAcc += (short)(mouseData >> 16);
            int steps = _wheelAcc / 120;
            _wheelAcc -= steps * 120;
            int sel = Math.Clamp(snap.Selected - steps, 0, snap.Items.Count - 1);
            if (sel != snap.Selected)
            {
                _snap = snap with { Selected = sel };
                Post(() => _sink.OnSelectionChanged(sel));
            }
            return true;
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
        _fieldVerified = false; // новое поле: пока UI Automation не подтвердит, что это не пароль, ничего не учим
        EvaluateSuppression();
        Post(_sink.OnFocusChanged);
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

    // ------------------------------------------------------------------ отложенное обучение
    // Слово запоминается, когда пользователь уже начал следующее слово (CommitDistance символов после него),
    // при паузе в наборе (CommitPendingLearning из UI через ~1,5 с) или при смене поля. Если он сразу вернулся
    // Backspace и исправил слово (опечатка), ошибочная версия в словарь не попадёт.

    private const int CommitDistance = 3;
    private readonly List<(CompletedWord Word, long End)> _pending = [];

    /// <summary>Под _sync: слово завершено разделителем; End — позиция сразу после слова.</summary>
    private void Queue(CompletedWord? w)
    {
        if (w is not { } word || !word.Learnable || !_opt.LearnEnabled) return;
        _pending.Add((word, _tracker.Position - 1));
        if (_pending.Count > 64) _pending.RemoveAt(0); // поле так и не проверено — не копим бесконечно
    }

    /// <summary>Под _sync: Backspace дошёл до слова — оно редактируется, не учим.</summary>
    private void ForgetEdited() => _pending.RemoveAll(p => _tracker.Position <= p.End);

    /// <summary>Пауза в наборе — фиксируем всё отложенное (вызывается из UI по таймеру).</summary>
    public void CommitPendingLearning() => FlushLearning(all: true);

    private void FlushLearning(bool all)
    {
        if (!_fieldVerified) return; // ждём проверки поля на «пароль»
        List<CompletedWord>? ready = null;
        lock (_sync)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!all && _tracker.Position - _pending[i].End < CommitDistance) continue;
                (ready ??= []).Add(_pending[i].Word);
                _pending.RemoveAt(i--);
            }
        }
        if (ready is not null) Post(() => { foreach (var w in ready) _sink.OnLearn(w); });
    }

    /// <param name="commit">true — набранное окончательно (ушли из поля/строки); false — правка, отложенное выбрасываем.</param>
    private void ResetTracking(bool commit = true)
    {
        if (commit && _fieldVerified) FlushLearning(all: true);
        else lock (_sync) _pending.Clear();
        bool hadSomething;
        lock (_sync)
        {
            hadSomething = _tracker.Tail.Length > 0;
            _tracker.Reset();
        }
        _autoSpace = _autoComma = false;
        _tabAccept = null;
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
