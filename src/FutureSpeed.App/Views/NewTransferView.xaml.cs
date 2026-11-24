using System.Windows;
using System.Windows.Controls;
using FutureSpeed.App.ViewModels;

namespace FutureSpeed.App.Views;

public partial class NewTransferView : UserControl
{
    public NewTransferView() => InitializeComponent();

    private NewTransferViewModel? ViewModel => DataContext as NewTransferViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            ViewModel?.AddPaths(paths);

        // Handled here so the window-level handler does not add the paths twice.
        e.Handled = true;
    }
}
