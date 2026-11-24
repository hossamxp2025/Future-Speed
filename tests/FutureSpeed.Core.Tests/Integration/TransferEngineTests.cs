using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Tests.Unit;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Integration;

public sealed class TransferEngineTests
{
    private static readonly PauseToken NoPause = PauseToken.None;

    [Fact]
    public async Task A_folder_tree_is_copied_with_identical_content()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 64 * 1024);
        workspace.CreateFile(@"docs\b.bin", 150 * 1024);
        workspace.CreateFile(@"docs\deep\c.bin", 8 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob();

        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.Equal(3, result.CopiedFiles);
        Assert.Equal(0, result.FailedFiles);

        foreach (var relative in new[] { "a.bin", @"docs\b.bin", @"docs\deep\c.bin" })
        {
            var destination = workspace.DestinationPathOf(relative);
            Assert.True(File.Exists(destination), $"missing: {destination}");
            Assert.True(TestWorkspace.FilesHaveSameContent(Path.Combine(workspace.Source, relative), destination));
        }

        Assert.Empty(workspace.LeftoverPartFiles());
    }

    [Fact]
    public async Task Empty_folders_are_recreated_at_the_destination()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Source, "empty-one", "empty-two"));
        workspace.CreateFile("a.bin", 1024);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(workspace.CreateJob(), null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.True(Directory.Exists(Path.Combine(workspace.Destination, "source", "empty-one", "empty-two")));
    }

    [Fact]
    public async Task Timestamps_are_preserved()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("a.bin", 4096);
        var stamp = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, stamp);

        var engine = workspace.CreateEngine();
        await engine.ExecuteAsync(workspace.CreateJob(), null, NoPause, CancellationToken.None);

        var destination = workspace.DestinationPathOf("a.bin");
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(destination), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Hidden_files_keep_their_attribute()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("secret.bin", 2048);
        File.SetAttributes(source, FileAttributes.Hidden);

        var engine = workspace.CreateEngine();
        await engine.ExecuteAsync(workspace.CreateJob(), null, NoPause, CancellationToken.None);

        var attributes = File.GetAttributes(workspace.DestinationPathOf("secret.bin"));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task Move_deletes_the_source_only_after_a_successful_copy()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("a.bin", 32 * 1024);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(
            workspace.CreateJob(TransferMode.Move),
            null,
            NoPause,
            CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task Verification_records_matching_hashes()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 100 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(configure: o => o.VerifyWithSha256 = true);

        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.False(string.IsNullOrEmpty(item.SourceHash));
        Assert.Equal(item.SourceHash, item.DestinationHash);
    }

    [Fact]
    public async Task A_verified_move_also_removes_the_source()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("a.bin", 64 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(TransferMode.Move, o =>
        {
            o.VerifyWithSha256 = true;
            o.AllowSameVolumeFastMove = true; // must be ignored while verifying
        });

        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.False(File.Exists(source));
        Assert.Equal(FileOutcome.Moved, result.Items[0].Outcome);
        Assert.False(string.IsNullOrEmpty(result.Items[0].DestinationHash));
    }

    [Fact]
    public async Task Skip_policy_leaves_an_existing_file_untouched()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096, fill: 0x01);
        var existing = workspace.CreateDestinationFile(@"source\a.bin", 10, fill: 0x99);

        var engine = workspace.CreateEngine(new PolicyConflictResolver(ConflictPolicy.Skip));
        var job = workspace.CreateJob(configure: o => o.ConflictPolicy = ConflictPolicy.Skip);

        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

        Assert.Equal(1, result.SkippedFiles);
        Assert.Equal(10, new FileInfo(existing).Length);
    }

    [Fact]
    public async Task Overwrite_policy_replaces_the_destination()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("a.bin", 4096, fill: 0x01);
        workspace.CreateDestinationFile(@"source\a.bin", 10, fill: 0x99);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(workspace.CreateJob(), null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.True(TestWorkspace.FilesHaveSameContent(source, workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task Auto_rename_keeps_both_copies()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 4096, fill: 0x01);
        workspace.CreateDestinationFile(@"source\a.bin", 10, fill: 0x99);

        var engine = workspace.CreateEngine(new PolicyConflictResolver(ConflictPolicy.AutoRename));
        var job = workspace.CreateJob(configure: o => o.ConflictPolicy = ConflictPolicy.AutoRename);

        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.True(File.Exists(workspace.DestinationPathOf("a.bin")));
        Assert.True(File.Exists(workspace.DestinationPathOf("a (2).bin")));
        Assert.Equal(10, new FileInfo(workspace.DestinationPathOf("a.bin")).Length);
    }

    [Fact]
    public async Task A_destination_inside_the_source_is_refused()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob();
        job.DestinationRoot = Path.Combine(workspace.Source, "nested");

        await Assert.ThrowsAsync<InvalidDestinationException>(
            () => engine.ExecuteAsync(job, null, NoPause, CancellationToken.None));
    }

    [Fact]
    public async Task A_transfer_that_does_not_fit_never_starts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 512 * 1024);

        // A volume with 1 KB free.
        var drives = new FakeDriveService { FreeSpace = 1024 };
        var engine = workspace.CreateEngine(drives: drives);

        var exception = await Assert.ThrowsAsync<InsufficientSpaceException>(
            () => engine.ExecuteAsync(workspace.CreateJob(), null, NoPause, CancellationToken.None));

        Assert.Equal(SpaceValidationStatus.Insufficient, exception.Validation.Status);
        Assert.True(exception.Validation.MissingBytes > 0);
        Assert.False(File.Exists(workspace.DestinationPathOf("a.bin")));
    }

    [Fact]
    public async Task Cancelling_stops_the_job_and_leaves_no_partial_file()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("big.bin", 6 * 1024 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(configure: o =>
        {
            o.MaxBytesPerSecond = 1024 * 1024; // slow enough to cancel mid-file
            o.MaxParallelFiles = 1;
        });

        using var cts = new CancellationTokenSource();
        var run = engine.ExecuteAsync(job, null, NoPause, cts.Token);

        await Task.Delay(400);
        cts.Cancel();

        var result = await run;

        Assert.Equal(JobState.Canceled, result.FinalState);
        Assert.Empty(workspace.LeftoverPartFiles());
        Assert.False(File.Exists(workspace.DestinationPathOf("big.bin")));
    }

    [Fact]
    public async Task A_paused_job_makes_no_progress_and_finishes_after_resume()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("big.bin", 3 * 1024 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(configure: o =>
        {
            o.MaxBytesPerSecond = 2 * 1024 * 1024;
            o.MaxParallelFiles = 1;
        });

        var pause = new PauseTokenSource();
        TransferProgress? latest = null;
        var progress = new Progress<TransferProgress>(p => latest = p);

        var run = engine.ExecuteAsync(job, progress, pause.Token, CancellationToken.None);

        await Task.Delay(300);
        pause.Pause();
        await Task.Delay(400);

        var atPause = Volatile.Read(ref latest)?.TransferredBytes ?? 0;
        await Task.Delay(500);
        var afterWaiting = Volatile.Read(ref latest)?.TransferredBytes ?? 0;

        Assert.True(afterWaiting - atPause < 512 * 1024, "a paused job should not keep transferring");

        pause.Resume();
        var result = await run;

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.True(TestWorkspace.FilesHaveSameContent(
            Path.Combine(workspace.Source, "big.bin"),
            workspace.DestinationPathOf("big.bin")));
    }

    [Fact]
    public async Task Progress_ends_at_the_full_size_and_file_count()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 200 * 1024);
        workspace.CreateFile("b.bin", 300 * 1024);

        TransferProgress? last = null;
        var progress = new Progress<TransferProgress>(p => last = p);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(workspace.CreateJob(), progress, NoPause, CancellationToken.None);

        // The Progress<T> callback is posted asynchronously; give it a moment to land.
        await Task.Delay(300);

        Assert.Equal(500 * 1024, result.TransferredBytes);
        Assert.NotNull(last);
        Assert.Equal(2, last!.TotalFiles);
    }

    [Fact]
    public async Task One_unreadable_file_does_not_stop_the_others()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("good-1.bin", 32 * 1024);
        var locked = workspace.CreateFile("locked.bin", 32 * 1024);
        workspace.CreateFile("good-2.bin", 32 * 1024);

        // Hold the file open with no sharing, which is what a running program does.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var engine = workspace.CreateEngine();
            var job = workspace.CreateJob(configure: o =>
            {
                o.ContinueOnError = true;
                o.MaxParallelFiles = 1;
            });

            var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);

            Assert.Equal(JobState.CompletedWithErrors, result.FinalState);
            Assert.Equal(1, result.FailedFiles);
            Assert.Equal(2, result.CopiedFiles);
            Assert.True(File.Exists(workspace.DestinationPathOf("good-1.bin")));
            Assert.True(File.Exists(workspace.DestinationPathOf("good-2.bin")));
        }
    }

    [Fact]
    public async Task A_resumed_job_does_not_copy_what_is_already_done()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 64 * 1024);
        workspace.CreateFile("b.bin", 64 * 1024);

        var store = new JsonJobStateStore(workspace.StateDirectory);
        var jobId = Guid.NewGuid().ToString("N");

        // First run: finish normally but keep a checkpoint listing both files.
        var engine = workspace.CreateEngine(stateStore: store);
        var first = workspace.CreateJob(configure: o => o.EnableResume = true, jobId: jobId);
        var firstResult = await engine.ExecuteAsync(first, null, NoPause, CancellationToken.None);
        Assert.Equal(JobState.Completed, firstResult.FinalState);

        await store.SaveAsync(new Core.Abstractions.JobCheckpoint
        {
            JobId = jobId,
            Name = "resume",
            DestinationRoot = workspace.Destination,
            Mode = TransferMode.Copy,
            State = JobState.Paused,
            CompletedRelativePaths = { Path.Combine("source", "a.bin") },
            Sources = { workspace.Source }
        });

        // Remove both destinations: only the file not marked as done must come back.
        File.Delete(workspace.DestinationPathOf("a.bin"));
        File.Delete(workspace.DestinationPathOf("b.bin"));

        var second = workspace.CreateJob(configure: o => o.EnableResume = true, jobId: jobId);
        var secondResult = await engine.ExecuteAsync(second, null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, secondResult.FinalState);
        Assert.False(File.Exists(workspace.DestinationPathOf("a.bin")));
        Assert.True(File.Exists(workspace.DestinationPathOf("b.bin")));
    }

    [Fact]
    public async Task A_checkpoint_is_written_for_an_interrupted_job_and_removed_after_success()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("big.bin", 6 * 1024 * 1024);

        var store = new JsonJobStateStore(workspace.StateDirectory);
        var engine = workspace.CreateEngine(stateStore: store);
        var jobId = Guid.NewGuid().ToString("N");

        var job = workspace.CreateJob(jobId: jobId, configure: o =>
        {
            o.EnableResume = true;
            o.MaxBytesPerSecond = 1024 * 1024;
            o.MaxParallelFiles = 1;
        });

        using var cts = new CancellationTokenSource();
        var run = engine.ExecuteAsync(job, null, NoPause, cts.Token);
        await Task.Delay(400);
        cts.Cancel();
        await run;

        Assert.NotNull(await store.LoadAsync(jobId));

        // A clean run of the same job clears the checkpoint.
        var finish = workspace.CreateJob(jobId: jobId, configure: o => o.EnableResume = true);
        var result = await engine.ExecuteAsync(finish, null, NoPause, CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.Null(await store.LoadAsync(jobId));
    }

    [Fact]
    public async Task A_speed_limit_is_respected()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 3 * 1024 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(configure: o =>
        {
            o.MaxBytesPerSecond = 1024 * 1024;
            o.MaxParallelFiles = 1;
        });

        var started = DateTime.UtcNow;
        var result = await engine.ExecuteAsync(job, null, NoPause, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.True(elapsed > TimeSpan.FromSeconds(1), $"3 MB at 1 MB/s should take over a second, took {elapsed}");
    }
}
