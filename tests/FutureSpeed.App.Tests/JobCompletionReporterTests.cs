using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FutureSpeed.App.Tests;

/// <summary>An engine that does nothing but hand back the result the test wants.</summary>
internal sealed class StubEngine : ITransferEngine
{
    private readonly int _failedFiles;

    public StubEngine(int failedFiles) => _failedFiles = failedFiles;

    public Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        SkipSignal skipSignal,
        CancellationToken cancellationToken)
    {
        var items = new List<TransferItem>();

        for (var i = 0; i < _failedFiles; i++)
        {
            items.Add(new TransferItem
            {
                SourcePath = $@"D:\src\locked-{i}.bin",
                DestinationPath = $@"E:\dst\locked-{i}.bin",
                RelativePath = $"locked-{i}.bin",
                Size = 10,
                Outcome = FileOutcome.Failed,
                Reason = FailureReason.InUse,
                NativeErrorCode = 32
            });
        }

        job.FinishedAt = DateTimeOffset.Now;

        return Task.FromResult(new TransferResult(
            job,
            _failedFiles > 0 ? JobState.CompletedWithErrors : JobState.Completed,
            0, 0, 0, _failedFiles, TimeSpan.FromSeconds(1), items));
    }

    public Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
        => ExecuteAsync(job, progress, pauseToken, new SkipSignal(), cancellationToken);
}

/// <summary>
/// The report must exist even when no window that writes reports was ever created —
/// the case when Explorer opens the compact window on its own.
/// </summary>
public sealed class JobCompletionReporterTests
{
    private static (JobCompletionReporter Reporter, FakeReportLauncher Reports, TransferQueue Queue) Build(int failures)
    {
        var queue = new TransferQueue(new StubEngine(failures), NullLogger<TransferQueue>.Instance);
        var reports = new FakeReportLauncher();
        var reporter = new JobCompletionReporter(queue, reports, NullLogger<JobCompletionReporter>.Instance);

        reporter.Start();
        return (reporter, reports, queue);
    }

    private static TransferJob NewJob()
    {
        var job = new TransferJob { Name = "job", DestinationRoot = @"E:\dst" };
        job.Sources.Add(@"D:\src");
        return job;
    }

    private static async Task<QueuedJob> RunAsync(TransferQueue queue)
    {
        var queued = queue.Enqueue(NewJob());

        for (var i = 0; i < 100 && !queued.IsFinished; i++)
            await Task.Delay(20);

        Assert.True(queued.IsFinished, "the stub job should have finished");
        return queued;
    }

    [Fact]
    public async Task A_job_with_failures_gets_an_html_report()
    {
        var (reporter, reports, queue) = Build(failures: 2);
        await using (queue)
        {
            var queued = await RunAsync(queue);

            var path = await reporter.ReportFor(queued.Job.Id);

            Assert.Equal(@"C:\reports\report.html", path);
            Assert.Equal(ReportFormat.Html, Assert.Single(reports.Saved));
        }
    }

    [Fact]
    public async Task A_clean_job_writes_nothing()
    {
        var (reporter, reports, queue) = Build(failures: 0);
        await using (queue)
        {
            var queued = await RunAsync(queue);

            Assert.Null(await reporter.ReportFor(queued.Job.Id));
            Assert.Empty(reports.Saved);
        }
    }

    [Fact]
    public async Task The_report_is_written_once_per_job()
    {
        var (reporter, reports, queue) = Build(failures: 1);
        await using (queue)
        {
            var queued = await RunAsync(queue);

            await reporter.ReportFor(queued.Job.Id);
            await reporter.ReportFor(queued.Job.Id);

            Assert.Single(reports.Saved);
        }
    }

    [Fact]
    public async Task Disposing_stops_the_reporter()
    {
        var (reporter, reports, queue) = Build(failures: 1);
        await using (queue)
        {
            reporter.Dispose();
            await RunAsync(queue);

            Assert.Empty(reports.Saved);
        }
    }
}
