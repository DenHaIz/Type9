using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Type9.Core;
using Type9.Native;
using Type9.Themes;
using Type9.UI.Controls;

namespace Type9.UI;

/// <summary>
/// Прозрачное окно поверх всего с круговым меню в позиции курсора. Не забирает фокус — текст уходит
/// в то поле, где пользователь печатал. Выбор — направлением мыши от центра (как в CS:GO).
/// </summary>
internal sealed class RadialMenuWindow : Window
{
    private readonly RadialView _view = new() { Opacity = 0 };
    private IntPtr _hwnd;
    private Win32.POINT _center;
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
        ApplyScale();
        UiScale.Changed += ApplyScale;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Content = _view;
        _view.HorizontalAlignment = HorizontalAlignment.Center;
        _view.VerticalAlignment = VerticalAlignment.Center;
        _view.RenderTransform = new ScaleTransform(1, 1);
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeNoActivate(_hwnd);
        };
    }

    private void ApplyScale()
    {
        double s = UiScale.Current;
        _view.LayoutTransform = new ScaleTransform(s, s);
        Width = (RadialView.ViewSize + 60) * s;
        Height = (RadialView.ViewSize + 60) * s;
    }

    public void Open(PhraseBook book)
    {
        _book = book;
        if (book.Groups.Count == 0) return;
        _page = Math.Clamp(_page, 0, book.Groups.Count - 1);
        _closing = false;
        IsOpen = true;

        _view.Build(book.Groups[_page], _page, book.Groups.Count);
        Win32.GetCursorPos(out _center);

        // Сначала ставим окно под курсор, потом показываем — без кадра на старом месте
        if (_hwnd != IntPtr.Zero) PlaceAtCursor();
        Show();
        PlaceAtCursor();
        _view.PlayOpen();
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    private void PlaceAtCursor()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.GetWindowRect(_hwnd, out var r);
        var mi = Win32.GetMonitorInfoAt(_center.X, _center.Y);
        int x = Math.Clamp(_center.X - r.Width / 2, mi.rcWork.Left - r.Width / 4, mi.rcWork.Right - r.Width * 3 / 4);
        int y = Math.Clamp(_center.Y - r.Height / 2, mi.rcWork.Top - r.Height / 4, mi.rcWork.Bottom - r.Height * 3 / 4);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        // Центр меню мог сместиться у края экрана — выбор считаем от фактического центра
        Win32.GetWindowRect(_hwnd, out r);
        _center = new Win32.POINT(r.Left + r.Width / 2, r.Top + r.Height / 2);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsOpen || _closing) return;
        Win32.GetCursorPos(out var p);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _view.Select(_view.HitIndex((p.X - _center.X) / scale, (p.Y - _center.Y) / scale));
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
        Close(result is not null);
        return result;
    }

    public void Cancel() => Close(false);

    private void Close(bool committed)
    {
        if (!IsOpen) return;
        IsOpen = false;
        _closing = true;
        CompositionTarget.Rendering -= OnFrame;
        // При выборе даём секунду подсветки «щелчку» — закрытие чуть позже
        _view.PlayClose(() => { if (!IsOpen) Hide(); });
    }
}
