using System.Windows.Automation;
using System.Windows.Automation.Text;
using Type9.Core;
using Type9.Native;

namespace Type9.Input;

internal readonly record struct CaretInfo(Win32.RECT Rect, bool Precise, bool IsPassword);

/// <summary>
/// Определяет позицию каретки в чужом окне: GetGUIThreadInfo (Win32/Office) → UI Automation (браузеры,
/// Electron, UWP) → курсор мыши. Работает в отдельном потоке: UIA-вызовы в чужие процессы бывают медленными.
/// Обрабатывается только самый свежий запрос.
/// </summary>
internal sealed class CaretLocator : IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly Thread _thread;
    private readonly AutoResetEvent _signal = new(false);
    private readonly int _ownPid = Environment.ProcessId;
    private (int Version, Action<int, CaretInfo> Callback)? _pending;
    private volatile bool _stop;

    private IntPtr _uiaFailedHwnd;
    private long _uiaFailedAt;

    public CaretLocator(System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Type9.Caret" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Request(int version, Action<int, CaretInfo> callback)
    {
        lock (_signal) _pending = (version, callback);
        _signal.Set();
    }

    private void Loop()
    {
        while (!_stop)
        {
            _signal.WaitOne();
            if (_stop) break;
            (int Version, Action<int, CaretInfo> Callback)? req;
            lock (_signal) { req = _pending; _pending = null; }
            if (req is not { } r) continue;

            CaretInfo info;
            try { info = Locate(); }
            catch (Exception ex)
            {
                Log.Error("CaretLocator", ex);
                info = AtMouse();
            }
            _ui.BeginInvoke(() => r.Callback(r.Version, info));
        }
    }

    private CaretInfo Locate()
    {
        var fg = Win32.GetForegroundWindow();
        uint tid = Win32.GetWindowThreadProcessId(fg, out var pid);

        var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (Win32.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero && gti.rcCaret.Height > 0)
        {
            var tl = new Win32.POINT(gti.rcCaret.Left, gti.rcCaret.Top);
            var br = new Win32.POINT(gti.rcCaret.Right, gti.rcCaret.Bottom);
            Win32.ClientToScreen(gti.hwndCaret, ref tl);
            Win32.ClientToScreen(gti.hwndCaret, ref br);
            if (tl.X != 0 || tl.Y != 0)
                return new CaretInfo(new Win32.RECT { Left = tl.X, Top = tl.Y, Right = Math.Max(br.X, tl.X + 1), Bottom = br.Y }, true, false);
        }

        if (pid != _ownPid)
        {
            var focusHwnd = gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;
            bool recentlyFailed = focusHwnd == _uiaFailedHwnd && Environment.TickCount64 - _uiaFailedAt < 5000;
            if (!recentlyFailed)
            {
                var uia = TryUia();
                if (uia is { } u) return u;
                _uiaFailedHwnd = focusHwnd;
                _uiaFailedAt = Environment.TickCount64;
            }
        }
        return AtMouse();
    }

    private static CaretInfo? TryUia()
    {
        var el = AutomationElement.FocusedElement;
        if (el is null) return null;

        bool isPassword = false;
        try { isPassword = el.Current.IsPassword; } catch { /* ignore */ }

        if (el.TryGetCurrentPattern(TextPattern.Pattern, out var obj) && obj is TextPattern tp)
        {
            try
            {
                var sel = tp.GetSelection();
                if (sel.Length > 0)
                {
                    var range = sel[0];
                    var rects = range.GetBoundingRectangles();
                    bool rightEdge = false;
                    if (rects.Length == 0)
                    {
                        // Пустой диапазон каретки: берём символ слева от неё
                        var r2 = range.Clone();
                        r2.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
                        rects = r2.GetBoundingRectangles();
                        rightEdge = true;
                    }
                    if (rects.Length > 0)
                    {
                        var r = rects[^1];
                        int x = (int)(rightEdge ? r.Right : r.Left);
                        return new CaretInfo(new Win32.RECT { Left = x, Top = (int)r.Top, Right = x + 1, Bottom = (int)r.Bottom }, true, isPassword);
                    }
                }
            }
            catch (Exception) { /* элемент мог исчезнуть */ }
        }

        var b = el.Current.BoundingRectangle;
        if (!b.IsEmpty && b.Height is > 0 and < 80)
            return new CaretInfo(new Win32.RECT { Left = (int)b.Left + 8, Top = (int)b.Top, Right = (int)b.Left + 9, Bottom = (int)b.Bottom }, false, isPassword);

        return isPassword ? new CaretInfo(default, false, true) : null;
    }

    private static CaretInfo AtMouse()
    {
        Win32.GetCursorPos(out var p);
        return new CaretInfo(new Win32.RECT { Left = p.X, Top = p.Y + 4, Right = p.X + 1, Bottom = p.Y + 20 }, false, false);
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
    }
}
