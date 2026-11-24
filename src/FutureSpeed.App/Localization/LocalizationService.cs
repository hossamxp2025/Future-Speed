using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace FutureSpeed.App.Localization;

public interface ILocalizationService : INotifyPropertyChanged
{
    string this[string key] { get; }
    string Language { get; }
    FlowDirection FlowDirection { get; }
    CultureInfo Culture { get; }
    void SetLanguage(string language);
    string Format(string key, params object[] args);
}

/// <summary>
/// In-memory string catalogue with live switching. A singleton is exposed for XAML
/// (<see cref="Instance"/>) because the markup extension needs a binding source that
/// exists before any view model does; the DI container registers the same instance.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    public static LocalizationService Instance { get; } = new();

    private readonly Dictionary<string, Dictionary<string, string>> _catalogue;
    private string _language = "ar";

    public LocalizationService()
    {
        _catalogue = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ar"] = Strings.Arabic,
            ["en"] = Strings.English
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language => _language;

    public FlowDirection FlowDirection => _language == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public CultureInfo Culture => _language == "ar"
        ? CultureInfo.GetCultureInfo("ar-EG")
        : CultureInfo.GetCultureInfo("en-US");

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            if (_catalogue.TryGetValue(_language, out var table) && table.TryGetValue(key, out var value))
                return value;

            // Fall back to English, then to the key itself so a missing string is visible
            // during development instead of rendering as blank.
            if (_catalogue["en"].TryGetValue(key, out var fallback))
                return fallback;

            return key;
        }
    }

    public void SetLanguage(string language)
    {
        var normalized = _catalogue.ContainsKey(language) ? language : "ar";
        if (string.Equals(normalized, _language, StringComparison.OrdinalIgnoreCase)) return;

        _language = normalized;

        // "Item[]" refreshes every indexer binding at once.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }

    public string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(Culture, this[key], args);
        }
        catch (FormatException)
        {
            return this[key];
        }
    }
}
