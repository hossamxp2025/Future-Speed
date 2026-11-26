using System.Windows.Controls;

namespace FutureSpeed.App.Views;

/// <summary>
/// The shared "files that did not transfer" card. Its DataContext is a
/// <see cref="ViewModels.FailureListViewModel"/>, so the compact window, the main
/// window and the history page all show the same thing.
/// </summary>
public partial class FailuresPanel : UserControl
{
    public FailuresPanel() => InitializeComponent();
}
