using System.Windows;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Views.Dialogs;
using FutureSpeed.Core.Abstractions;

namespace FutureSpeed.App.Services;

/// <summary>
/// Asks the user about a name clash. The engine calls this from a worker thread, so the
/// dialog is marshalled to the UI thread; the engine already serialises the calls, so the
/// user never sees two dialogs at once.
/// </summary>
public sealed class DialogConflictResolver : IConflictResolver
{
    private readonly ILocalizationService _localization;

    public DialogConflictResolver(ILocalizationService localization)
        => _localization = localization;

    public async Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken = default)
    {
        var application = Application.Current;
        if (application is null)
            return new ConflictDecision(ConflictAction.Skip, ApplyToAll: false);

        return await application.Dispatcher.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                return new ConflictDecision(ConflictAction.Cancel, ApplyToAll: false);

            var dialog = new ConflictDialog(conflict, _localization)
            {
                Owner = application.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? application.MainWindow,
                FlowDirection = _localization.FlowDirection
            };

            var result = dialog.ShowDialog();

            // Closing the dialog without choosing is treated as "skip this file", never
            // as permission to overwrite.
            return result == true
                ? new ConflictDecision(dialog.SelectedAction, dialog.ApplyToAll)
                : new ConflictDecision(ConflictAction.Skip, ApplyToAll: false);
        });
    }
}
