using System.ComponentModel;
using CaYaTunnel.Client;
using CaYaTunnel.Client.App.ViewModels;
using CaYaTunnel.Ui;
using Xunit;

namespace CaYaTunnel.Tests;

/// <summary>
/// Guards the view models against the class of bug that shipped in 1.0.0: a computed property
/// the views bind to that never announces its own change. The nav button highlighted, the page
/// did not change, and nothing failed — the screens still rendered correctly when built fresh,
/// which is exactly why the screenshot check missed it.
/// </summary>
public class ShellNavigationTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(
        Path.GetTempPath(), $"cayatunnel-nav-{Guid.NewGuid():n}.json");

    [Fact]
    public void Changing_the_page_announces_the_property_the_views_actually_bind_to()
    {
        var shell = CreateShell();
        var raised = new List<string>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        shell.Page = ClientPage.Devices;

        Assert.Equal(ClientPage.Devices, shell.Page);
        Assert.Contains(nameof(ShellViewModel.PageName), raised);
        Assert.Equal("Devices", shell.PageName);
    }

    [Fact]
    public void Navigating_by_command_reaches_every_page()
    {
        var shell = CreateShell();

        foreach (var page in Enum.GetValues<ClientPage>())
        {
            var raised = new List<string>();
            ((INotifyPropertyChanged)shell).PropertyChanged += Track;

            shell.GoToCommand.Execute(page.ToString());

            ((INotifyPropertyChanged)shell).PropertyChanged -= Track;

            Assert.Equal(page, shell.Page);
            Assert.Equal(page.ToString(), shell.PageName);

            // The starting page is already selected, so it legitimately raises nothing.
            if (page != ClientPage.Tunnels)
            {
                Assert.Contains(nameof(ShellViewModel.PageName), raised);
            }

            void Track(object? sender, PropertyChangedEventArgs e) => raised.Add(e.PropertyName ?? "");
        }
    }

    [Fact]
    public void Setting_the_same_page_twice_does_not_churn_the_bindings()
    {
        var shell = CreateShell();
        shell.Page = ClientPage.Settings;

        var raised = new List<string>();
        ((INotifyPropertyChanged)shell).PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        shell.Page = ClientPage.Settings;

        Assert.DoesNotContain(nameof(ShellViewModel.PageName), raised);
    }

    private ShellViewModel CreateShell()
    {
        var store = new ClientSettingsStore(_settingsPath);
        var settings = new ClientSettings { DeviceName = "TEST-PC" };

        // Not usable on purpose: constructing the shell must not try to reach a gateway.
        var profile = new ClientConnectionProfile("", 0, "", null, "CaYaTunnel", Provisioned: false);

        return new ShellViewModel(store, settings, profile);
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                File.Delete(_settingsPath);
            }
        }
        catch (IOException)
        {
            // Temp file.
        }
    }
}

/// <summary>
/// Exiting is the one thing a user cannot work around, so it is guarded rather than trusted.
/// Both apps shipped a version where closing the window left a process running with no window:
/// teardown resumed on the UI thread while the UI thread waited for teardown.
/// </summary>
public class ShutdownGuardTests
{
    [Fact]
    public void Teardown_runs_off_the_calling_thread()
    {
        // The whole fix: the UI thread stays free, so a continuation that wants it can have it.
        var callingThread = Environment.CurrentManagedThreadId;
        var workerThread = 0;

        var finished = ShutdownGuard.Run(() =>
        {
            workerThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        Assert.True(finished);
        Assert.NotEqual(callingThread, workerThread);
    }

    [Fact]
    public void A_teardown_that_never_finishes_is_abandoned_rather_than_trapping_the_process()
    {
        var stuck = new TaskCompletionSource();

        var finished = ShutdownGuard.Run(() => stuck.Task, TimeSpan.FromMilliseconds(300));

        Assert.False(finished);
        stuck.TrySetResult();
    }

    [Fact]
    public void A_teardown_that_throws_still_lets_the_process_exit()
    {
        var finished = ShutdownGuard.Run(() => throw new IOException("socket refused to close"));

        Assert.True(finished);
    }
}

/// <summary>
/// The lock that stops two copies fighting over one set of state. Keyed on that state, so
/// separate portable installs stay independent.
/// </summary>
public class SingleInstanceTests
{
    [Fact]
    public void A_second_claim_on_the_same_key_is_refused()
    {
        var key = $"cayatunnel-test-{Guid.NewGuid():n}";

        Assert.True(SingleInstance.TryClaim(key, out var first));
        using (first)
        {
            Assert.False(SingleInstance.TryClaim(key, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void Releasing_the_lock_lets_the_next_launch_through()
    {
        var key = $"cayatunnel-test-{Guid.NewGuid():n}";

        Assert.True(SingleInstance.TryClaim(key, out var first));
        first!.Dispose();

        Assert.True(SingleInstance.TryClaim(key, out var second));
        second!.Dispose();
    }

    [Fact]
    public void Different_keys_are_independent()
    {
        // Two portable clients in separate folders, pointed at different gateways, is a setup
        // that has to keep working.
        var a = $"cayatunnel-test-a-{Guid.NewGuid():n}";
        var b = $"cayatunnel-test-b-{Guid.NewGuid():n}";

        Assert.True(SingleInstance.TryClaim(a, out var first));
        using (first)
        {
            Assert.True(SingleInstance.TryClaim(b, out var second));
            second!.Dispose();
        }
    }

    [Fact]
    public async Task The_holder_is_told_when_another_launch_is_blocked()
    {
        var key = $"cayatunnel-test-{Guid.NewGuid():n}";

        Assert.True(SingleInstance.TryClaim(key, out var holder));
        using (holder)
        {
            var signalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            holder!.SecondInstanceAttempted += () => signalled.TrySetResult();

            Assert.False(SingleInstance.TryClaim(key, out _));

            // This is what makes launching again bring the window back instead of doing nothing.
            await signalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
