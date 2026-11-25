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
    private const double ROut = 206, RIn = 84, Gap = 6;
    private static readonly Point C = new(ViewSize / 2, ViewSize / 2);

    private sealed class Sector
    {
        public required Canvas Host;     // фон сектора (под подсветкой)
        public required Canvas Labels;   // подпись и номер (над подсветкой)
        public required SolidColorBrush Fill;
        public required SolidColorBrush LabelBrush;
        public required SolidColorBrush NumberBrush;
        public required ScaleTransform Scale;
        public double Level, Target;     // 0 — обычная подпись, 1 — подпись на подсветке
    }

    // Подсветка выбора — одна фигура на слой, которая «перетекает» к выбранному сектору поворотом.
    // Анимируется только поворот и прозрачность: дёшево даже на встроенной графике, ровно на 60 Гц.
    private Path _hl = new();
    private RotateTransform _hlRotate = new();
    private RadialGradientBrush _hlBrush = new();

    // Выбор анимируется покадрово (а не раскадровками): угол и прозрачность подсветки, цвет подписей плавно
    // догоняют цель. При быстром ведении мыши ничего не перезапускается и не «дёргается».
    private double _hlAngle, _hlAngleTarget, _hlOpacity, _hlOpacityTarget;
    private bool _ticking;
    private TimeSpan _lastTick;
    private (Color sector, Color fg, Color muted, Color accent, Color onAccent) _pal;

    private readonly BitmapCache _discCache = new() { SnapsToDevicePixels = true };
    private readonly Ellipse _disc;

    /// <summary>
    /// Облегчённая отрисовка для превью в настройках: без размытой тени и кеша-текстуры. Внутри масштабирующего
    /// Viewbox такая связка на некоторых видеодрайверах подвешивала отрисовку всего окна.
    /// </summary>
    public bool Lightweight
    {
        get => _disc.Effect is null;
        set
        {
            _disc.Effect = value ? null : new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black };
            _disc.CacheMode = value ? null : _discCache;
        }
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

    private double _textBoost = 1;

    /// <summary>Увеличение текста — для уменьшенного превью в настройках, чтобы подписи оставались читаемыми.</summary>
    public double TextBoost
    {
        get => _textBoost;
        set
        {
            _textBoost = Math.Clamp(value, 1, 2);
            _title.FontSize = 12 * _textBoost;
            _title.MaxWidth = _preview.MaxWidth = 140;
            _preview.FontSize = 12 * _textBoost;
            _preview.MaxHeight = 66 * _textBoost;
        }
    }

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

        var disc = _disc = new Ellipse
        {
            Width = ViewSize, Height = ViewSize,
            Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black },
        };
        disc.SetResourceReference(Shape.FillProperty, "B.Disc");
        disc.SetResourceReference(Shape.StrokeProperty, "B.Line");
        disc.StrokeThickness = 1;
        // Тень считается один раз и хранится как текстура — анимации не пересчитывают размытие каждый кадр
        disc.CacheMode = _discCache;
        Children.Add(disc);

        _sectorLayer = NewLayer();
        _content.Children.Add(_sectorLayer);
        Children.Add(_content);

        // Центр: название группы, превью выбранной фразы, индикатор страниц
        _hubRing = new Ellipse { Width = (RIn - 8) * 2, Height = (RIn - 8) * 2, StrokeThickness = 1.5 };
        _hubRing.SetResourceReference(Shape.FillProperty, "B.Surface");
        _hubRing.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        Children.Add(_hubRing);

        _title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 };
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

        // Подписка на статическое событие только пока элемент в дереве — иначе закрытое окно настроек не освободится
        Loaded += (_, _) => { ThemeManager.Changed -= Recolor; ThemeManager.Changed += Recolor; };
        Unloaded += (_, _) => ThemeManager.Changed -= Recolor;
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
        int n = Populate(group);
        UpdateCenter(group, page, pageCount, n);
    }

    /// <summary>Наполняет текущий слой: фоны секторов → подсветка → подписи.</summary>
    private int Populate(PhraseGroup? group)
    {
        var phrases = group?.Phrases.Take(PhraseGroup.MaxPhrases).ToList() ?? [];
        int n = phrases.Count;
        for (int i = 0; i < n; i++)
            _sectors.Add(CreateSector(phrases[i], i, n));
        CreateHighlight(n);
        foreach (var s in _sectors) _sectorLayer.Children.Add(s.Labels);
        return n;
    }

    private void CreateHighlight(int n)
    {
        _hlRotate = new RotateTransform(0, C.X, C.Y);
        _hlAngle = _hlAngleTarget = _hlOpacity = _hlOpacityTarget = 0;
        _hlBrush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, Center = C, GradientOrigin = C, RadiusX = ROut, RadiusY = ROut,
            GradientStops = { new GradientStop(default, RIn / ROut), new GradientStop(default, 1) },
        };
        ColorHighlight(Palette());
        _hl = new Path
        {
            Data = RadialGeometry.Sector(C, RIn, ROut, 0, Math.Max(n, 1), Gap), Fill = _hlBrush,
            RenderTransform = _hlRotate, Opacity = 0, IsHitTestVisible = false,
        };
        _sectorLayer.Children.Add(_hl);
    }

    /// <summary>Лёгкий перелив: у центра акцент чуть темнее, к краю — светлее.</summary>
    private void ColorHighlight((Color sector, Color fg, Color muted, Color accent, Color onAccent) c)
    {
        _hlBrush.GradientStops[0].Color = ThemeManager.Mix(c.accent, Colors.Black, 0.12);
        _hlBrush.GradientStops[1].Color = ThemeManager.Mix(c.accent, Colors.White, 0.12);
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
        var scale = new ScaleTransform(1, 1);

        var host = new Canvas
        {
            Width = ViewSize, Height = ViewSize,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };
        var labels = new Canvas
        {
            Width = ViewSize, Height = ViewSize, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale,
        };

        var path = new Path { Data = RadialGeometry.Sector(C, RIn, ROut, i, n, Gap), Fill = fill, StrokeThickness = 1 };
        path.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        host.Children.Add(path);

        double mid = RadialGeometry.SectorCenter(i, n);
        double span = 360.0 / n;
        double rLabel = (RIn + ROut) / 2 + 4;
        double chord = n == 1 ? 200 : 2 * rLabel * Math.Sin(Math.Min(span, 120) * Math.PI / 360);
        double maxW = Math.Clamp(chord * (_textBoost > 1 ? 1.0 : 0.86), 70, 150 * _textBoost);

        var label = new TextBlock
        {
            Text = phrase.Caption, Foreground = labelBrush, FontSize = 13 * _textBoost, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = maxW, MaxHeight = 54 * _textBoost, IsHitTestVisible = false,
        };
        label.Measure(new Size(maxW, 54 * _textBoost));
        var lp = n == 1 ? new Point(C.X, C.Y - rLabel) : RadialGeometry.At(C, rLabel, mid);
        Canvas.SetLeft(label, lp.X - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, lp.Y - label.DesiredSize.Height / 2);
        labels.Children.Add(label);

        var num = new TextBlock { Text = (i + 1).ToString(), Foreground = numberBrush, FontSize = 11.5 * (1 + (_textBoost - 1) / 2), FontWeight = FontWeights.SemiBold, IsHitTestVisible = false };
        num.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Номер — в «переднем» углу сектора у внешнего края, чтобы не пересекаться с подписью
        double numAngle = n == 1 ? 0 : mid - span / 2 + Math.Min(span / 2 - 4, 9);
        var np = RadialGeometry.At(C, ROut - 14, numAngle);
        Canvas.SetLeft(num, np.X - num.DesiredSize.Width / 2);
        Canvas.SetTop(num, np.Y - num.DesiredSize.Height / 2);
        labels.Children.Add(num);

        _sectorLayer.Children.Add(host);
        return new Sector { Host = host, Labels = labels, Fill = fill, LabelBrush = labelBrush, NumberBrush = numberBrush, Scale = scale };
    }

    private (Color sector, Color fg, Color muted, Color accent, Color onAccent) Palette() => (
        ThemeManager.Brush("Sector").Color, ThemeManager.Brush("Fg").Color, ThemeManager.Brush("FgMuted").Color,
        ThemeManager.Brush("Accent").Color, ThemeManager.Brush("OnAccent").Color);

    public void Select(int index, bool animate = true)
    {
        if (index >= _sectors.Count) index = -1;
        if (index == _selected) return;
        int prev = _selected;
        _selected = index;
        _pal = Palette();

        for (int i = 0; i < _sectors.Count; i++) _sectors[i].Target = i == index ? 1 : 0;
        if (index >= 0)
        {
            double target = RadialGeometry.SectorCenter(index, _sectors.Count);
            double delta = ((target - _hlAngle) % 360 + 540) % 360 - 180;
            _hlAngleTarget = _hlAngle + delta;
            // Появление или прыжок через центр — без вращения: подсветка мягко проявляется на новом месте
            if (prev < 0 || Math.Abs(delta) > 100)
            {
                _hlAngle = _hlAngleTarget;
                _hlOpacity = Math.Min(_hlOpacity, prev < 0 ? 0.2 : 0.35);
            }
            _hlOpacityTarget = 1;
            var p = _group!.Phrases[index];
            _preview.Text = p.Text.Replace("\r", "").TrimEnd();
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.Fg");
            _hubRing.SetResourceReference(Shape.StrokeProperty, "B.Accent");
        }
        else
        {
            _hlOpacityTarget = 0;
            _preview.Text = Loc.T(_sectors.Count == 0 ? "R.Empty" : "R.Hover");
            _preview.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");
            _hubRing.SetResourceReference(Shape.StrokeProperty, "B.SectorLine");
        }

        if (!animate)
        {
            _hlAngle = _hlAngleTarget;
            _hlOpacity = _hlOpacityTarget;
            foreach (var s in _sectors) s.Level = s.Target;
            ApplySelection(force: true);
            return;
        }
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += Tick;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= Tick;
    }

    /// <summary>Один кадр: всё экспоненциально приближается к цели (постоянная времени ~40–60 мс).</summary>
    private void Tick(object? sender, EventArgs e)
    {
        var now = e is RenderingEventArgs r ? r.RenderingTime : TimeSpan.Zero;
        if (now == _lastTick && now != TimeSpan.Zero) return; // событие бывает дважды за кадр
        double dt = _lastTick == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.05);
        _lastTick = now;

        bool busy = false;
        busy |= Approach(ref _hlAngle, _hlAngleTarget, dt, 0.042, 0.05);
        busy |= Approach(ref _hlOpacity, _hlOpacityTarget, dt, _hlOpacityTarget > _hlOpacity ? 0.04 : 0.06, 0.004);
        foreach (var s in _sectors) busy |= Approach(ref s.Level, s.Target, dt, 0.05, 0.004);
        ApplySelection(force: false);
        if (!busy) StopTicking();
    }

    private static bool Approach(ref double cur, double target, double dt, double tau, double eps)
    {
        if (cur == target) return false;
        cur += (target - cur) * (1 - Math.Exp(-dt / tau));
        if (Math.Abs(target - cur) < eps) cur = target;
        return cur != target;
    }

    private void ApplySelection(bool force)
    {
        if (force || _hlRotate.Angle != _hlAngle) _hlRotate.Angle = _hlAngle;
        if (force || _hl.Opacity != _hlOpacity) _hl.Opacity = _hlOpacity;
        foreach (var s in _sectors)
        {
            var label = Mix(_pal.fg, _pal.onAccent, s.Level);
            if (force || s.LabelBrush.Color != label) s.LabelBrush.Color = label;
            var num = Mix(_pal.muted, _pal.onAccent, s.Level);
            if (force || s.NumberBrush.Color != num) s.NumberBrush.Color = num;
        }
    }

    private static Color Mix(Color a, Color b, double t) => t <= 0 ? a : t >= 1 ? b : ThemeManager.Mix(a, b, t);

    private void Recolor()
    {
        var c = _pal = Palette();
        ColorHighlight(c);
        foreach (var s in _sectors) s.Fill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(c.sector, Motion.Slow));
        ApplySelection(force: true);
    }

    /// <summary>Появление: диск «раскрывается», сектора разлетаются веером.</summary>
    public void PlayOpen()
    {
        // Кеш диска — в реальном разрешении (DPI экрана × масштаб интерфейса), иначе край и тень «пиксельные»
        _discCache.RenderAtScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        this.Animate(OpacityProperty, 1, Motion.Normal, Motion.EaseOut, 0);
        var scale = RenderTransform as ScaleTransform ?? (ScaleTransform)(RenderTransform = new ScaleTransform(1, 1));
        scale.Animate(ScaleTransform.ScaleXProperty, 1, Motion.Normal, Motion.Spring, 0.86);
        scale.Animate(ScaleTransform.ScaleYProperty, 1, Motion.Normal, Motion.Spring, 0.86);

        for (int i = 0; i < _sectors.Count; i++)
        {
            var s = _sectors[i];
            var delay = TimeSpan.FromMilliseconds(12 * i);
            s.Host.BeginAnimation(OpacityProperty, Motion.To(1, Motion.Normal, Motion.EaseOut, 0, delay));
            s.Labels.BeginAnimation(OpacityProperty, Motion.To(1, Motion.Normal, Motion.EaseOut, 0, delay));
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

        int count = Populate(group);
        UpdateCenter(group, page, pageCount, count);
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

    /// <summary>
    /// Индекс сектора по смещению от центра в DIP. Выбранный сектор держится, пока курсор не ушёл за его край
    /// на несколько градусов (или заметно вглубь центра) — на границе выбор не мигает.
    /// </summary>
    public int HitIndex(double dx, double dy, double deadZone = 34)
    {
        int n = _sectors.Count;
        if (_selected >= 0 && _selected < n && dx * dx + dy * dy >= (deadZone - 6) * (deadZone - 6))
        {
            if (n == 1) return _selected;
            double deg = Math.Atan2(dx, -dy) * 180 / Math.PI;
            double off = Math.Abs(((deg - RadialGeometry.SectorCenter(_selected, n)) % 360 + 540) % 360 - 180);
            if (off <= 180.0 / n + 4) return _selected;
        }
        return RadialGeometry.IndexFromVector(dx, dy, n, deadZone);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!InteractiveHover) return;
        var p = e.GetPosition(this);
        double dx = p.X - C.X, dy = p.Y - C.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        Select(dist > ROut + 4 ? -1 : HitIndex(dx, dy, RIn - 8));
    }
}
