using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Type9.Themes;

/// <summary>Масштаб интерфейса: ручной (проценты) или автоматический по размеру рабочей области экрана.</summary>
internal static class UiScale
{
    public static readonly int[] Presets = [0, 90, 100, 115, 130];

    public static double Current { get; private set; } = 1;
    public static event Action? Changed;

    /// <summary>Подбор масштаба по рабочей области основного монитора (в DIP — DPI Windows уже учтён).</summary>
    public static double Auto()
    {
        var wa = SystemParameters.WorkArea;
        double byHeight = wa.Height switch
        {
            < 740 => 0.85,
            < 880 => 0.92,
            < 1250 => 1.0,
            < 1500 => 1.15,
            _ => 1.3,
        };
        double byWidth = wa.Width switch
        {
            < 1150 => 0.85,
            < 1350 => 0.92,
            < 2200 => 1.0,
            < 2800 => 1.15,
            _ => 1.3,
        };
        return Math.Min(byHeight, byWidth);
    }

    public static double Resolve(int percent) => percent <= 0 ? Auto() : Math.Clamp(percent, 70, 200) / 100.0;

    public static void Apply(int percent)
    {
        double v = Resolve(percent);
        if (Math.Abs(v - Current) < 0.001) return;
        Current = v;
        Changed?.Invoke();
    }

    /// <summary>
    /// Масштаб через LayoutTransform сдвигает округление раскладки с сетки пикселей экрана: при 90/115/130%
    /// (и при масштабе Windows 125/150%) рамки в 1 пиксель «плывут», текст мылится. Сообщаем окну DPI с учётом
    /// нашего масштаба — WPF округляет размеры и растеризует текст и кеши сразу под реальные пиксели.
    /// Всё содержимое окна должно лежать внутри масштабирующего элемента.
    /// </summary>
    public static void PixelAlign(Window w)
    {
        void Apply()
        {
            if (PresentationSource.FromVisual(w)?.CompositionTarget is not { } ct) return;
            var m = ct.TransformToDevice;
            var want = new DpiScale(m.M11 * Current, m.M22 * Current);
            var cur = VisualTreeHelper.GetDpi(w);
            if (Math.Abs(cur.DpiScaleX - want.DpiScaleX) < 0.001 && Math.Abs(cur.DpiScaleY - want.DpiScaleY) < 0.001) return;
            try { VisualTreeHelper.SetRootDpi(w, want); }
            catch (Exception ex) { Core.Log.Error("SetRootDpi", ex); }
        }
        Action onScale = Apply;
        w.SourceInitialized += (_, _) => Apply();
        // Переезд на монитор с другим DPI: WPF выставит системный DPI — поправляем после него
        w.DpiChanged += (_, _) => w.Dispatcher.BeginInvoke(Apply, System.Windows.Threading.DispatcherPriority.Loaded);
        Changed += onScale;
        w.Closed += (_, _) => Changed -= onScale;
    }

    /// <summary>Реальное число физических пикселей на DIP для окна (без поправки PixelAlign).</summary>
    public static double DeviceScale(Visual v) =>
        PresentationSource.FromVisual(v)?.CompositionTarget?.TransformToDevice.M11 ?? VisualTreeHelper.GetDpi(v).DpiScaleX;

    /// <summary>Плавно анимирует масштабирующую трансформацию к текущему значению.</summary>
    public static void AnimateTo(ScaleTransform t, bool animate = true)
    {
        if (!animate)
        {
            t.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            t.ScaleX = t.ScaleY = Current;
            return;
        }
        t.Animate(ScaleTransform.ScaleXProperty, Current, Motion.Slow, Motion.EaseOutStrong);
        t.Animate(ScaleTransform.ScaleYProperty, Current, Motion.Slow, Motion.EaseOutStrong);
    }
}

/// <summary>Вложенное свойство: элемент плавно «въезжает» при появлении (элементы списков, карточки).</summary>
public static class Anim
{
    public static readonly DependencyProperty AppearProperty = DependencyProperty.RegisterAttached(
        "Appear", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnAppearChanged));

    public static bool GetAppear(DependencyObject d) => (bool)d.GetValue(AppearProperty);
    public static void SetAppear(DependencyObject d, bool v) => d.SetValue(AppearProperty, v);

    private static void OnAppearChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && e.NewValue is true) fe.Loaded += (_, _) => Play(fe);
    }

    public static void Play(FrameworkElement fe, double offset = 8)
    {
        var shift = new TranslateTransform(0, offset);
        fe.RenderTransform = shift;
        fe.Animate(UIElement.OpacityProperty, 1, Motion.Slow, Motion.EaseOut, 0);
        shift.Animate(TranslateTransform.YProperty, 0, Motion.Slow, Motion.EaseOutStrong);
    }

    /// <summary>Плавно убирает элемент (сжатие + исчезновение), затем вызывает удаление из коллекции.</summary>
    public static void Remove(FrameworkElement? fe, Action remove)
    {
        if (fe is null) { remove(); return; }
        var container = FindContainer(fe);
        container.IsHitTestVisible = false;
        var st = new ScaleTransform(1, 1);
        container.RenderTransformOrigin = new Point(0.5, 0.5);
        container.RenderTransform = st;
        st.Animate(ScaleTransform.ScaleYProperty, 0.85, Motion.Normal, Motion.EaseIn);
        container.Animate(UIElement.OpacityProperty, 0, Motion.Normal, Motion.EaseIn, completed: (_, _) => remove());
    }

    private static FrameworkElement FindContainer(FrameworkElement fe)
    {
        // Ищем корень шаблона элемента списка (ContentPresenter внутри ItemsControl)
        DependencyObject? cur = fe;
        FrameworkElement best = fe;
        while (cur is not null)
        {
            if (cur is FrameworkElement f && GetAppear(f)) return f;
            if (cur is System.Windows.Controls.ContentPresenter cp && cp.TemplatedParent is null) best = cp;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return best;
    }

    /// <summary>Короткое «мерцание» обновлённого текста/блока.</summary>
    public static void Pulse(UIElement el) => el.Animate(UIElement.OpacityProperty, 1, Motion.Slow, Motion.EaseOut, 0.25);
}
