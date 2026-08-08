using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CaYaTunnel.Ui;

/// <summary>
/// Minimal INotifyPropertyChanged base. The apps are small enough that a full MVVM framework
/// would be more dependency than value.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread.
    /// <para>
    /// Everything interesting in this app is raised from a background thread — the gateway's
    /// listener tasks, the agent's link read loop. Touching a bound collection from there throws
    /// at an unpredictable later moment, so every event handler marshals through here.
    /// </para>
    /// </summary>
    protected static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
