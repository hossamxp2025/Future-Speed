using System.Collections.Concurrent;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

/// <summary>
/// Writes the readable report for any job that ends with failed files, whatever window
/// the user happens to have open.
///
/// It exists because the history page is the only other place that writes reports, and
/// that page is never created when Explorer opens a compact window on its own — which is
/// exactly the case where the user most needs the list of files that did not transfer.
/// </summary>
public sealed class JobCompletionReporter : IDisposable
{
    private readonly ITransferQueue _queue;
    private readonly ITransferReportLauncher _reports;
    private readonly ILogger<JobCompletionReporter> _logger;

    private readonly ConcurrentDictionary<string, Task<string?>> _written = new();
    private bool _started;

    public JobCompletionReporter(
        ITransferQueue queue,
        ITransferReportLauncher reports,
        ILogger<JobCompletionReporter> logger)
    {
        _queue = queue;
        _reports = reports;
        _logger = logger;
    }

    /// <summary>Subscribes to the queue. Safe to call more than once.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _queue.JobFinished += OnJobFinished;
    }

    /// <summary>The report written for a job, awaited if it is still being written.</summary>
    public Task<string?> ReportFor(string jobId)
        => _written.TryGetValue(jobId, out var task) ? task : Task.FromResult<string?>(null);

    private void OnJobFinished(object? sender, JobEventArgs e)
    {
        var result = e.Job.Result;
        if (result is null || result.FailedFiles == 0) return;

        _written.GetOrAdd(e.Job.Job.Id, _ => WriteAsync(result));
    }

    private async Task<string?> WriteAsync(Core.Abstractions.TransferResult result)
    {
        var path = await _reports.SaveAsync(result, ReportFormat.Html).ConfigureAwait(false);

        if (path is null) _logger.LogWarning("No failure report was written for job {Job}.", result.Job.Id);
        else _logger.LogInformation("Failure report written to {Path}.", path);

        return path;
    }

    public void Dispose()
    {
        if (!_started) return;
        _queue.JobFinished -= OnJobFinished;
        _started = false;
    }
}
