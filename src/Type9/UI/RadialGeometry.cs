using System.Windows;
using System.Windows.Media;

namespace Type9.UI;

/// <summary>Геометрия кругового меню. Углы в градусах: 0° — вверх, по часовой стрелке. Сектор i центрирован на i·360/n.</summary>
public static class RadialGeometry
{
    public static Point At(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    public static double SectorCenter(int index, int count) => index * 360.0 / count;

    /// <summary>Кольцевой сектор с зазором <paramref name="gapPx"/> (постоянной ширины по всей длине).</summary>
    public static Geometry Sector(Point c, double rIn, double rOut, int index, int count, double gapPx)
    {
        double span = 360.0 / count;
        double mid = SectorCenter(index, count);
        if (count == 1)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(c, rOut, rOut));
            ring.Children.Add(new EllipseGeometry(c, rIn, rIn));
            ring.Freeze();
            return ring;
        }

        // Угловой отступ на каждом радиусе свой — тогда щель между секторами ровная
        double padOut = Math.Asin(Math.Min(1, gapPx / 2 / rOut)) * 180 / Math.PI;
        double padIn = Math.Asin(Math.Min(1, gapPx / 2 / rIn)) * 180 / Math.PI;
        double a0o = mid - span / 2 + padOut, a1o = mid + span / 2 - padOut;
        double a0i = mid - span / 2 + padIn, a1i = mid + span / 2 - padIn;

        var fig = new PathFigure { StartPoint = At(c, rIn, a0i), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment(At(c, rOut, a0o), true));
        fig.Segments.Add(new ArcSegment(At(c, rOut, a1o), new Size(rOut, rOut), 0, a1o - a0o > 180, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(At(c, rIn, a1i), true));
        fig.Segments.Add(new ArcSegment(At(c, rIn, a0i), new Size(rIn, rIn), 0, a1i - a0i > 180, SweepDirection.Counterclockwise, true));
        var g = new PathGeometry([fig]);
        g.Freeze();
        return g;
    }

    /// <summary>Индекс сектора по вектору от центра (dx вправо, dy вниз). -1 — внутри мёртвой зоны.</summary>
    public static int IndexFromVector(double dx, double dy, int count, double deadZone)
    {
        if (count <= 0 || dx * dx + dy * dy < deadZone * deadZone) return -1;
        double deg = Math.Atan2(dx, -dy) * 180 / Math.PI; // 0 = вверх, по часовой
        if (deg < 0) deg += 360;
        double span = 360.0 / count;
        return (int)Math.Floor((deg + span / 2) / span) % count;
    }
}
