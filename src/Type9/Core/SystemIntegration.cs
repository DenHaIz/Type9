using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Type9.Core;

internal static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Перезапуск с правами администратора (UAC). false — пользователь отказался.</summary>
    public static bool RelaunchElevated(string args = "--restart")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Paths.ExePath, args) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // отмена в окне UAC
        }
    }

    /// <summary>Перезапуск без повышенных прав (через explorer, который работает от обычного пользователя).</summary>
    public static void RelaunchNormal()
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Paths.ExePath}\"") { UseShellExecute = false });
    }
}

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Type9";

    /// <summary>Приводит автозапуск в соответствие с настройками. Возвращает текст ошибки или null.</summary>
    public static string? Apply(bool enabled, RunMode mode)
    {
        try
        {
            if (!enabled)
            {
                SetRunKey(false);
                if (TaskExists()) DeleteTask();
                return null;
            }

            if (mode == RunMode.Normal)
            {
                SetRunKey(true);
                if (TaskExists() && Elevation.IsElevated) DeleteTask();
                return null;
            }

            // Режим администратора: задача Планировщика с наивысшими правами (без запроса UAC при входе)
            if (!Elevation.IsElevated)
                return Loc.T("Autostart.NeedAdmin");
            CreateTask();
            SetRunKey(false);
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка настройки автозапуска", ex);
            return ex.Message;
        }
    }

    private static void SetRunKey(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (on) key.SetValue(Name, $"\"{Paths.ExePath}\" --autostart");
        else if (key.GetValue(Name) is not null) key.DeleteValue(Name, false);
    }

    public static bool TaskExists() => RunSchtasks($"/Query /TN \"{Name}\"") == 0;

    private static void DeleteTask() => RunSchtasks($"/Delete /TN \"{Name}\" /F");

    private static void CreateTask()
    {
        var user = WindowsIdentity.GetCurrent().Name;
        string Esc(string s) => System.Security.SecurityElement.Escape(s)!;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Type9 — автозапуск с правами администратора</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId><Delay>PT3S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Esc(Paths.ExePath)}</Command><Arguments>--autostart</Arguments></Exec></Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), "type9-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/Create /TN \"{Name}\" /XML \"{tmp}\" /F") != 0)
                throw new InvalidOperationException("schtasks не смог создать задачу автозапуска");
        }
        finally { File.Delete(tmp); }
    }

    private static int RunSchtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);
        return p.ExitCode;
    }
}

/// <summary>Один экземпляр на сессию; повторный запуск просит работающий экземпляр открыть настройки.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Type9.SingleInstance";
    private const string EventName = @"Local\Type9.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _wait;

    public bool TryAcquire(TimeSpan wait)
    {
        try
        {
            _mutex = new Mutex(false, MutexName);
            try
            {
                if (_mutex.WaitOne(wait)) return true;
            }
            catch (AbandonedMutexException) { return true; }
        }
        catch (UnauthorizedAccessException) { /* мьютекс создан экземпляром с другими правами */ }
        _mutex?.Dispose();
        _mutex = null;
        return false;
    }

    public static void SignalExisting()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var ev)) using (ev) ev.Set();
        }
        catch { /* ignore */ }
    }

    public void Listen(Action onSignal)
    {
        try
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => onSignal(), null, -1, false);
        }
        catch (Exception ex) { Log.Error("SingleInstance.Listen", ex); }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _event?.Dispose();
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
            _mutex.Dispose();
        }
    }
}
