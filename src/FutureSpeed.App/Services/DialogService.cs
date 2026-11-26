using System.Diagnostics;
using System.IO;
using System.Windows;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Views.Dialogs;
using FutureSpeed.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FutureSpeed.App.Services;

public interface IDialogService
{
    string[]? PickFiles(string? title = null);

    /// <summary>One folder, for a destination.</summary>
    string? PickFolder(string? title = null);

    /// <summary>Several folders at once, for building a source list.</summary>
    string[]? PickFolders(string? title = null);

    bool Confirm(string message, string? title = null);
    void Info(string message, string? title = null);
    void Error(string message, string? title = null);

    /// <summary>Shows the blocking space-problem dialog; true when the user chose to continue anyway.</summary>
    bool ShowSpaceProblem(SpaceValidationResult validation);

    void OpenInExplorer(string path);

    /// <summary>Opens a file with whatever Windows associates with it (used for the HTML report).</summary>
    void OpenFile(string path);

    /// <summary>Puts plain text on the clipboard; false when Windows would not give it up.</summary>
    bool CopyText(string text);
}

public sealed class DialogService : IDialogService
{
    private readonly ILocalizationService _localization;
    private readonly ILogger<DialogService> _logger;

    public DialogService(ILocalizationService localization, ILogger<DialogService> logger)
    {
        _localization = localization;
        _logger = logger;
    }

    private static Window? Owner => Application.Current?.Windows
        .OfType<Window>()
        .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public string[]? PickFiles(string? title = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title ?? _localization["Common_AddFiles"],
            Multiselect = true,
            CheckFileExists = true
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FileNames : null;
    }

    public string? PickFolder(string? title = null)
    {
        // OpenFolderDialog is the modern Windows picker and needs no WinForms interop.
        var dialog = new OpenFolderDialog
        {
            Title = title ?? _localization["Common_AddFolder"],
            Multiselect = false
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string[]? PickFolders(string? title = null)
    {
        // Ctrl-click / Shift-click selects several folders in one pass, so building a
        // source list does not mean reopening the dialog once per folder.
        var dialog = new OpenFolderDialog
        {
            Title = title ?? _localization["Common_AddFolder"],
            Multiselect = true
        };

        return dialog.ShowDialog(Owner) == true ? dialog.FolderNames.ToArray() : null;
    }

    public bool Confirm(string message, string? title = null)
        => Show(message, title ?? _localization["Common_Confirm"], MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    public void Info(string message, string? title = null)
        => Show(message, title ?? _localization["App_Name"], MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string message, string? title = null)
        => Show(message, title ?? _localization["Error_Title"], MessageBoxButton.OK, MessageBoxImage.Error);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner is null
            ? MessageBox.Show(message, title, buttons, image)
            : MessageBox.Show(owner, message, title, buttons, image);
    }

    public bool ShowSpaceProblem(SpaceValidationResult validation)
    {
        var dialog = new SpaceDialog(validation, _localization)
        {
            Owner = Owner,
            FlowDirection = _localization.FlowDirection
        };

        return dialog.ShowDialog() == true && dialog.ProceedAnyway;
    }

    public void OpenInExplorer(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open {Path} in Explorer.", path);
        }
    }

    public void OpenFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            // UseShellExecute: the report is HTML, so the user's browser handles it.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the file {Path}.", path);
        }
    }

    public bool CopyText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // Only one process owns the clipboard at a time, so one refusal means nothing.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (Exception ex) when (attempt < 5)
            {
                _logger.LogDebug(ex, "Clipboard busy, attempt {Attempt}.", attempt);
                Thread.Sleep(100);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not copy the text to the clipboard.");
                return false;
            }
        }

        return false;
    }
}
