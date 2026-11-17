using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Type9.Core;
using Type9.Native;
using Type9.Themes;
using Type9.UI.Controls;

namespace Type9.UI;

/// <summary>
/// Прозрачное окно поверх всего с круговым меню. Не забирает фокус — текст уходит в то поле, где пользователь печатал.
/// Окно всегда целиком на мониторе под курсором (с его DPI), не перекрывает строку с кареткой; если меню пришлось
/// сдвинуть, курсор мыши переносится в его центр и после закрытия возвращается на место.
/// </summary>
internal sealed class RadialMenuWindow : Window
{
    private const double ShadowMargin = 30; // поле под тень вокруг колеса (DIP)

    private readonly RadialView _view = new() { Opacity = 0 };
    private readonly ScaleTransform _scale = new(1, 1);
    private IntPtr _hwnd;
    private Win32.POINT _center;
    private Win32.POINT? _restoreCursor;
    private double _dpiScale = 1;
    private PhraseBook? _book;
    private int _page;
    private bool _closing;

    public bool IsOpen { get; private set; }

    public RadialMenuWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI");
        // Всё содержимое (вместе с полем под тень) — внутри масштаба, чтобы округление совпадало с пикселями
        _view.Margin = new Thickness(ShadowMargin);
        _view.HorizontalAlignment = HorizontalAlignment.Center;
        _view.VerticalAlignment = VerticalAlignment.Center;
        _view.RenderTransform = new ScaleTransform(1, 1);
        Content = new System.Windows.Controls.Grid { LayoutTransform = _scale, Children = { _view } };
        ApplyScale();
        UiScale.Changed += ApplyScale;
        UiScale.PixelAlign(this);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeNoActivate(_hwnd);
        };
    }

    private void ApplyScale()
    {
        double s = UiScale.Current;
        _scale.ScaleX = _scale.ScaleY = s;
        Width = (RadialView.ViewSize + 2 * ShadowMargin) * s;
        Height = (RadialView.ViewSize + 2 * ShadowMargin) * s;
    }

    /// <param name="caret">Каретка в поле ввода (физические пиксели), если известна точно — её строку меню не перекрывает.</param>
    public void Open(PhraseBook book, Win32.RECT? caret)
    {
        _book = book;
        if (book.Groups.Count == 0) return;
        _page = Math.Clamp(_page, 0, book.Groups.Count - 1);
        _closing = false;
        IsOpen = true;
        _view.Build(book.Groups[_page], _page, book.Groups.Count);

        Win32.GetCursorPos(out var cursor);
        var (work, dpi) = Win32.MonitorAt(cursor.X, cursor.Y);
        _dpiScale = dpi;
        int size = (int)Math.Round((RadialView.ViewSize + 2 * ShadowMargin) * UiScale.Current * dpi);
        var pos = Place(cursor, size, work, caret, dpi);

        // Окно ставится на нужный монитор ещё скрытым: WPF получает его DPI заранее, и меню не «прыгает»
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, pos.X, pos.Y, size, size, Win32.SWP_NOACTIVATE);
        Show();
        Win32.GetWindowRect(_hwnd, out var r);
        if (r.Width != size || r.Left != pos.X || r.Top != pos.Y)
        {
            pos = Place(cursor, r.Width, work, caret, dpi);
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, pos.X, pos.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            Win32.GetWindowRect(_hwnd, out r);
        }
        _center = new Win32.POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);

        // Меню сдвинуто относительно курсора (край экрана / строка текста) — переносим курсор в центр колеса
        _restoreCursor = null;
        if (Math.Abs(_center.X - cursor.X) > 2 || Math.Abs(_center.Y - cursor.Y) > 2)
        {
            _restoreCursor = cursor;
            Win32.SetCursorPos(_center.X, _center.Y);
        }

        _view.PlayOpen();
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>
    /// Позиция окна: по центру курсора, но целиком в рабочей области его монитора и не поверх строки с кареткой
    /// (тогда выше или ниже строки — где ближе и где помещается).
    /// </summary>
    internal static Win32.POINT Place(Win32.POINT cursor, int size, Win32.RECT work, Win32.RECT? caret, double dpi)
    {
        int x = cursor.X - size / 2, y = cursor.Y - size / 2;
        int inset = (int)(ShadowMargin * 0.6 * dpi); // прозрачное поле под тень можно «утопить» за край

        if (caret is { } c && c.Height > 0)
        {
            int pad = (int)(6 * dpi);
            int lineTop = c.Top - pad, lineBottom = c.Bottom + pad;
            bool overlaps = y + inset < lineBottom && y + size - inset > lineTop;
            if (overlaps)
            {
                int above = lineTop - size + inset;
                int below = lineBottom - inset;
                bool fitsAbove = above >= work.Top - inset;
                bool fitsBelow = below + size <= work.Bottom + inset;
                if (fitsAbove && fitsBelow)
                    y = Math.Abs(above + size / 2 - cursor.Y) <= Math.Abs(below + size / 2 - cursor.Y) ? above : below;
                else if (fitsAbove) y = above;
                else if (fitsBelow) y = below;
            }
        }

        x = Math.Clamp(x, work.Left - inset, Math.Max(work.Left - inset, work.Right - size + inset));
        y = Math.Clamp(y, work.Top - inset, Math.Max(work.Top - inset, work.Bottom - size + inset));
        return new Win32.POINT(x, y);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsOpen || _closing) return;
        Win32.GetCursorPos(out var p);
        double k = _dpiScale * UiScale.Current;
        _view.Select(_view.HitIndex((p.X - _center.X) / k, (p.Y - _center.Y) / k));
    }

    public void Page(int delta)
    {
        if (!IsOpen || _book is null || _book.Groups.Count < 2) return;
        _page = (_page + delta + _book.Groups.Count) % _book.Groups.Count;
        _view.SwitchPage(_book.Groups[_page], _page, _book.Groups.Count, delta);
    }

    /// <summary>Закрывает меню и возвращает выбранную фразу (или null).</summary>
    public Phrase? Commit(int? forcedIndex = null)
    {
        if (!IsOpen || _book is null) return null;
        int idx = forcedIndex ?? _view.Selected;
        Phrase? result = null;
        var group = _book.Groups.ElementAtOrDefault(_page);
        if (group is not null && idx >= 0 && idx < Math.Min(group.Phrases.Count, PhraseGroup.MaxPhrases))
        {
            if (forcedIndex is not null) _view.Select(idx);
            result = group.Phrases[idx];
        }
        CloseMenu();
        return result;
    }

    public void Cancel() => CloseMenu();

    private void CloseMenu()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _closing = true;
        CompositionTarget.Rendering -= OnFrame;
        if (_restoreCursor is { } rc) Win32.SetCursorPos(rc.X, rc.Y);
        _restoreCursor = null;
        _view.PlayClose(() => { if (!IsOpen) Hide(); });
    }
}
