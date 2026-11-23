using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SpiritNest.Services;

/// <summary>
/// Logon start is one current-user scheduled task with no delay.
/// The dev build and the installed build each have their own task.
/// Turning autostart off deletes only this build's task. A missed start is not replayed.
/// </summary>
public static class AutostartService
{
    public const string SetSwitch = "--set-autostart";
    public const string ClearSwitch = "--clear-autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "SpiritPerch";
    private const string LegacyValueName = "SpiritNest";
#if DEV_BUILD
    private const string TaskName = @"SpiritPerchDev\Autostart";
    private const string TaskDescription = "开机时启动灵栖（开发版）";
#else
    private const string TaskName = @"SpiritPerch\Autostart";
    private const string TaskDescription = "开机时启动灵栖";
#endif
    private const string LegacyTaskName = @"SpiritNest\Autostart";

    public static bool TryHandle(string[] args)
    {
        var set = args.Any(a => string.Equals(a, SetSwitch, StringComparison.OrdinalIgnoreCase));
        var clear = args.Any(a => string.Equals(a, ClearSwitch, StringComparison.OrdinalIgnoreCase));
        if (!set && !clear)
            return false;
        if (!Apply(set && !clear))
            Environment.ExitCode = 1;
        return true;
    }

    /// <summary>Returns false when enabling did not leave a logon task in place.</summary>
    public static bool Apply(bool enable)
    {
        RunSchtasks($"/Delete /TN \"{LegacyTaskName}\" /F");
        if (!enable)
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
#if !DEV_BUILD
            DeleteRunValues();
#endif
            return true;
        }

        if (!CreateLogonTask(ResolveExePath()))
            return false;
#if !DEV_BUILD
        DeleteRunValues();
#endif
        return true;
    }

    public static bool IsEnabled() => QueryTask(TaskName);

    private static string ResolveExePath()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            return exe;
        return Path.Combine(AppContext.BaseDirectory, "SpiritPerch.exe");
    }

    private static bool CreateLogonTask(string exe)
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        var xmlPath = Path.Combine(Path.GetTempPath(), "SpiritPerch-autostart.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
            return RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"");
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    private static string BuildTaskXml(string exe)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{Xml(TaskDescription)}</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{Xml(user)}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{Xml(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>LeastPrivilege</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Xml(exe)}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static string Xml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static bool QueryTask(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{name}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            _ = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return false;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return false;
            }
            _ = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteRunValues()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            approved?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }
}
