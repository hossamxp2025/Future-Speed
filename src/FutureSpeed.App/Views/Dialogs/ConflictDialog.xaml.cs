using System.IO;
using System.Windows;
using FutureSpeed.App.Localization;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.App.Views.Dialogs;

public partial class ConflictDialog : Window
{
    private readonly ILocalizationService _localization;

    public ConflictDialog(ConflictInfo conflict, ILocalizationService localization)
    {
        _localization = localization;
        InitializeComponent();

        SelectedAction = ConflictAction.Skip;

        FileNameText.Text = Path.GetFileName(conflict.DestinationPath);

        SourceSizeText.Text = ByteFormatter.FormatSize(conflict.SourceSize, localization.Culture);
        SourceDateText.Text = conflict.SourceLastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        DestinationSizeText.Text = ByteFormatter.FormatSize(conflict.DestinationSize, localization.Culture);
        DestinationDateText.Text = conflict.DestinationLastWriteUtc == DateTime.MinValue
            ? "-"
            : conflict.DestinationLastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        // A short verdict saves the user from comparing two timestamps themselves.
        HintText.Text = ConflictStrategy.LooksIdentical(conflict)
            ? localization["Conflict_Identical"]
            : conflict.SourceLastWriteUtc > conflict.DestinationLastWriteUtc
                ? localization["Conflict_SourceNewer"]
                : localization["Conflict_DestinationNewer"];
    }

    public ConflictAction SelectedAction { get; private set; }

    public bool ApplyToAll => ApplyAllCheck.IsChecked == true;

    private void OnOverwrite(object sender, RoutedEventArgs e) => Close(ConflictAction.Overwrite);
    private void OnSkip(object sender, RoutedEventArgs e) => Close(ConflictAction.Skip);
    private void OnRename(object sender, RoutedEventArgs e) => Close(ConflictAction.AutoRename);
    private void OnCancel(object sender, RoutedEventArgs e) => Close(ConflictAction.Cancel);

    private void Close(ConflictAction action)
    {
        SelectedAction = action;
        DialogResult = true;
    }
}
