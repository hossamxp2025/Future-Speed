using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Integration;

public sealed class SyncServiceTests
{
    [Fact]
    public async Task A_one_way_sync_plans_only_new_and_changed_files()
    {
        using var workspace = new TestWorkspace();
        var sync = new SyncService();

        var left = Path.Combine(workspace.Root, "left");
        var right = Path.Combine(workspace.Root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        File.WriteAllText(Path.Combine(left, "same.txt"), "identical");
        File.WriteAllText(Path.Combine(right, "same.txt"), "identical");
        File.SetLastWriteTimeUtc(Path.Combine(right, "same.txt"), File.GetLastWriteTimeUtc(Path.Combine(left, "same.txt")));

        File.WriteAllText(Path.Combine(left, "new.txt"), "only on the left");
        File.WriteAllText(Path.Combine(right, "extra.txt"), "only on the right");

        var plan = await sync.BuildPlanAsync(left, right, SyncDirection.OneWay);

        Assert.Single(plan.Transfer.Files);
        Assert.Equal("new.txt", plan.Transfer.Files[0].RelativePath);
        Assert.Empty(plan.ExtraDestinationFiles); // one-way never deletes
    }

    [Fact]
    public async Task A_mirror_sync_lists_the_extra_destination_files()
    {
        using var workspace = new TestWorkspace();
        var sync = new SyncService();

        var left = Path.Combine(workspace.Root, "left");
        var right = Path.Combine(workspace.Root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);

        File.WriteAllText(Path.Combine(left, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(right, "stale.txt"), "y");

        var plan = await sync.BuildPlanAsync(left, right, SyncDirection.Mirror);

        Assert.Single(plan.ExtraDestinationFiles);
        Assert.EndsWith("stale.txt", plan.ExtraDestinationFiles[0]);

        var deleted = await sync.DeleteExtrasAsync(plan);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(Path.Combine(right, "stale.txt")));
    }

    [Fact]
    public async Task Overlapping_folders_are_refused()
    {
        using var workspace = new TestWorkspace();
        var sync = new SyncService();

        var left = Path.Combine(workspace.Root, "left");
        var inside = Path.Combine(left, "inner");
        Directory.CreateDirectory(inside);

        var plan = await sync.BuildPlanAsync(left, inside, SyncDirection.OneWay);

        Assert.NotEmpty(plan.Problems);
        Assert.False(plan.HasWork);
    }
}

public sealed class ReportServiceTests
{
    private static async Task<TransferResult> RunSmallJobAsync(TestWorkspace workspace)
    {
        workspace.CreateFile("a.bin", 2048);
        var engine = workspace.CreateEngine();
        return await engine.ExecuteAsync(workspace.CreateJob(), null, PauseToken.None, CancellationToken.None);
    }

    [Theory]
    [InlineData(ReportFormat.Json, ".json")]
    [InlineData(ReportFormat.Csv, ".csv")]
    [InlineData(ReportFormat.Txt, ".txt")]
    public async Task A_report_is_written_in_each_format(ReportFormat format, string extension)
    {
        using var workspace = new TestWorkspace();
        var result = await RunSmallJobAsync(workspace);

        var reports = Path.Combine(workspace.Root, "reports");
        var path = await new ReportService().SaveAsync(result, format, reports);

        Assert.True(File.Exists(path));
        Assert.EndsWith(extension, path);

        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("a.bin", content);
    }

    [Fact]
    public async Task The_json_report_carries_the_totals()
    {
        using var workspace = new TestWorkspace();
        var result = await RunSmallJobAsync(workspace);

        var path = await new ReportService().SaveAsync(result, ReportFormat.Json, Path.Combine(workspace.Root, "reports"));
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));

        var totals = document.RootElement.GetProperty("totals");

        Assert.Equal(1, totals.GetProperty("files").GetInt32());
        Assert.Equal(1, totals.GetProperty("copied").GetInt32());
        Assert.Equal(2048, totals.GetProperty("transferredBytes").GetInt64());
    }
}

public sealed class JobStateStoreTests
{
    [Fact]
    public async Task A_checkpoint_round_trips()
    {
        using var workspace = new TestWorkspace();
        var store = new JsonJobStateStore(workspace.StateDirectory);

        var checkpoint = new JobCheckpoint
        {
            JobId = "job-1",
            Name = "nightly",
            DestinationRoot = workspace.Destination,
            Mode = TransferMode.Move,
            State = JobState.Paused,
            TotalFiles = 3,
            TotalBytes = 9000,
            CompletedRelativePaths = { "a.bin", "b.bin" },
            Sources = { workspace.Source }
        };

        await store.SaveAsync(checkpoint);
        var loaded = await store.LoadAsync("job-1");

        Assert.NotNull(loaded);
        Assert.Equal("nightly", loaded!.Name);
        Assert.Equal(TransferMode.Move, loaded.Mode);
        Assert.Equal(2, loaded.CompletedRelativePaths.Count);
    }

