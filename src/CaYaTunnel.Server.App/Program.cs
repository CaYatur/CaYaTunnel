using System.IO;
using System.ServiceProcess;
using CaYaTunnel.Server.Configuration;
using CaYaTunnel.Server.Gateway;
using CaYaTunnel.Server.Registry;

namespace CaYaTunnel.Server.App;

/// <summary>
/// One executable, two modes. With <c>--service</c> it runs the gateway headless under the
/// Windows service control manager; otherwise it opens the admin window. Sharing a binary keeps
/// the two from drifting: the service runs exactly the code the admin app was tested against.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, WindowsServiceManager.ServiceSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new GatewayService());
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

/// <summary>
/// The headless host. Deliberately small: it starts the same <see cref="TunnelServer"/> the admin
/// app drives, and logs to disk because there is no window to log to.
/// </summary>
public sealed class GatewayService : ServiceBase
{
    private TunnelServer? _server;
    private GatewayLog? _log;

    public GatewayService()
    {
        ServiceName = WindowsServiceManager.ServiceName;
        CanShutdown = true;
        CanStop = true;
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            ServerPaths.EnsureCreated();

            _log = new GatewayLog(ServerPaths.LogDirectory);
            var config = new ServerConfigStore().Load();
            var registry = new TunnelRegistry();

            _server = new TunnelServer(config, registry, _log);
            _server.StartAsync().GetAwaiter().GetResult();

            _log.Info("service", "Gateway started under the Windows service host.");
        }
        catch (Exception ex)
        {
            // The SCM shows only a generic failure, so the reason has to reach the log file or
            // it is lost entirely.
            _log?.Error("service", "Gateway failed to start", ex);
            WriteFallbackLog(ex);
            throw;
        }
    }

    protected override void OnStop()
    {
        try
        {
            _server?.StopAsync().GetAwaiter().GetResult();
            _log?.Info("service", "Gateway stopped.");
        }
        catch (Exception ex)
        {
            _log?.Error("service", "Gateway did not stop cleanly", ex);
        }
    }

    protected override void OnShutdown() => OnStop();

    private static void WriteFallbackLog(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "cayatunnel-service-error.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing further we can do.
        }
    }
}
