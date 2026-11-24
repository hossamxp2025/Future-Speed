using System.Windows;
using FutureSpeed.App.Services;
using FutureSpeed.App.ViewModels;

namespace FutureSpeed.App.Views;

public partial class MainWindow : Window
{
    private readonly INavigationService _navigation;

    public MainWindow(INavigationService navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.InitializeAsync();
    }

    /// <summary>
    /// Window-wide drag and drop: dropping anywhere adds the paths as transfer sources
    /// and brings the user to the transfer page, which is what people expect from a
    /// file-copy tool.
    /// </summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is null) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            ViewModel.NewTransfer.AddPaths(paths);
            _navigation.Go(AppPage.NewTransfer);
        }

        e.Handled = true;
    }
}
