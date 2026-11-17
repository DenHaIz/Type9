using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Type9.Core;
using Type9.Themes;

namespace Type9.UI.Controls;

/// <summary>
/// Круговое меню фраз (стиль buy-menu CS:GO / chat wheel Dota 2). Используется и во всплывающем окне,
/// и как живое превью в настройках.
/// </summary>
public sealed class RadialView : Grid
{
    public const double ViewSize = 440;
    private const double ROut = 206, RIn = 84, Gap = 6, Push = 7;
    private static readonly Point C = new(ViewSize / 2, ViewSize / 2);

    private sealed class Sector
    {
        public required Canvas Host;
        public required SolidColorBrush Fill;
        public required SolidColorBrush LabelBrush;
        public required SolidColorBrush NumberBrush;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required double Mid;
    }

    private Canvas _sectorLayer;
    private readonly Grid _content = new();
    private readonly TextBlock _title;
    private readonly TextBlock _preview;
    private readonly StackPanel _dots;
    private readonly Ellipse _hubRing;
    private readonly List<Sector> _sectors = [];
    private PhraseGroup? _group;
    private int _selected = -1;

    /// <summary>Подсветка под курсором мыши (для превью в настройках).</summary>
    public bool InteractiveHover { get; set; }
    public event Action<int>? SectorClicked;

    public int Count => _sectors.Count;
    public int Selected => _selected;

    public RadialView()
    {
        Width = ViewSize;
        Height = ViewSize;
        RenderTransformOrigin = new Point(0.5, 0.5);

        var disc = new Ellipse
        {
            Width = ViewSize, Height = ViewSize,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black },
        };
        disc.SetResourceReference(Shape.FillProperty, "B.Disc");
        disc.SetResourceReference(Shape.StrokeProperty, "B.Line");
        disc.StrokeThickness = 1;
        // Тень считается один раз и хранится как текстура — анимации не пересчитывают размытие каждый кадр
        disc.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        Children.Add(disc);

        _sectorLayer = NewLayer();
        _content.Children.Add(_sectorLayer);
        Children.Add(_content);

        // Центр: название группы, превью выбранной фразы, индикатор страниц
        _hubRing = new Ellipse { Width = (RIn - 8) * 2, Height = (RIn - 8) * 2, StrokeThickness = 1.5 };
        _hubRing.SetResourceReference(Shape.FillProperty, "B.Surface");
        _hubRing.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        Children.Add(_hubRing);

