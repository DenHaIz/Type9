using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Type9.Core;
using Type9.Input;
using Type9.Prediction;
using Type9.Themes;
using Type9.UI.Controls;

namespace Type9.UI;

/// <summary>
/// Служебный режим для проверки вёрстки (ключ --snapshot &lt;папка&gt;): рендерит окна во всех темах,
/// языках и масштабах в PNG без вывода на экран.
/// </summary>
internal static class Snapshot
{
    public static async Task RunAsync(AppController app, string dir)
    {
        Directory.CreateDirectory(dir);
        var s = app.Settings;
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.English })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        foreach (var scale in new[] { 100, 130 })
        {
            if (scale != 100 && theme == AppTheme.Light) continue;
            s.Language = lang;
            s.Theme = theme;
            s.UiScale = scale;
            Loc.Apply(lang);
            ThemeManager.Apply(theme, s.Accent, animate: false);
            UiScale.Apply(scale);
            string tag = $"{(lang == AppLanguage.Russian ? "ru" : "en")}-{theme.ToString().ToLowerInvariant()}-{scale}";

            var w = new SettingsWindow(app) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            w.Show();
            foreach (var section in new[] { "general", "phrases", "abbrev", "dict", "excl", "about" })
            {
                w.Navigate(section);
                await Settle();
                Save(w, Path.Combine(dir, $"settings-{section}-{tag}.png"));
            }
            w.Close();

            // Круговое меню и подсказка
            var view = new RadialView { Margin = new Thickness(20) };
            var host = new System.Windows.Controls.Grid { Background = ThemeManager.Brush("Bg"), Children = { view } };
            var hw = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };
            hw.Show();
            view.Build(app.Phrases.Groups[0], 0, app.Phrases.Groups.Count);
            view.Select(2, animate: false);
            await Settle();
            Save(host, Path.Combine(dir, $"radial-{tag}.png"));
            hw.Close();

            var popup = new SuggestionPopup { Left = -20000, Top = -20000 };
            popup.SetItems(
            [
                new Suggestion(SuggestionKind.Completion, "Здр", "авствуйте", "", 0),
                new Suggestion(SuggestionKind.Completion, "Здр", "авствуй", "", 0),
                new Suggestion(SuggestionKind.Abbreviation, "здр → ", "Здравствуйте,", "", 3),
            ], 0);
            popup.ShowAt(new CaretInfo(new Native.Win32.RECT { Left = -20000, Top = -20000, Right = -19999, Bottom = -19980 }, true, false), false);
            await Settle();
            Save(popup, Path.Combine(dir, $"popup-{tag}.png"), ThemeManager.Brush("Bg").Color);
            popup.Close();
        }
    }

    private static async Task Settle()
    {
        // ждём завершения анимаций появления
        await Task.Delay(450);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(FrameworkElement el, string path, Color? background = null)
    {
        double dpi = 96 * VisualTreeHelper.GetDpi(el).DpiScaleX;
        int w = (int)Math.Ceiling(el.ActualWidth * dpi / 96), h = (int)Math.Ceiling(el.ActualHeight * dpi / 96);
        if (w <= 0 || h <= 0) return;
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        if (background is { } bg)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            rtb.Render(dv);
        }
        rtb.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }


}
