using System.Windows;
using FutureSpeed.App.Localization;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.App.Views.Dialogs;

/// <summary>
/// Shows the pre-flight space verdict. "Continue anyway" appears only when the sizes
/// could not be determined - a genuine shortfall is not overridable.
/// </summary>
public partial class SpaceDialog : Window
{
    public SpaceDialog(SpaceValidationResult validation, ILocalizationService localization)
    {
        InitializeComponent();

        var culture = localization.Culture;

        TitleText.Text = localization["Space_Title"];
        RequiredText.Text = ByteFormatter.FormatSize(validation.RequiredBytes, culture);
        AvailableText.Text = ByteFormatter.FormatSize(validation.AvailableBytes, culture);
        MissingText.Text = ByteFormatter.FormatSize(validation.MissingBytes, culture);

        VolumeText.Text = string.IsNullOrWhiteSpace(validation.VolumeLabel)
            ? validation.DestinationRoot
            : $"{validation.VolumeLabel} — {validation.DestinationRoot} ({validation.FileSystem})";

        ReclaimableText.Text = validation.ReclaimableBytes > 0
            ? $"{localization["Space_Reclaimable"]}: {ByteFormatter.FormatSize(validation.ReclaimableBytes, culture)}"
            : string.Empty;

        switch (validation.Status)
        {
            case SpaceValidationStatus.Undeterminable:
                MessageText.Text = localization["Space_Undeterminable"];
                ProceedButton.Visibility = Visibility.Visible;
                IconText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Warning");
                break;

            case SpaceValidationStatus.Sufficient:
                MessageText.Text = localization["Space_Sufficient"];
                IconText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Success");
                break;

            default:
                MessageText.Text = validation.Message ?? localization["Space_Insufficient"];
                break;
        }
    }

    public bool ProceedAnyway { get; private set; }

    private void OnProceed(object sender, RoutedEventArgs e)
    {
        ProceedAnyway = true;
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = true;
}
