using System.Diagnostics;
using System.IO;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Ui;

namespace CaYaTunnel.Server.App;

/// <summary>
/// Creates the Windows Firewall rules the gateway needs, so an operator does not have to
/// translate their port configuration into netsh by hand — the step most likely to be done
/// wrong, and the one whose failure looks like "the tunnel is broken".
/// <para>
/// Every rule carries the same group name, which is what makes removal exact: it never touches a
/// rule someone else created.
/// </para>
/// </summary>
public static class FirewallManager
{
    private const string Group = "CaYaTunnel";
    private const string RulePrefix = "CaYaTunnel -";

    public static bool CanManage => StartupManager.IsElevated;

    /// <summary>Replaces this deployment's rules with ones matching the current configuration.</summary>
    public static (bool Ok, string Message) Apply(ServerConfig config)
    {
        if (!CanManage)
        {
            return (false, "Creating firewall rules needs administrator rights. Restart this app as administrator and try again.");
        }

        // Removed first so a changed port never leaves the old one open — a rule nobody
        // remembers creating is worse than one that was never created.
        Remove();

        var created = new List<string>();

        foreach (var (name, protocol, ports) in Plan(config))
        {
            var (exitCode, output) = Run("netsh",
                $"advfirewall firewall add rule name=\"{RulePrefix} {name}\" dir=in action=allow " +
                $"protocol={protocol} localport={ports} profile=any enable=yes " +
                $"description=\"Created by CaYaTunnel\" group=\"{Group}\"");

            if (exitCode != 0)
            {
                return (false, $"Windows refused the rule for {name}: {output}");
            }

            created.Add($"{name} ({protocol} {ports})");
        }

        return created.Count == 0
            ? (false, "Nothing to open — every listener is disabled.")
            : (true, "Opened: " + string.Join(", ", created) + ".");
    }

    public static (bool Ok, string Message) Remove()
    {
        if (!CanManage)
        {
            return (false, "Removing firewall rules needs administrator rights.");
        }

        var (exitCode, output) = Run("netsh", $"advfirewall firewall delete rule group=\"{Group}\"");

        // "No rules match" is a success from the caller's point of view: the desired state is
        // "none of ours exist", and that is now true.
        return exitCode == 0 || output.Contains("No rules match", StringComparison.OrdinalIgnoreCase)
            ? (true, "CaYaTunnel firewall rules removed.")
            : (false, output);
    }

    public static bool RulesExist()
    {
        var (exitCode, output) = Run("netsh", $"advfirewall firewall show rule name=all group=\"{Group}\"");
        return exitCode == 0 && output.Contains(RulePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What would be opened, in plain terms. Shown before anything is changed so the operator
    /// can see the whole list rather than approving something invisible.
    /// </summary>
    public static IEnumerable<(string Name, string Protocol, string Ports)> Plan(ServerConfig config)
    {
        yield return ("control port", "TCP", config.ControlPort.ToString());

        if (config.SinglePortMode)
        {
            // Everything hostname-routable already shares the control port; nothing else to open
            // beyond the tunnels that genuinely cannot share one.
            yield return DedicatedTcp(config);
            yield return DedicatedUdp(config);
            yield break;
        }

        if (config.EnableHttpRouter)
        {
            yield return ("HTTP and HTTPS", "TCP", $"{config.HttpPort},{config.HttpsPort}");
        }

        if (config.EnableMinecraftRouter)
        {
            yield return ("Minecraft", "TCP", config.MinecraftPort.ToString());
        }

        yield return DedicatedTcp(config);
        yield return DedicatedUdp(config);
    }

    private static (string, string, string) DedicatedTcp(ServerConfig config)
        => ("tunnel ports", "TCP", $"{config.TcpPortRangeStart}-{config.TcpPortRangeEnd}");

    private static (string, string, string) DedicatedUdp(ServerConfig config)
        => ("tunnel ports", "UDP", $"{config.TcpPortRangeStart}-{config.TcpPortRangeEnd}");

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
