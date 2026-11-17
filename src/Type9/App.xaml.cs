using System.IO;
using System.Windows;
using System.Windows.Threading;
using Type9.Core;
using Type9.Themes;
using Type9.UI;

namespace Type9;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Необработанное исключение UI", ex.Exception);
            ex.Handled = true; // программа не должна падать и «ронять» ввод
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Необработанное исключение", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task", ex.Exception); ex.SetObserved(); };

        // Служебный режим: сгенерировать иконку (используется при сборке)
        int iconArg = Array.IndexOf(args, "--export-icon");
        if (iconArg >= 0)
        {
            ThemeManager.Init(Resources, AppTheme.Dark, AppSettings.DefaultAccent);
            Loc.Apply(AppLanguage.Russian, Resources);
            LogoRenderer.ExportIco(args.Length > iconArg + 1 ? args[iconArg + 1] : "type9.ico");
            Shutdown();
            return;
        }

        // Служебный режим: отрисовать окна в PNG для проверки вёрстки
        int snapArg = Array.IndexOf(args, "--snapshot");
        if (snapArg >= 0)
        {
            var dir = Path.GetFullPath(args.Length > snapArg + 1 ? args[snapArg + 1] : "snapshots");
            Environment.SetEnvironmentVariable("TYPE9_DATA_DIR", Path.Combine(dir, "data"));
            var st = new AppSettings();
            ThemeManager.Init(Resources, st.Theme, st.Accent);
            Loc.Apply(st.Language, Resources);
            var ctl = new AppController(Dispatcher, st, PhraseBook.CreateDefault(AppLanguage.Russian));
            Dispatcher.BeginInvoke(async () =>
            {
                try { await Snapshot.RunAsync(ctl, dir); }
                catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); }
                Shutdown();
            });
            return;
        }

        _instance = new SingleInstance();
        bool restarted = args.Contains("--restart");
        if (!_instance.TryAcquire(restarted ? TimeSpan.FromSeconds(8) : TimeSpan.Zero))
        {
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        var settings = Storage.Load(Paths.Settings, JsonCtx.Default.AppSettings, () => new AppSettings(), out bool settingsExisted);
        var phrases = Storage.Load(Paths.Phrases, JsonCtx.Default.PhraseBook, () => PhraseBook.CreateDefault(settings.Language), out bool phrasesExisted);
        if (!phrasesExisted) Storage.Save(Paths.Phrases, phrases, JsonCtx.Default.PhraseBook);

        // Режим администратора: при обычном запуске переподнимаемся через UAC
        if (settings.Mode == RunMode.Elevated && !Elevation.IsElevated && !args.Contains("--no-elevate"))
        {
            _instance.Dispose();
            _instance = null;
            if (Elevation.RelaunchElevated("--restart"))
            {
                Shutdown();
                return;
            }
            _instance = new SingleInstance();
            _instance.TryAcquire(TimeSpan.FromSeconds(2));
            Log.Info("Пользователь отказался от повышения прав — работаем в обычном режиме");
        }

        ThemeManager.Init(Resources, settings.Theme, settings.Accent);
        Loc.Apply(settings.Language, Resources);
        UiScale.Apply(settings.UiScale);

        _controller = new AppController(Dispatcher, settings, phrases);
        _controller.Start();
        _instance.Listen(() => Dispatcher.BeginInvoke(() => _controller?.ShowSettings()));

        if (settings.Autostart) _controller.ApplyAutostart();

        if (!settingsExisted)
        {
            Directory.CreateDirectory(Paths.DataDir);
            _controller.SaveSettingsNow();
            _controller.ShowSettings("about"); // первый запуск — показать, что программа делает
        }
        else if (!args.Contains("--autostart") && !restarted)
        {
            _controller.ShowSettings();
        }
        else if (restarted)
        {
            _controller.ShowSettings("general");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
