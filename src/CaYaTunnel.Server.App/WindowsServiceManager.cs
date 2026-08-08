using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace CaYaTunnel.Ui;

/// <summary>
/// Installs the gateway as a Windows service so it keeps running with nobody signed in — the
/// normal state of a VPS. Uses sc.exe rather than a COM interop layer: it is present on every
/// Windows install and its failures are readable.
/// </summary>
public static class WindowsServiceManager
{
    public const string ServiceName = "CaYaTunnel";
    public const string DisplayName = "CaYaTunnel Gateway";

    /// <summary>Argument that makes the executable run headless under the service host.</summary>
    public const string ServiceSwitch = "--service";

    public static bool IsInstalled => Query() is not null;

    public static ServiceControllerStatus? Status => Query()?.Status;

    public static bool IsRunning => Status == ServiceControllerStatus.Running;

    public static (bool Ok, string Message) Install(string executablePath)
    {
        if (!StartupManager.IsElevated)
        {
            return (false, "Installing a Windows service needs administrator rights. Restart this app as administrator.");
        }

        if (!File.Exists(executablePath))
        {
            return (false, $"Cannot find '{executablePath}'.");
        }

        // sc.exe is famously picky: the space after each "key=" is required.
        var (exitCode, output) = Run("sc.exe",
            $"create {ServiceName} binPath= \"\\\"{executablePath}\\\" {ServiceSwitch}\" DisplayName= \"{DisplayName}\" start= auto");

        if (exitCode != 0)
        {
            return (false, $"Windows refused to create the service: {output}");
        }

        Run("sc.exe", $"description {ServiceName} \"Keeps CaYaTunnel reverse tunnels available without an interactive session.\"");

        // Restart on failure rather than leaving every tunnel down until someone notices.
        Run("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        return (true, "Service installed. It will start automatically with Windows.");
    }

    public static (bool Ok, string Message) Uninstall()
    {
        if (!StartupManager.IsElevated)
        {
            return (false, "Removing a Windows service needs administrator rights.");
        }

        Stop();
        var (exitCode, output) = Run("sc.exe", $"delete {ServiceName}");

        return exitCode == 0
            ? (true, "Service removed.")
            : (false, $"Windows refused to remove the service: {output}");
    }

    public static (bool Ok, string Message) Start()
    {
        var (exitCode, output) = Run("sc.exe", $"start {ServiceName}");
        return exitCode == 0 ? (true, "Service started.") : (false, output);
    }

    public static (bool Ok, string Message) Stop()
    {
        var (exitCode, output) = Run("sc.exe", $"stop {ServiceName}");
        return exitCode == 0 ? (true, "Service stopped.") : (false, output);
    }

    private static ServiceController? Query()
    {
        try
        {
            var controller = ServiceController.GetServices()
                .FirstOrDefault(s => string.Equals(s.ServiceName, ServiceName, StringComparison.OrdinalIgnoreCase));

            controller?.Refresh();
            return controller;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string arguments)
    {
        try
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
            var output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
            process.WaitForExit(30000);

            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return (-1, ex.Message);
        }
    }
}
