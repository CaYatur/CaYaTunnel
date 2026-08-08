using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace CaYaTunnel.Ui;

public enum AppLanguage
{
    /// <summary>Follow Windows: Turkish on a Turkish system, English everywhere else.</summary>
    System,
    Turkish,
    English,
}

/// <summary>
/// Runtime string lookup for the two shipped languages.
/// <para>
/// Deliberately not .resx satellite assemblies: the client is published as a single portable
/// file, and satellite assemblies are per-culture files that complicate that. A plain table
/// keeps everything in the one binary and lets the language change without a restart.
/// </para>
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private AppLanguage _language = AppLanguage.System;

    private Loc()
    {
    }

    public static Loc Current { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value)
            {
                return;
            }

            _language = value;

            // Refreshing the indexer refreshes every string binding in the app at once, so the
            // language switch takes effect immediately rather than at the next restart.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTurkish)));
        }
    }

    public bool IsTurkish => Resolve() == AppLanguage.Turkish;

    public string this[string key] => Get(key);

    /// <summary>Looks up a key, falling back to English and then to the key itself.</summary>
    public static string Get(string key)
    {
        if (!Strings.Table.TryGetValue(key, out var entry))
        {
            // Showing the key is better than showing nothing: it is obvious in a screenshot and
            // points straight at the missing entry.
            return key;
        }

        return Current.Resolve() == AppLanguage.Turkish ? entry.Turkish : entry.English;
    }

    /// <summary>Formats a string that carries {0}-style placeholders.</summary>
    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private AppLanguage Resolve()
    {
        if (_language != AppLanguage.System)
        {
            return _language;
        }

        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("tr", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Turkish
            : AppLanguage.English;
    }
}

/// <summary>
/// XAML shorthand: <c>Text="{ui:L TunnelsTitle}"</c>. Produces a binding rather than a constant,
/// so switching language updates the screen without reloading it.
/// </summary>
public sealed class LExtension : MarkupExtension
{
    public LExtension()
    {
    }

    public LExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

public readonly record struct LocalisedString(string English, string Turkish);