        _title = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "B.AccentFg");
        _preview = new TextBlock
        {
            FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 128, MaxHeight = 66, Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
        _dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        center.Children.Add(_title);
        center.Children.Add(_preview);
        center.Children.Add(_dots);
        Children.Add(center);

        ThemeManager.Changed += Recolor;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) => { if (InteractiveHover) Select(-1); };
        MouseLeftButtonUp += (_, _) => { if (_selected >= 0) SectorClicked?.Invoke(_selected); };
    }

    private static Canvas NewLayer() => new()
    {
        Width = ViewSize, Height = ViewSize, RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new RotateTransform(0) } },
    };

    public void Build(PhraseGroup? group, int page, int pageCount)
    {
        _group = group;
        _selected = -1;
        _sectors.Clear();
        _content.Children.Clear();
        _sectorLayer = NewLayer();
        _content.Children.Add(_sectorLayer);

        var phrases = group?.Phrases.Take(PhraseGroup.MaxPhrases).ToList() ?? [];
        int n = phrases.Count;
        for (int i = 0; i < n; i++)
            _sectors.Add(CreateSector(phrases[i], i, n));
        UpdateCenter(group, page, pageCount, n);
    }

    private void UpdateCenter(PhraseGroup? group, int page, int pageCount, int n)
    {
        _title.Text = group?.Name?.ToUpperInvariant() ?? "";
        _preview.Text = Loc.T(n == 0 ? "R.Empty" : "R.Hover");
        _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");
        _hubRing.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");

        _dots.Children.Clear();
        if (pageCount > 1)
        {
            for (int i = 0; i < pageCount; i++)
            {
                var dot = new Border { Width = i == page ? 14 : 5, Height = 5, CornerRadius = new CornerRadius(2.5), Margin = new Thickness(2, 0, 2, 0) };
                dot.SetResourceReference(Border.BackgroundProperty, i == page ? "B.AccentFg" : "B.Line");
                _dots.Children.Add(dot);
            }
        }
    }

    private Sector CreateSector(Phrase phrase, int i, int n)
    {
        var theme = Palette();
        var fill = new SolidColorBrush(theme.sector);
        var labelBrush = new SolidColorBrush(theme.fg);
        var numberBrush = new SolidColorBrush(theme.muted);
        var shift = new TranslateTransform();
        var scale = new ScaleTransform(1, 1);

        var host = new Canvas
        {
            Width = ViewSize, Height = ViewSize,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, shift } },
        };

        var path = new Path { Data = RadialGeometry.Sector(C, RIn, ROut, i, n, Gap), Fill = fill, StrokeThickness = 1 };
        path.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        host.Children.Add(path);

        double mid = RadialGeometry.SectorCenter(i, n);
        double span = 360.0 / n;
        double rLabel = (RIn + ROut) / 2 + 4;
        double chord = n == 1 ? 200 : 2 * rLabel * Math.Sin(Math.Min(span, 120) * Math.PI / 360);
        double maxW = Math.Clamp(chord * 0.86, 70, 150);

        var label = new TextBlock
        {
            Text = phrase.Caption, Foreground = labelBrush, FontSize = 13, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = maxW, MaxHeight = 54, IsHitTestVisible = false,
        };
        label.Measure(new Size(maxW, 54));
        var lp = n == 1 ? new Point(C.X, C.Y - rLabel) : RadialGeometry.At(C, rLabel, mid);
        Canvas.SetLeft(label, lp.X - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, lp.Y - label.DesiredSize.Height / 2);
        host.Children.Add(label);

        var num = new TextBlock { Text = (i + 1).ToString(), Foreground = numberBrush, FontSize = 10.5, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        num.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Номер — в «переднем» углу сектора у внешнего края, чтобы не пересекаться с подписью
        double numAngle = n == 1 ? 0 : mid - span / 2 + Math.Min(span / 2 - 4, 9);
        var np = RadialGeometry.At(C, ROut - 14, numAngle);
        Canvas.SetLeft(num, np.X - num.DesiredSize.Width / 2);
        Canvas.SetTop(num, np.Y - num.DesiredSize.Height / 2);
        host.Children.Add(num);

        _sectorLayer.Children.Add(host);
        return new Sector { Host = host, Fill = fill, LabelBrush = labelBrush, NumberBrush = numberBrush, Shift = shift, Scale = scale, Mid = mid };
    }

    private (Color sector, Color fg, Color muted, Color accent, Color onAccent) Palette() => (
        ThemeManager.Brush("Sector").Color, ThemeManager.Brush("Fg").Color, ThemeManager.Brush("FgMuted").Color,
        ThemeManager.Brush("Accent").Color, ThemeManager.Brush("OnAccent").Color);

    public void Select(int index, bool animate = true)
    {
        if (index >= _sectors.Count) index = -1;
        if (index == _selected) return;
        var c = Palette();
        var d = animate ? Motion.Fast : new Duration(TimeSpan.Zero);

        if (_selected >= 0) ApplyStyle(_sectors[_selected], false, c, d);
        _selected = index;
        if (_selected >= 0)
        {
            ApplyStyle(_sectors[_selected], true, c, d);
            var p = _group!.Phrases[_selected];
            _preview.Text = p.Text.Replace("\r", "").TrimEnd();
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
            _hubRing.SetResourceReference(Shape.StrokeProperty, "B.Accent");
        }
        else
        {
            _preview.Text = Loc.T(_sectors.Count == 0 ? "R.Empty" : "R.Hover");
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");
            _hubRing.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        }
    }

    private static void ApplyStyle(Sector s, bool on, (Color sector, Color fg, Color muted, Color accent, Color onAccent) c, Duration d)
    {
        var ease = on ? Motion.Spring : Motion.EaseOut;
        s.Fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? c.accent : c.sector, d) { EasingFunction = Motion.EaseOut });
        s.LabelBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? c.onAccent : c.fg, d));
        s.NumberBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? c.onAccent : c.muted, d));
        var target = on ? RadialGeometry.At(new Point(0, 0), Push, s.Mid) : new Point(0, 0);
        var dur = on ? new Duration(TimeSpan.FromMilliseconds(160)) : d;
        s.Shift.Animate(TranslateTransform.XProperty, target.X, dur, ease);
        s.Shift.Animate(TranslateTransform.YProperty, target.Y, dur, ease);
    }

    private void Recolor()
    {
        var c = Palette();
        for (int i = 0; i < _sectors.Count; i++)
            ApplyStyle(_sectors[i], i == _selected, c, Motion.Slow);
    }

    /// <summary>Появление: диск «раскрывается», сектора разлетаются веером.</summary>
    public void PlayOpen()
    {
        this.Animate(OpacityProperty, 1, Motion.Normal, Motion.EaseOut, 0);
        var scale = RenderTransform as ScaleTransform ?? (ScaleTransform)(RenderTransform = new ScaleTransform(1, 1));
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Normal, Motion.Spring, 0.86);
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Normal, Motion.Spring, 0.86);

        for (int i = 0; i < _sectors.Count; i++)
        {
            var s = _sectors[i];
            var delay = TimeSpan.FromMilliseconds(12 * i);
            s.Host.BeginAnimation(OpacityProperty, Motion.To(1, Motion.Normal, Motion.EaseOut, 0, delay));
            s.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, Motion.Normal, Motion.Spring, 0.9, delay));
            s.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, Motion.Normal, Motion.Spring, 0.9, delay));
        }
    }

    public void PlayClose(Action? done = null)
    {
        var scale = RenderTransform as ScaleTransform ?? (ScaleTransform)(RenderTransform = new ScaleTransform(1, 1));
        scale.Animate(ScaleTransform.ScaleXProperty, 0.92, Motion.Fast, Motion.EaseIn);
        scale.Animate(ScaleTransform.ScaleYProperty, 0.92, Motion.Fast, Motion.EaseIn);
        this.Animate(OpacityProperty, 0, Motion.Fast, Motion.EaseIn, completed: (_, _) => done?.Invoke());
    }

    /// <summary>
    /// Смена страницы без мерцания: старая страница поворачивается и растворяется, новая одновременно
    /// въезжает с другой стороны. Обе на время анимации кешируются как текстуры (ровно на 60 Гц).
    /// </summary>
    public void SwitchPage(PhraseGroup? group, int page, int pageCount, int direction)
    {
        var old = _sectorLayer;
        var oldSelected = _selected;
        _group = group;
        _selected = -1;
        _sectors.Clear();
        _sectorLayer = NewLayer();
        _content.Children.Add(_sectorLayer);

        var phrases = group?.Phrases.Take(PhraseGroup.MaxPhrases).ToList() ?? [];
        for (int i = 0; i < phrases.Count; i++) _sectors.Add(CreateSector(phrases[i], i, phrases.Count));
        UpdateCenter(group, page, pageCount, phrases.Count);
        Anim.Pulse(_title);

        var dur = new Duration(TimeSpan.FromMilliseconds(260));
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        old.IsHitTestVisible = false;
        old.CacheMode = new BitmapCache(dpi);
        var (os, or) = Parts(old);
        or.Animate(RotateTransform.AngleProperty, -direction * 32, dur, Motion.EaseOut);
        os.Animate(ScaleTransform.ScaleXProperty, 0.9, dur, Motion.EaseOut);
        os.Animate(ScaleTransform.ScaleYProperty, 0.9, dur, Motion.EaseOut);
        old.Animate(OpacityProperty, 0, new Duration(TimeSpan.FromMilliseconds(180)), Motion.EaseOut,
            completed: (_, _) => _content.Children.Remove(old));

        var layer = _sectorLayer;
        layer.CacheMode = new BitmapCache(dpi);
        var (ns, nr) = Parts(layer);
        nr.Animate(RotateTransform.AngleProperty, 0, dur, Motion.EaseOutStrong, direction * 32);
        ns.Animate(ScaleTransform.ScaleXProperty, 1, dur, Motion.EaseOutStrong, 0.9);
        ns.Animate(ScaleTransform.ScaleYProperty, 1, dur, Motion.EaseOutStrong, 0.9);
        layer.Animate(OpacityProperty, 1, dur, Motion.EaseOut, 0, completed: (_, _) =>
        {
            if (layer == _sectorLayer) layer.CacheMode = null; // вернуть чёткий текст после анимации
        });
        _ = oldSelected;
    }

    private static (ScaleTransform, RotateTransform) Parts(Canvas layer)
    {
        var g = (TransformGroup)layer.RenderTransform;
        return ((ScaleTransform)g.Children[0], (RotateTransform)g.Children[1]);
    }

    /// <summary>Совместимость с превью настроек: мягкое проявление текущей страницы.</summary>
    public void PlayPage(int direction)
    {
        var (s, r) = Parts(_sectorLayer);
        r.Animate(RotateTransform.AngleProperty, 0, Motion.Slow, Motion.EaseOutStrong, direction * -20);
        _sectorLayer.Animate(OpacityProperty, 1, Motion.Normal, Motion.EaseOut, 0.2);
        s.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Slow, Motion.EaseOutStrong, 0.94);
        s.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Slow, Motion.EaseOutStrong, 0.94);
    }

    /// <summary>Индекс сектора по смещению от центра в DIP.</summary>
    public int HitIndex(double dx, double dy, double deadZone = 34) =>
        RadialGeometry.IndexFromVector(dx, dy, _sectors.Count, deadZone);

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!InteractiveHover) return;
        var p = e.GetPosition(this);
        double dx = p.X - C.X, dy = p.Y - C.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        Select(dist > ROut + 4 ? -1 : HitIndex(dx, dy, RIn - 8));
    }
}
