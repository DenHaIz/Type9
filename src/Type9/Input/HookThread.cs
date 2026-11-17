using Type9.Core;
using Type9.Native;

namespace Type9.Input;

/// <summary>
/// Отдельный поток с собственным циклом сообщений, на котором живут низкоуровневые хуки клавиатуры/мыши
/// и WinEvent-хук смены фокуса. Так занятость UI-потока никогда не задерживает ввод в системе.
/// </summary>
internal sealed class HookThread : IDisposable
{
    public delegate bool KeyHandler(int vk, uint scan, bool down, uint flags);
    public delegate bool MouseHandler(int msg, Win32.POINT pt, uint mouseData);

    private readonly KeyHandler _onKey;
    private readonly MouseHandler _onMouse;
    private readonly Action<uint, IntPtr> _onWinEvent;

    // Делегаты держим в полях, иначе GC соберёт их, пока Windows продолжает вызывать
    private readonly Win32.LowLevelProc _kbProc;
    private readonly Win32.LowLevelProc _msProc;
    private readonly Win32.WinEventProc _winEventProc;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _kbHook, _msHook, _evFocus, _evForeground;
    private readonly ManualResetEventSlim _started = new();

    public HookThread(KeyHandler onKey, MouseHandler onMouse, Action<uint, IntPtr> onWinEvent)
    {
        _onKey = onKey;
        _onMouse = onMouse;
        _onWinEvent = onWinEvent;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        _winEventProc = WinEventProc;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Type9.Hooks", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait(5000);
    }

    private void Run()
    {
        _threadId = Win32.GetCurrentThreadId();
        InstallHooks();

        const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        _evForeground = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0, flags);
        _evFocus = Win32.SetWinEventHook(Win32.EVENT_OBJECT_FOCUS, Win32.EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventProc, 0, 0, flags);
        _started.Set();

        // Сторож: Windows молча снимает LL-хук, если система однажды не дождалась ответа (нагрузка,
        // блокировка сеанса). Раз в минуту простоя переустанавливаем хуки — это дёшево и надёжно.
        var timer = Win32.SetTimer(IntPtr.Zero, UIntPtr.Zero, 60_000, IntPtr.Zero);
        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_TIMER && msg.hwnd == IntPtr.Zero)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastInput) > 60_000) InstallHooks();
                continue;
            }
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }

        Win32.KillTimer(IntPtr.Zero, timer);
        Win32.UnhookWindowsHookEx(_kbHook);
        Win32.UnhookWindowsHookEx(_msHook);
        Win32.UnhookWinEvent(_evFocus);
        Win32.UnhookWinEvent(_evForeground);
    }

    private long _lastInput = Environment.TickCount64;

    private void InstallHooks()
    {
        var hMod = Win32.GetModuleHandle(null);
        if (_kbHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_kbHook);
        if (_msHook != IntPtr.Zero) Win32.UnhookWindowsHookEx(_msHook);
        _kbHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _msHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _msProc, hMod, 0);
        if (_kbHook == IntPtr.Zero || _msHook == IntPtr.Zero)
            Log.Error($"Не удалось установить хуки ввода (код {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
        Interlocked.Exchange(ref _lastInput, Environment.TickCount64);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        Interlocked.Exchange(ref _lastInput, Environment.TickCount64);
        if (nCode >= 0)
        {
            try
            {
                var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
                if (data.dwExtraInfo != Win32.InjectionMarker)
                {
                    int m = wParam.ToInt32();
                    bool down = m is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
                    if (_onKey((int)data.vkCode, data.scanCode, down, data.flags))
                        return new IntPtr(1); // поглощаем клавишу
                }
            }
            catch (Exception ex) { Log.Error("KeyboardProc", ex); }
        }
        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int m = wParam.ToInt32();
                if (m != 0x0200) // WM_MOUSEMOVE не интересен — пропускаем без разбора структуры
                {
                    var data = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.MSLLHOOKSTRUCT>(lParam);
                    if (data.dwExtraInfo != Win32.InjectionMarker && _onMouse(m, data.pt, data.mouseData))
                        return new IntPtr(1);
                }
            }
            catch (Exception ex) { Log.Error("MouseProc", ex); }
        }
        return Win32.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    private void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try { _onWinEvent(evt, hwnd); }
        catch (Exception ex) { Log.Error("WinEventProc", ex); }
    }

    public void Dispose()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, Win32.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(1000);
        _started.Dispose();
    }
}
