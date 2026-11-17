using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Type9.Core;

namespace Type9.UI;

/// <summary>
/// Логотип: тёмное круговое меню, в котором один сектор «выбран» — подсвечен бордовым и выдвинут наружу.
/// Рисуется векторно (холст 256×256); из него же генерируются .ico и иконка в трее.
/// </summary>
public static class LogoRenderer
{
    private static readonly Point C = new(128, 128);

    private static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    /// <param name="selected">Индекс выбранного сектора (0 — верхний, по часовой).</param>
    /// <param name="paused">Серый вариант для режима паузы.</param>
    /// <param name="simple">Упрощённый вариант для 16–24 px.</param>
    public static DrawingGroup Create(int selected = 1, bool paused = false, bool simple = false, Color? accent = null)
    {
        var acc = paused ? Hex("#6B6F78") : accent ?? Hex(AppSettings.DefaultAccent);
        var accLight = Themes.ThemeManager.Mix(acc, Colors.White, paused ? 0.15 : 0.28);
        var accDark = Themes.ThemeManager.Mix(acc, Colors.Black, 0.25);

        int n = simple ? 4 : 6;
        double rOut = simple ? 112 : 102, rIn = simple ? 42 : 46, gap = simple ? 14 : 8, push = simple ? 6 : 9;
        selected = ((selected % n) + n) % n;

        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            // Диск
            var disc = new RadialGradientBrush(Hex("#2A2D35"), Hex("#121318")) { Center = new Point(0.45, 0.35), GradientOrigin = new Point(0.45, 0.3), RadiusX = 0.7, RadiusY = 0.7 };
            dc.DrawEllipse(disc, new Pen(new SolidColorBrush(Hex("#3A3E48")), simple ? 0 : 3), C, simple ? 126 : 122, simple ? 126 : 122);

            // Обычные сектора
            var secBrush = new LinearGradientBrush(Hex("#383C46"), Hex("#2A2D35"), 90);
            var secPen = new Pen(new SolidColorBrush(Hex("#454A55")), simple ? 0 : 1.2);
            for (int i = 0; i < n; i++)
            {
                if (i == selected) continue;
                dc.DrawGeometry(secBrush, secPen, RadialGeometry.Sector(C, rIn, rOut, i, n, gap));
            }

            // Выбранный сектор: выдвинут наружу, со свечением
            double mid = RadialGeometry.SectorCenter(selected, n);
            var shift = RadialGeometry.At(new Point(0, 0), push, mid);
            dc.PushTransform(new TranslateTransform(shift.X, shift.Y));
            var sel = RadialGeometry.Sector(C, rIn, rOut, selected, n, gap);
            if (!simple)
            {
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, acc.R, acc.G, acc.B)), 14) { LineJoin = PenLineJoin.Round }, sel);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x55, acc.R, acc.G, acc.B)), 7) { LineJoin = PenLineJoin.Round }, sel);
            }
            var p0 = RadialGeometry.At(C, rOut, mid);
            var p1 = RadialGeometry.At(C, rIn, mid);
            var selBrush = new LinearGradientBrush(accLight, accDark, p0, p1) { MappingMode = BrushMappingMode.Absolute };
            dc.DrawGeometry(selBrush, new Pen(new SolidColorBrush(accLight), simple ? 0 : 1.5), sel);
            dc.Pop();

            // Центральный «хаб» с указателем на выбранный сектор
            double hub = simple ? 30 : 34;
            dc.DrawEllipse(new SolidColorBrush(Hex("#17181D")), new Pen(new SolidColorBrush(Hex("#3A3E48")), simple ? 0 : 2.5), C, hub, hub);
            if (!simple)
            {
                var tip = RadialGeometry.At(C, hub - 9, mid);
                var l = RadialGeometry.At(C, hub - 22, mid - 28);
                var r = RadialGeometry.At(C, hub - 22, mid + 28);
                var tri = new StreamGeometry();
                using (var sg = tri.Open())
                {
                    sg.BeginFigure(tip, true, true);
                    sg.LineTo(r, true, false);
                    sg.LineTo(l, true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(accLight), null, tri);
                dc.DrawEllipse(new SolidColorBrush(Hex("#2C2F37")), null, C, 7, 7);
            }
            else
            {
                dc.DrawEllipse(new SolidColorBrush(accLight), null, RadialGeometry.At(C, 12, mid), 8, 8);
            }
        }
        g.Freeze();
        return g;
    }

    public static ImageSource CreateImage(int selected = 1, bool paused = false, Color? accent = null)
    {
        var img = new DrawingImage(Create(selected, paused, false, accent));
        img.Freeze();
        return img;
    }

    public static BitmapSource Render(int px, bool paused = false, Color? accent = null)
    {
        var drawing = Create(1, paused, px <= 24, accent);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(px / 256.0, px / 256.0));
            dc.DrawDrawing(drawing);
        }
        var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>ICO с PNG-кадрами (формат Vista+).</summary>
    public static byte[] CreateIco(int[] sizes, bool paused = false, Color? accent = null)
    {
        var frames = sizes.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(s, paused, accent)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Size: s, Data: ms.ToArray());
        }).ToList();

        using var o = new MemoryStream();
        using var w = new BinaryWriter(o);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var f in frames)
        {
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)(f.Size >= 256 ? 0 : f.Size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(f.Data.Length);
            w.Write(offset);
            offset += f.Data.Length;
        }
        foreach (var f in frames) w.Write(f.Data);
        w.Flush();
        return o.ToArray();
    }

    public static void ExportIco(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, CreateIco([16, 20, 24, 32, 40, 48, 64, 128, 256]));
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(512)));
        using var fs = File.Create(Path.ChangeExtension(path, ".png"));
        enc.Save(fs);
    }

    public static System.Drawing.Icon CreateTrayIcon(bool paused, Color? accent = null)
    {
        using var ms = new MemoryStream(CreateIco([16, 20, 24, 32, 40, 48, 64], paused, accent));
        return new System.Drawing.Icon(ms, System.Windows.Forms.SystemInformation.SmallIconSize);
    }
}
