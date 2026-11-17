using System.Windows.Threading;

namespace Type9.Core;

/// <summary>
/// Сторож интерфейса: раз в 2 с ставит пустую операцию в очередь UI-потока. Если она не выполнилась за 6 с —
/// пишет в лог, что интерфейс завис и где (раздел настроек и т.п.); после восстановления — сколько длилось.
/// </summary>
internal static class UiWatchdog
{
    /// <summary>Что сейчас на экране — попадает в лог при зависании.</summary>
    public static volatile string Context = "";

    public static void Start(Dispatcher ui)
    {
        var t = new Thread(() => Loop(ui)) { IsBackground = true, Name = "Type9.UiWatchdog", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private static void Loop(Dispatcher ui)
    {
        while (!ui.HasShutdownStarted)
        {
            long sent = Environment.TickCount64;
            var op = ui.BeginInvoke(DispatcherPriority.Normal, () => { });
            bool reported = false;
            while (op.Status != DispatcherOperationStatus.Completed && op.Status != DispatcherOperationStatus.Aborted)
            {
                Thread.Sleep(250);
                if (ui.HasShutdownStarted) return;
                long waited = Environment.TickCount64 - sent;
                if (!reported && waited > 6000)
                {
                    reported = true;
                    Log.Error($"Интерфейс не отвечает больше 6 с (экран: {Context})");
                }
            }
            if (reported) Log.Info($"Интерфейс снова отвечает через {(Environment.TickCount64 - sent) / 1000.0:0.0} с");
            Thread.Sleep(2000);
        }
    }
}
