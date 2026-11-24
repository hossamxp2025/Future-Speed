using System.Windows.Data;
using System.Windows.Markup;

namespace FutureSpeed.App.Localization;

/// <summary>
/// XAML shorthand: <c>Text="{loc:Str Nav_Dashboard}"</c>.
/// Returns a one-way binding to the localisation singleton's indexer, so switching
/// language updates every label without reloading the views.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class StrExtension : MarkupExtension
{
    public StrExtension() { }

    public StrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
