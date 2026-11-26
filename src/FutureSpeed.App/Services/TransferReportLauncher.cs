using System.IO;
using FutureSpeed.App.Localization;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

/// <summary>Writes the human-readable report for a job and opens it.</summary>
public interface ITransferReportLauncher
{
    /// <summary>Writes the HTML report for a finished job and opens it; null when it could not be written.</summary>
    Task<string?> OpenAsync(TransferResult result);

    /// <summary>Same, for a job that is still running: the report is a snapshot of right now.</summary>
    Task<string?> OpenAsync(QueuedJob job);

    /// <summary>Writes the report without opening it (used when a job finishes with failures).</summary>
    Task<string?> SaveAsync(TransferResult result, ReportFormat? format = null);
}

public sealed class TransferReportLauncher : ITransferReportLauncher
{
    private readonly IReportService _reports;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILocalizationService _localization;
    private readonly ILogger<TransferReportLauncher> _logger;

    public TransferReportLauncher(
        IReportService reports,
        ISettingsService settings,
        IDialogService dialogs,
        ILocalizationService localization,
        ILogger<TransferReportLauncher> logger)
    {
        _reports = reports;
        _settings = settings;
        _dialogs = dialogs;
        _localization = localization;
        _logger = logger;
    }

    public async Task<string?> OpenAsync(TransferResult result)
    {
        var path = await SaveAsync(result, ReportFormat.Html).ConfigureAwait(true);
        if (path is null) return null;

        _dialogs.OpenFile(path);
        return path;
    }

    public Task<string?> OpenAsync(QueuedJob job) => OpenAsync(job.Result ?? Snapshot(job));

    public async Task<string?> SaveAsync(TransferResult result, ReportFormat? format = null)
    {
        try
        {
            return await _reports.SaveAsync(
                result,
                format ?? ReportFormat.Html,
                _settings.ReportsDirectory,
                _localization.Culture).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the transfer report.");
            return null;
        }
    }

    /// <summary>
    /// A result for a job that has not finished: the numbers come from the latest progress
    /// snapshot and the file list from the scan, so the report reflects the run so far.
    /// </summary>
    private static TransferResult Snapshot(QueuedJob job)
    {
        var items = job.Job.Scan?.Files ?? (IReadOnlyList<TransferItem>)Array.Empty<TransferItem>();
        var progress = job.Progress;

        return new TransferResult(
            job.Job,
            job.State,
            progress.TransferredBytes,
            progress.CompletedFiles,
            progress.SkippedFiles,
            progress.FailedFiles,
            progress.Elapsed,
            items);
    }
}