    [Fact]
    public async Task Only_interrupted_jobs_are_offered_for_resume()
    {
        using var workspace = new TestWorkspace();
        var store = new JsonJobStateStore(workspace.StateDirectory);

        await store.SaveAsync(new JobCheckpoint { JobId = "done", State = JobState.Completed });
        await store.SaveAsync(new JobCheckpoint { JobId = "paused", State = JobState.Paused });
        await store.SaveAsync(new JobCheckpoint { JobId = "running", State = JobState.Running });

        var resumable = await store.LoadResumableAsync();

        Assert.Equal(2, resumable.Count);
        Assert.DoesNotContain(resumable, c => c.JobId == "done");
    }

    [Fact]
    public async Task A_corrupt_checkpoint_is_ignored_rather_than_crashing()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.StateDirectory, "broken.job.json"), "{ not json");

        var store = new JsonJobStateStore(workspace.StateDirectory);
        var all = await store.LoadAllAsync();

        Assert.Empty(all);
    }

    [Fact]
    public async Task Deleting_a_checkpoint_works_and_is_idempotent()
    {
        using var workspace = new TestWorkspace();
        var store = new JsonJobStateStore(workspace.StateDirectory);

        await store.SaveAsync(new JobCheckpoint { JobId = "x", State = JobState.Paused });
        await store.DeleteAsync("x");
        await store.DeleteAsync("x");

        Assert.Null(await store.LoadAsync("x"));
    }
}

public sealed class TransferQueueTests
{
    [Fact]
    public async Task A_queued_job_runs_and_reports_as_finished()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 64 * 1024);

        await using var queue = new TransferQueue(workspace.CreateEngine());

        var finished = new TaskCompletionSource<QueuedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobFinished += (_, e) => finished.TrySetResult(e.Job);

        queue.Enqueue(workspace.CreateJob());

        var job = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(JobState.Completed, job.State);
        Assert.True(File.Exists(workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task A_job_that_is_only_added_does_not_start_on_its_own()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096);

        await using var queue = new TransferQueue(workspace.CreateEngine());
        var queued = queue.Enqueue(workspace.CreateJob(), startImmediately: false);

        await Task.Delay(1500);

        Assert.Equal(JobState.Created, queued.State);
        Assert.False(File.Exists(workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task A_scheduled_job_waits_for_its_time()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096);

        await using var queue = new TransferQueue(workspace.CreateEngine());

        var job = workspace.CreateJob();
        job.ScheduledFor = DateTimeOffset.Now.AddMinutes(10);
        var queued = queue.Enqueue(job);

        await Task.Delay(1500);

        Assert.Equal(JobState.Queued, queued.State);
        Assert.True(queued.IsScheduled);
        Assert.False(File.Exists(workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task Cancelling_a_waiting_job_marks_it_canceled()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096);

        await using var queue = new TransferQueue(workspace.CreateEngine());
        var queued = queue.Enqueue(workspace.CreateJob(), startImmediately: false);

        queue.CancelJob(queued.Job.Id);

        Assert.Equal(JobState.Canceled, queued.State);
        Assert.True(queued.IsFinished);
    }

    [Fact]
    public async Task Reordering_changes_the_priority_of_queued_jobs()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096);

        await using var queue = new TransferQueue(workspace.CreateEngine());

        var first = queue.Enqueue(workspace.CreateJob(), startImmediately: false);
        var second = queue.Enqueue(workspace.CreateJob(), startImmediately: false);

        queue.MoveUp(second.Job.Id);

        var order = queue.Jobs.Select(j => j.Job.Id).ToList();

        Assert.Equal(second.Job.Id, order[0]);
        Assert.Equal(first.Job.Id, order[1]);
        Assert.True(second.Job.Priority < first.Job.Priority);
    }

    [Fact]
    public async Task Finished_jobs_can_be_cleared_from_the_list()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096);

        await using var queue = new TransferQueue(workspace.CreateEngine());

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobFinished += (_, _) => finished.TrySetResult();
        queue.Enqueue(workspace.CreateJob());

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        queue.ClearFinished();

        Assert.Empty(queue.Jobs);
    }
}
