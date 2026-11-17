using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Type9.Input;
using Type9.Native;
using Type9.Prediction;
using Type9.Themes;

namespace Type9.UI;

/// <summary>Плашка с вариантами у каретки. Не активируется и не перехватывает фокус.</summary>
internal sealed class SuggestionPopup : Window
{
    private const double ItemH = 30, Pad = 4, ShadowMargin = 14;

    private readonly Grid _root;
    private readonly TranslateTransform _rootShift = new();
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly BitmapCache _shadowCache = new();
    private readonly StackPanel _items = new();
    private readonly Border _highlight;
    private readonly TranslateTransform _highlightShift = new();
    private readonly TextBlock _hint;
    private IntPtr _hwnd;
    private bool _visible;
    private int _selected;
    private IReadOnlyList<Suggestion> _current = [];

    public event Action<int>? ItemClicked;

    public bool IsShown => _visible;

    public SuggestionPopup()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI");

        var shadow = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black },
            CacheMode = _shadowCache, // тень не пересчитывается при анимации появления
        };
        shadow.SetResourceReference(Border.BackgroundProperty, "B.Surface");
        shadow.SetResourceReference(Border.BorderBrushProperty, "B.Line");

        _highlight = new Border
        {
            Height = ItemH, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(7),
            RenderTransform = _highlightShift,
        };
        _highlight.SetResourceReference(Border.BackgroundProperty, "B.AccentSoft");

        var accentBar = new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 0, 0) };
        accentBar.SetResourceReference(Border.BackgroundProperty, "B.AccentFg");
        _highlight.Child = accentBar;

        var list = new Grid { Margin = new Thickness(Pad) };
        list.Children.Add(_highlight);
        list.Children.Add(_items);

        _hint = new TextBlock { FontSize = 11.5, Margin = new Thickness(12, 0, 12, 6) };
        _hint.SetResourceReference(TextBlock.TextProperty, "S.Pop.Hint");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");

        var stack = new StackPanel { MinWidth = 190 };
        stack.Children.Add(list);
        stack.Children.Add(_hint);

        _root = new Grid { Margin = new Thickness(ShadowMargin), RenderTransform = _rootShift, Opacity = 0 };
        UiScale.AnimateTo(_scale, false);
        UiScale.Changed += () => UiScale.AnimateTo(_scale, false);
        _root.Children.Add(shadow);
        _root.Children.Add(stack);
        // Масштаб — на самом внешнем элементе (вместе с полем под тень): округление совпадает с пикселями
        Content = new Grid { LayoutTransform = _scale, Children = { _root } };
        UiScale.PixelAlign(this);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            Win32.MakeNoActivate(_hwnd);
        };
    }

    public void SetItems(IReadOnlyList<Suggestion> items, int selected)
    {
        _current = items;
        _items.Children.Clear();
        for (int i = 0; i < items.Count; i++) _items.Children.Add(CreateRow(items[i], i));
        _hint.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Select(selected, animate: false);
    }

    private FrameworkElement CreateRow(Suggestion s, int index)
    {
        var row = new Grid { Height = ItemH, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 10, 0), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
        if (s.Kind == SuggestionKind.NextWord)
        {
            var arrow = new System.Windows.Documents.Run("→ ") { FontSize = 12 };
            arrow.SetResourceReference(TextElement.ForegroundProperty, "B.FgMuted");
            text.Inlines.Add(arrow);
        }
        if (s.Typed.Length > 0)
        {
            var typed = new System.Windows.Documents.Run(s.Typed);
            typed.SetResourceReference(TextElement.ForegroundProperty, "B.FgMuted");
            text.Inlines.Add(typed);
        }
        var rest = new System.Windows.Documents.Run(s.Rest) { FontWeight = FontWeights.SemiBold };
        rest.SetResourceReference(TextElement.ForegroundProperty, "B.Fg");
        text.Inlines.Add(rest);
        row.Children.Add(text);

        var tag = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1) };
        tag.SetResourceReference(Border.BorderBrushProperty, "B.Line");
        var tagText = new TextBlock { Text = Badge(index), FontSize = 11.5 };
        tagText.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");
        tag.Child = tagText;
        tag.Tag = "badge";
        Grid.SetColumn(tag, 1);
        row.Children.Add(tag);

        row.MouseLeftButtonDown += (_, e) => { e.Handled = true; ItemClicked?.Invoke(index); };
        // Наведение — только лёгкая подсветка; выбранный для Tab вариант меняют лишь ↑/↓ (иначе UI и Tab расходятся)
        var hover = new Border { CornerRadius = new CornerRadius(7), Opacity = 0, IsHitTestVisible = false };
        hover.SetResourceReference(Border.BackgroundProperty, "B.Hover");
        Grid.SetColumnSpan(hover, 2);
        row.Children.Insert(0, hover);
        row.MouseEnter += (_, _) => hover.Animate(OpacityProperty, 1, Motion.Fast, Motion.EaseOut);
        row.MouseLeave += (_, _) => hover.Animate(OpacityProperty, 0, Motion.Normal, Motion.EaseOut);
        return row;
    }

    public void Select(int index, bool animate = true)
    {
        _selected = Math.Clamp(index, 0, Math.Max(0, _current.Count - 1));
        double y = _selected * ItemH;
        if (animate) _highlightShift.Animate(TranslateTransform.YProperty, y, Motion.Normal, Motion.EaseOutStrong);
        else
        {
            _highlightShift.BeginAnimation(TranslateTransform.YProperty, null);
            _highlightShift.Y = y;
        }
        // Подсказка клавиши «Tab» — у выбранного варианта
        for (int i = 0; i < _items.Children.Count; i++)
        {
            if (_items.Children[i] is Grid g && g.Children.OfType<Border>().FirstOrDefault(b => "badge".Equals(b.Tag)) is { Child: TextBlock tb })
                tb.Text = Badge(i);
        }
    }

    /// <summary>Клавиша варианта: у выбранного — Tab, у остальных — Tab+номер.</summary>
    private string Badge(int i) =>
        i != _selected ? $"Tab+{i + 1}" : _current[i].Kind == SuggestionKind.Abbreviation ? "Tab ⇢" : "Tab";

    /// <summary>Показывает плашку под (или над) кареткой. Координаты — физические пиксели экрана.</summary>
    public void ShowAt(CaretInfo caret, bool fixedCorner)
    {
        if (!_visible)
        {
            Show();
            _visible = true;
            _root.Animate(OpacityProperty, 1, Motion.Normal, Motion.EaseOut);
            _rootShift.Animate(TranslateTransform.YProperty, 0, Motion.Normal, Motion.EaseOutStrong, -6);
        }
        else if (_root.Opacity < 1)
        {
            _root.Animate(OpacityProperty, 1, Motion.Fast, Motion.EaseOut);
        }

        UpdateLayout();
        if (_hwnd == IntPtr.Zero) return;
        // Текстура тени — в разрешении экрана с учётом масштаба интерфейса (иначе на 125–150% она размыта)
        _shadowCache.RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double scale = UiScale.DeviceScale(this);
        int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
        int m = (int)(ShadowMargin * UiScale.Current * scale);

        var anchorX = caret.Rect.Left;
        var anchorY = caret.Rect.Bottom;
        var mi = Win32.GetMonitorInfoAt(anchorX, anchorY);
        var work = mi.rcWork;

        int x, y;
        if (fixedCorner)
        {
            x = work.Right - w + m - (int)(16 * scale);
            y = work.Bottom - h + m - (int)(16 * scale);
        }
        else
        {
            x = anchorX - m - (int)(10 * scale);
            y = anchorY - m + (int)(6 * scale);
            if (y + h - m > work.Bottom) y = caret.Rect.Top - h + m - (int)(6 * scale); // не помещается снизу — над кареткой
            x = Math.Clamp(x, work.Left - m, Math.Max(work.Left - m, work.Right - w + m));
            y = Math.Max(y, work.Top - m);
        }
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public Win32.RECT ScreenRect
    {
        get
        {
            if (!_visible || _hwnd == IntPtr.Zero) return default;
            Win32.GetWindowRect(_hwnd, out var r);
            return r;
        }
    }

    public void HideAnimated()
    {
        if (!_visible) return;
        _visible = false;
        _root.Animate(OpacityProperty, 0, Motion.Fast, Motion.EaseIn, completed: (_, _) =>
        {
            if (!_visible) Hide();
        });
    }
}
