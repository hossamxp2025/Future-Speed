using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Integration;

public sealed class TransferRequestIpcTests
{
    private static string UniquePipe() => "FutureSpeed.tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_request_reaches_the_running_instance()
    {
        var pipe = UniquePipe();
        await using var server = new TransferRequestServer(pipe);

        var received = new TaskCompletionSource<TransferRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestReceived += (_, e) => received.TrySetResult(e.Request);
        server.Start();

        var sent = await TransferRequestClient.TrySendAsync(new TransferRequest
        {
            Mode = TransferMode.Move,
            Sources = { @"D:\مشاريعي\ملف.txt", @"C:\data\a b.txt" },
            Destination = @"E:\نسخة احتياطية",
            Compact = true
        }, pipe);

        Assert.True(sent);

        var request = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TransferMode.Move, request.Mode);
        Assert.Equal(2, request.Sources.Count);
        Assert.Contains(@"D:\مشاريعي\ملف.txt", request.Sources);
        Assert.Equal(@"E:\نسخة احتياطية", request.Destination);
        Assert.True(request.Compact);
    }

    [Fact]
    public async Task Sending_without_a_listener_fails_fast_so_the_caller_can_host()
    {
        var sent = await TransferRequestClient.TrySendAsync(
            new TransferRequest { Destination = @"C:\x", Sources = { @"C:\y" } },
            UniquePipe(),
            timeoutMilliseconds: 300);

        Assert.False(sent);
    }

    [Fact]
    public async Task Several_requests_in_a_row_all_arrive()
    {
        var pipe = UniquePipe();
        await using var server = new TransferRequestServer(pipe);

        var count = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RequestReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref count) == 3) all.TrySetResult();
        };
        server.Start();

        for (var i = 0; i < 3; i++)
        {
            var ok = await TransferRequestClient.TrySendAsync(
                new TransferRequest { Destination = @"C:\dst", Sources = { $@"C:\src\{i}.bin" } }, pipe);
            Assert.True(ok);
        }

        await all.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(3, count);
    }

    [Fact]
    public void A_request_round_trips_through_json()
    {
        var original = new TransferRequest
        {
            Mode = TransferMode.Move,
            Sources = { @"C:\أ.txt" },
            Destination = @"D:\وجهة",
            Compact = true,
            FromClipboard = true
        };

        var copy = TransferRequest.FromJson(original.ToJson());

        Assert.NotNull(copy);
        Assert.Equal(original.Mode, copy!.Mode);
        Assert.Equal(original.Destination, copy.Destination);
        Assert.True(copy.FromClipboard);
        Assert.True(copy.HasWork);
    }

    [Fact]
    public void Malformed_json_is_rejected_rather_than_throwing()
        => Assert.Null(TransferRequest.FromJson("{not json"));

    [Fact]
    public void An_empty_request_reports_that_it_has_no_work()
        => Assert.False(new TransferRequest().HasWork);

    [Fact]
    public async Task Disposing_the_server_twice_is_safe()
    {
        var server = new TransferRequestServer(UniquePipe());
        server.Start();

        await server.DisposeAsync();
        await server.DisposeAsync();
    }
}

public sealed class SkipFileTests
{
    [Fact]
    public async Task Skipping_abandons_only_the_current_file()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("1-big.bin", 4 * 1024 * 1024);
        workspace.CreateFile("2-small.bin", 32 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(configure: o =>
        {
            o.MaxBytesPerSecond = 1024 * 1024;   // slow enough to interrupt the first file
            o.MaxParallelFiles = 1;
        });

        var skip = new SkipSignal();
        var run = engine.ExecuteAsync(job, null, PauseToken.None, skip, CancellationToken.None);

        await Task.Delay(500);
        skip.Request();

        var result = await run;

        Assert.Equal(JobState.Completed, result.FinalState);

        var big = result.Items.First(i => i.RelativePath.EndsWith("1-big.bin", StringComparison.OrdinalIgnoreCase));
        var small = result.Items.First(i => i.RelativePath.EndsWith("2-small.bin", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(FileOutcome.Skipped, big.Outcome);
        Assert.False(File.Exists(workspace.DestinationPathOf("1-big.bin")));

        // The rest of the job carried on.
        Assert.Equal(FileOutcome.Copied, small.Outcome);
        Assert.True(File.Exists(workspace.DestinationPathOf("2-small.bin")));
        Assert.Empty(workspace.LeftoverPartFiles());
    }

    [Fact]
    public async Task A_skip_that_is_never_needed_changes_nothing()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("a.bin", 16 * 1024);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(
            workspace.CreateJob(), null, PauseToken.None, new SkipSignal(), CancellationToken.None);

        Assert.Equal(JobState.Completed, result.FinalState);
        Assert.Equal(1, result.CopiedFiles);
    }

    [Fact]
    public async Task A_move_does_not_delete_the_source_of_a_skipped_file()
    {
        using var workspace = new TestWorkspace();
        var source = workspace.CreateFile("big.bin", 4 * 1024 * 1024);

        var engine = workspace.CreateEngine();
        var job = workspace.CreateJob(TransferMode.Move, o =>
        {
            o.MaxBytesPerSecond = 1024 * 1024;
            o.MaxParallelFiles = 1;
            o.AllowSameVolumeFastMove = false;   // force a real copy so there is something to skip
        });

        var skip = new SkipSignal();
        var run = engine.ExecuteAsync(job, null, PauseToken.None, skip, CancellationToken.None);

        await Task.Delay(500);
        skip.Request();
        await run;

        // The original must still be there: a skipped file was never transferred.
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void The_signal_is_consumed_exactly_once()
    {
        var signal = new SkipSignal();

        Assert.False(signal.Consume());

        signal.Request();
        Assert.True(signal.IsRequested);
        Assert.True(signal.Consume());
        Assert.False(signal.Consume());
    }

    [Fact]
    public async Task The_queue_can_skip_the_file_of_a_running_job()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("1-big.bin", 4 * 1024 * 1024);
        workspace.CreateFile("2-small.bin", 16 * 1024);

        await using var queue = new TransferQueue(workspace.CreateEngine());

        var finished = new TaskCompletionSource<QueuedJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.JobFinished += (_, e) => finished.TrySetResult(e.Job);

        var queued = queue.Enqueue(workspace.CreateJob(configure: o =>
        {
            o.MaxBytesPerSecond = 1024 * 1024;
            o.MaxParallelFiles = 1;
        }));

        await Task.Delay(1500);
        queue.SkipCurrentFile(queued.Job.Id);

        var job = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(job.Result!.SkippedFiles >= 1);
        Assert.Equal(JobState.Completed, job.State);
    }
}
