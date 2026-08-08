using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace CaYaTunnel.Ui;

/// <summary>
/// Runs an application at sign-in, at either privilege level.
/// <para>
/// Two mechanisms, because Windows offers no single one that covers both. The Run key is simple
/// and needs no privileges, but anything it launches runs unelevated. Starting elevated without
/// a UAC prompt every time requires a scheduled task with the highest-available run level, and
/// registering that task itself needs administrator rights once.
/// </para>
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CaYaTunnel.exe");

    // ---- Current state -------------------------------------------------------

    public static StartupState GetState(string appName, string taskName)
    {
        if (TaskExists(taskName))
        {
            return StartupState.ElevatedTask;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(appName) is not null ? StartupState.RunKey : StartupState.Disabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return StartupState.Disabled;
        }
    }

    // ---- Standard (unelevated) ------------------------------------------------

    public static bool SetRunKey(string appName, bool enabled, bool startHidden)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var command = startHidden
                    ? $"\"{CurrentExecutablePath}\" --hidden"
                    : $"\"{CurrentExecutablePath}\"";
                key.SetValue(appName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // ---- Elevated (scheduled task) --------------------------------------------

    /// <summary>
    /// Registers a logon task that starts the app with the highest privileges available.
    /// Requires the current process to be elevated; returns false with a reason otherwise so the
    /// UI can offer to relaunch as administrator rather than silently doing nothing.
    /// </summary>
    public static (bool Ok, string Message) SetElevatedTask(string taskName, bool enabled, bool startHidden)
    {
        if (!IsElevated)
        {
            return (false, "Setting up an elevated startup entry needs administrator rights. Restart the app as administrator and try again.");
        }

        try
        {
            if (!enabled)
            {
                Run("schtasks", $"/Delete /TN \"{taskName}\" /F");
                return (true, "Startup entry removed.");
            }

            var xmlPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():n}.xml");
            try
            {
                // Unicode with a BOM: schtasks rejects task XML in any other encoding.
                File.WriteAllText(xmlPath, BuildTaskXml(taskName, startHidden), new UnicodeEncoding(false, true));

                var (exitCode, output) = Run("schtasks", $"/Create /TN \"{taskName}\" /XML \"{xmlPath}\" /F");
                return exitCode == 0
                    ? (true, "This app will now start with Windows, with administrator rights.")
                    : (false, $"Windows Task Scheduler refused the entry: {output}");
            }
            finally
            {
                try
                {
                    File.Delete(xmlPath);
                }
                catch (IOException)
                {
                    // Temp file; not worth reporting.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return (false, ex.Message);
        }
    }

    public static bool TaskExists(string taskName)
    {
        try
        {
            var (exitCode, _) = Run("schtasks", $"/Query /TN \"{taskName}\"");
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    /// <summary>Restarts this application with a UAC prompt, so the user can grant rights once.</summary>
    public static bool RelaunchElevated(string? arguments = null)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = CurrentExecutablePath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the user declined the prompt
        }
    }

    private static string BuildTaskXml(string taskName, bool startHidden)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var arguments = startHidden ? "<Arguments>--hidden</Arguments>" : string.Empty;

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts {taskName} at sign-in with administrator rights.</Description>
            <URI>\{taskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <!-- Zero means "no time limit": a tunnel agent is meant to stay running. -->
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{System.Security.SecurityElement.Escape(CurrentExecutablePath)}</Command>
              {arguments}
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(20000);

        return (process.ExitCode, output.Trim());
    }
}

public enum StartupState
{
    Disabled,

    /// <summary>Runs at sign-in without elevation.</summary>
    RunKey,

    /// <summary>Runs at sign-in with administrator rights, via Task Scheduler.</summary>
    ElevatedTask,
}
