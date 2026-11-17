using System.Windows;
using System.Windows.Media.Animation;

namespace Type9.Themes;

/// <summary>
/// Единые длительности и кривые анимаций. Все анимации — по Opacity/трансформациям (GPU, без relayout).
/// Длительности подобраны под 60 Гц: 7–15 кадров — заметно плавно, но не «вязко».
/// </summary>
internal static class Motion
{
    public static readonly Duration Fast = TimeSpan.FromMilliseconds(110);
    public static readonly Duration Normal = TimeSpan.FromMilliseconds(170);
    public static readonly Duration Slow = TimeSpan.FromMilliseconds(250);

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutStrong = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static IEasingFunction Freeze(EasingFunctionBase e)
    {
        e.Freeze();
        return e;
    }

    public static DoubleAnimation To(double to, Duration d, IEasingFunction? ease = null, double? from = null, TimeSpan? delay = null)
    {
        var a = new DoubleAnimation { To = to, Duration = d, EasingFunction = ease ?? EaseOut };
        if (from is { } f) a.From = f;
        if (delay is { } dl) a.BeginTime = dl;
        return a;
    }

    public static void Animate(this Animatable target, DependencyProperty dp, double to, Duration d,
        IEasingFunction? ease = null, double? from = null, EventHandler? completed = null)
    {
        var a = To(to, d, ease, from);
        if (completed is not null) a.Completed += completed;
        target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }

    public static void Animate(this UIElement target, DependencyProperty dp, double to, Duration d,
        IEasingFunction? ease = null, double? from = null, EventHandler? completed = null, TimeSpan? delay = null)
    {
        var a = To(to, d, ease, from, delay);
        if (completed is not null) a.Completed += completed;
        target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
    }
}
