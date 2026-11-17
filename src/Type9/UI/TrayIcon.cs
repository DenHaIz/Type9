using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Type9.Core;
using Type9.Native;
using Type9.Themes;
using Forms = System.Windows.Forms;

namespace Type9.UI;

/// <summary>Иконка в области уведомлений с меню в стиле текущей темы.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppController _app;
    private readonly Forms.NotifyIcon _icon;
    private System.Drawing.Icon? _normal, _paused;
    private string _accentForIcons = "";

    public TrayIcon(AppController app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon { Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _app.ShowSettings();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        Update();
    }

    public void Update()
    {
        if (_accentForIcons != _app.Settings.Accent)
        {
            _normal?.Dispose();
            _paused?.Dispose();
            var accent = ThemeManager.ParseColor(_app.Settings.Accent);
            _normal = LogoRenderer.CreateTrayIcon(false, accent);
            _paused = LogoRenderer.CreateTrayIcon(true, accent);
            _accentForIcons = _app.Settings.Accent;
        }
        _icon.Icon = _app.Paused ? _paused : _normal;
        _icon.Text = _app.Paused ? Loc.T("T.TipPaused") : Loc.F("T.Tip", _app.Settings.RadialHotkey);
    }

    private static MenuItem Item(string header, string glyph, Action click, string gesture = "")
    {
        var icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("T.Icon"), FontSize = 13 };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "B.FgMuted");
        var mi = new MenuItem { Header = header, Icon = icon, InputGestureText = gesture };
        mi.Click += (_, _) => click();
        return mi;
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(Item(Loc.T("T.Settings"), "", () => _app.ShowSettings()));
        menu.Items.Add(Item(Loc.T("T.Phrases"), "", () => _app.ShowSettings("phrases")));
        menu.Items.Add(new Separator());
        menu.Items.Add(_app.Paused
            ? Item(Loc.T("Btn.Resume"), "", () => _app.SetPaused(false), _app.Settings.PauseHotkey)
            : Item(Loc.T("Btn.Pause"), "", () => _app.SetPaused(true), _app.Settings.PauseHotkey));
        menu.Items.Add(Item(Loc.T(ThemeManager.IsDark ? "T.Light" : "T.Dark"), ThemeManager.IsDark ? "" : "", () =>
        {
            _app.Settings.Theme = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
            _app.SettingsChanged();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("T.Exit"), "", _app.Exit));

        menu.Opened += (_, _) =>
        {
            // Без активации меню не закрывалось бы по клику мимо
            if (PresentationSource.FromVisual(menu) is HwndSource src) Win32.SetForegroundWindow(src.Handle);
            menu.Opacity = 0;
            menu.Animate(UIElement.OpacityProperty, 1, Motion.Normal, Motion.EaseOut);
        };
        menu.IsOpen = true;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normal?.Dispose();
        _paused?.Dispose();
    }
}
