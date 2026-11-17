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

    // Стабилизация: если каретку в этот раз определить не удалось, не прыгаем к мыши, а остаёмся у последней
    // известной позиции в том же поле (или у места последнего клика в этом окне — каретка обычно там)
    private sealed record Anchor(IntPtr Window, Win32.RECT Rect, long At);
    private Anchor? _lastPrecise;
    private volatile Anchor? _lastClick;

    public CaretLocator(System.Windows.Threading.Dispatcher ui)
    {
        _ui = ui;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Type9.Caret" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Клик мыши (из хука): запоминаем точку — при неизвестной каретке плашка встанет здесь.</summary>
    public void NoteClick(Win32.POINT pt) =>
        _lastClick = new Anchor(IntPtr.Zero, new Win32.RECT { Left = pt.X, Top = pt.Y - 9, Right = pt.X + 1, Bottom = pt.Y + 11 }, Environment.TickCount64);

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
                info = Fallback(Win32.GetForegroundWindow(), IntPtr.Zero, false);
            }
            _ui.BeginInvoke(() => r.Callback(r.Version, info));
        }
    }

    private CaretInfo Locate()
    {
        var fg = Win32.GetForegroundWindow();
        uint tid = Win32.GetWindowThreadProcessId(fg, out var pid);

        var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        bool haveGti = Win32.GetGUIThreadInfo(tid, ref gti);
        var focusHwnd = haveGti && gti.hwndFocus != IntPtr.Zero ? gti.hwndFocus : fg;

        if (haveGti && gti.hwndCaret != IntPtr.Zero && gti.rcCaret.Height > 0)
        {
            var tl = Win32.ClientToPhysical(gti.hwndCaret, new Win32.POINT(gti.rcCaret.Left, gti.rcCaret.Top));
            var br = Win32.ClientToPhysical(gti.hwndCaret, new Win32.POINT(gti.rcCaret.Right, gti.rcCaret.Bottom));
            var rect = new Win32.RECT { Left = tl.X, Top = tl.Y, Right = Math.Max(br.X, tl.X + 1), Bottom = Math.Max(br.Y, tl.Y + 1) };
            // Некоторые программы держат невидимую «служебную» каретку в углу или вне окна — такую не берём
            if ((tl.X != 0 || tl.Y != 0) && Inside(gti.hwndCaret, rect))
            {
                bool isPassword = false;
                // Qt (Telegram и др.) ставит системную каретку для экранного диктора, но её высота/вертикаль бывают неточными:
                // уточняем по UI Automation, иначе опускаем низ каретки до низа однострочного поля — плашка не закроет текст
                if (pid != _ownPid && IsQt(gti.hwndCaret) && !UiaRecentlyFailed(focusHwnd))
                {
                    var uia = TryUia(fg, out var field);
                    double sc = Scale(rect);
                    // Позиция от UIA берётся, только если она рядом с системной кареткой (защита от чужих координат)
                    if (uia is { Precise: true } u && Math.Abs(u.Rect.Left - rect.Left) < 3 * LineHeight(sc)
                        && Math.Abs(u.Rect.Top - rect.Top) < 3 * LineHeight(sc))
                        return Precise(focusHwnd, MinLine(u.Rect), u.IsPassword);
                    if (uia is null) NoteUiaFailed(focusHwnd);
                    isPassword = uia?.IsPassword ?? false;
                    if (field is { } f && f.Height < 3 * LineHeight(sc) && rect.Left >= f.Left - 4 && rect.Left <= f.Right + 4
                        && rect.Top >= f.Top - LineHeight(sc) && rect.Top <= f.Bottom)
                        rect.Bottom = Math.Max(rect.Bottom, f.Bottom - (int)(4 * sc));
                }
                return Precise(focusHwnd, MinLine(rect), isPassword);
            }
        }

        if (pid != _ownPid)
        {
            if (!UiaRecentlyFailed(focusHwnd))
            {
                var uia = TryUia(fg, out _);
                if (uia is { Precise: true } u) return Precise(focusHwnd, MinLine(u.Rect), u.IsPassword);
                if (uia is { } approx) return Fallback(fg, focusHwnd, approx.IsPassword, approx);
                NoteUiaFailed(focusHwnd);
            }
        }
        return Fallback(fg, focusHwnd, false);
    }

    private bool UiaRecentlyFailed(IntPtr focusHwnd) => focusHwnd == _uiaFailedHwnd && Environment.TickCount64 - _uiaFailedAt < 5000;

    private void NoteUiaFailed(IntPtr focusHwnd)
    {
        _uiaFailedHwnd = focusHwnd;
        _uiaFailedAt = Environment.TickCount64;
    }

    private static bool IsQt(IntPtr hwnd) =>
        Win32.GetClassName(hwnd) is var c && c.StartsWith("Qt", StringComparison.Ordinal) && c.Contains("QWindow");

    /// <summary>Масштаб монитора под кареткой (координаты уже физические).</summary>
    private static double Scale(Win32.RECT r) => Win32.MonitorAt(r.Left, r.Top).Scale;

    /// <summary>Высота строки обычного текста в физических пикселях.</summary>
    private static int LineHeight(double scale) => (int)Math.Round(18 * scale);

    /// <summary>
    /// Каретка ниже обычной строки (некоторые программы отдают высоту 1–2 px у верха строки) — дотягиваем низ,
    /// чтобы плашка не наезжала на набираемый текст.
    /// </summary>
    private static Win32.RECT MinLine(Win32.RECT r)
    {
        double scale = Scale(r);
        if (r.Height < (int)Math.Round(14 * scale)) r.Bottom = r.Top + LineHeight(scale);
        return r;
    }

    private CaretInfo Precise(IntPtr focusHwnd, Win32.RECT rect, bool isPassword)
    {
        _lastPrecise = new Anchor(focusHwnd, rect, Environment.TickCount64);
        return new CaretInfo(rect, true, isPassword);
    }

    /// <summary>
    /// Каретку точно не нашли. По убыванию надёжности: последняя точная позиция в этом же поле или клик в этом окне
    /// (что новее), приблизительная позиция от UI Automation, курсор мыши.
    /// </summary>
    private CaretInfo Fallback(IntPtr fg, IntPtr focusHwnd, bool isPassword, CaretInfo? approx = null)
    {
        var last = _lastPrecise is { } lp && lp.Window == focusHwnd && focusHwnd != IntPtr.Zero ? lp : null;
        var click = _lastClick is { } c && Environment.TickCount64 - c.At < 30 * 60_000 &&
                    Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT(c.Rect.Left, c.Rect.Top + 9)), Win32.GA_ROOT) == fg ? c : null;
        var best = last is null ? click : click is null ? last : click.At > last.At ? click : last;
        if (best is not null) return new CaretInfo(best.Rect, false, isPassword);
        if (approx is { } a) return a;
        if (isPassword) return new CaretInfo(default, false, true);
        return AtMouse();
    }

    private static bool Inside(IntPtr hwnd, Win32.RECT r)
    {
        if (!Win32.GetWindowRect(Win32.GetAncestor(hwnd, Win32.GA_ROOT), out var w)) return true;
        int cx = r.Left, cy = (r.Top + r.Bottom) / 2;
        return cx >= w.Left - 2 && cx <= w.Right + 2 && cy >= w.Top - 2 && cy <= w.Bottom + 2;
    }

    /// <param name="field">Границы поля ввода (физические пиксели), если оно не больше трёх строк.</param>
    private static CaretInfo? TryUia(IntPtr fg, out Win32.RECT? field)
    {
        field = null;
        var el = AutomationElement.FocusedElement;
        if (el is null) return null;

        try
        {
            var fb = el.Current.BoundingRectangle;
            if (!fb.IsEmpty && fb.Height is > 0 and < 200)
                field = new Win32.RECT { Left = (int)fb.Left, Top = (int)fb.Top, Right = (int)fb.Right, Bottom = (int)fb.Bottom };
        }
        catch { /* элемент мог исчезнуть */ }

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
                    if (rects.Length == 0)
                    {
                        // Каретка в самом начале текста — символ справа от неё
                        var r3 = range.Clone();
                        r3.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
                        rects = r3.GetBoundingRectangles();
                        rightEdge = false;
                    }
                    if (rects.Length > 0)
                    {
                        var r = rightEdge ? rects[^1] : rects[0];
                        int x = (int)(rightEdge ? r.Right : r.Left);
                        var rect = new Win32.RECT { Left = x, Top = (int)r.Top, Right = x + 1, Bottom = (int)r.Bottom };
                        if (r.Height > 0 && Inside(fg, rect))
                            return new CaretInfo(rect, true, isPassword);
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
