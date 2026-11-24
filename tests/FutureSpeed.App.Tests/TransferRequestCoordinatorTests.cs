using System.IO;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using Xunit;

namespace FutureSpeed.App.Tests;

public sealed class TransferRequestCoordinatorTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeSettingsService _settings;
    private readonly TransferQueue _queue;
    private readonly TransferRequestCoordinator _coordinator;

    public TransferRequestCoordinatorTests()
    {
        _settings = new FakeSettingsService(Path.Combine(_tree.Root, "appdata"));

        // A real queue with a real engine, but every job is created paused-at-the-gate:
        // the tests only assert on what was queued, not on copied bytes.
        var drives = new DriveService();
        var engine = new TransferEngine(
            new FileSystemScanner(),
            new SpaceValidator(drives),
            drives,
            new Sha256HashService(),
            new PolicyConflictResolver(ConflictPolicy.Skip));

        _queue = new TransferQueue(engine);

        _coordinator = new TransferRequestCoordinator(
            _queue,
            _settings,
            _clipboard,
            LocalizationService.Instance,
            TestLogger.For<TransferRequestCoordinator>());
    }

    private TransferRequest Request(TransferMode mode, string destination, params string[] sources)
        => new() { Mode = mode, Destination = destination, Sources = sources.ToList(), Compact = true };

    [Fact]
    public void Several_folders_and_files_become_one_job()
    {
        var a = _tree.Folder("a");
        var b = _tree.Folder("b");
        var file = _tree.File("c.txt");
        var destination = _tree.Folder("dest");

        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, a, b, file));

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Job!.Job.Sources.Count);
        Assert.Equal(TransferMode.Copy, result.Job.Job.Mode);
    }

    [Fact]
    public void The_same_folder_listed_twice_is_only_queued_once()
    {
        var a = _tree.Folder("a");
        var destination = _tree.Folder("dest");

        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, a, a.ToUpperInvariant(), a + @"\"));

        Assert.True(result.Succeeded);
        Assert.Single(result.Job!.Job.Sources);
    }

    [Fact]
    public void Sources_that_no_longer_exist_are_dropped()
    {
        var a = _tree.Folder("a");
        var destination = _tree.Folder("dest");

        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, a, Path.Combine(_tree.Root, "ghost")));

        Assert.True(result.Succeeded);
        Assert.Single(result.Job!.Job.Sources);
    }

    [Fact]
    public void A_request_with_no_usable_source_is_refused()
    {
        var destination = _tree.Folder("dest");
        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, Path.Combine(_tree.Root, "ghost")));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void A_request_without_a_destination_is_refused()
    {
        var a = _tree.Folder("a");
        var result = _coordinator.Submit(Request(TransferMode.Copy, string.Empty, a));

        Assert.False(result.Succeeded);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void A_destination_inside_the_source_is_refused()
    {
        var source = _tree.Folder("source");
        var inside = Path.Combine(source, "nested");
        Directory.CreateDirectory(inside);

        var result = _coordinator.Submit(Request(TransferMode.Copy, inside, source));

        Assert.False(result.Succeeded);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void A_missing_destination_folder_is_created()
    {
        var a = _tree.Folder("a");
        var destination = Path.Combine(_tree.Root, "brand new folder");

        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, a));

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void Arabic_paths_and_spaces_are_handled()
    {
        var source = _tree.Folder("مجلد المشاريع");
        var destination = _tree.Folder("نسخة احتياطية 2026");

        var result = _coordinator.Submit(Request(TransferMode.Move, destination, source));

        Assert.True(result.Succeeded);
        Assert.Equal(TransferMode.Move, result.Job!.Job.Mode);
        Assert.Contains("مجلد المشاريع", result.Job.Job.Sources[0]);
    }

    [Fact]
    public void Explorer_firing_the_same_verb_twice_reuses_the_running_job()
    {
        var a = _tree.Folder("a");
        _tree.File(@"a\big.bin", 1024);
        var destination = _tree.Folder("dest");

        var first = _coordinator.Submit(Request(TransferMode.Copy, destination, a));
        var second = _coordinator.Submit(Request(TransferMode.Copy, destination, a));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Same(first.Job, second.Job);
    }

    [Fact]
    public void A_clipboard_without_files_is_left_alone()
    {
        var destination = _tree.Folder("dest");
        _clipboard.Payload = ClipboardFilePayload.Empty;

        var result = _coordinator.PasteInto(destination);

        Assert.False(result.Succeeded);
        Assert.Empty(_queue.Jobs);
    }

    [Fact]
    public void A_cut_clipboard_produces_a_move_job()
    {
        var source = _tree.Folder("cut-me");
        var destination = _tree.Folder("dest");

        _clipboard.Payload = new ClipboardFilePayload(ClipboardOperation.Move, new[] { source });

        var result = _coordinator.PasteInto(destination);

        Assert.True(result.Succeeded);
        Assert.Equal(TransferMode.Move, result.Job!.Job.Mode);
    }

    [Fact]
    public void A_copied_clipboard_produces_a_copy_job()
    {
        var source = _tree.Folder("copy-me");
        var destination = _tree.Folder("dest");

        _clipboard.Payload = new ClipboardFilePayload(ClipboardOperation.Copy, new[] { source });

        var result = _coordinator.PasteInto(destination);

        Assert.True(result.Succeeded);
        Assert.Equal(TransferMode.Copy, result.Job!.Job.Mode);
    }

    [Fact]
    public void Two_different_transfers_can_sit_in_the_queue_together()
    {
        var a = _tree.Folder("a");
        var b = _tree.Folder("b");
        var destination1 = _tree.Folder("dest1");
        var destination2 = _tree.Folder("dest2");

        var first = _coordinator.Submit(Request(TransferMode.Copy, destination1, a));
        var second = _coordinator.Submit(Request(TransferMode.Copy, destination2, b));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Job, second.Job);
        Assert.Equal(2, _queue.Jobs.Count);
        Assert.NotEqual(first.Job!.Job.Id, second.Job!.Job.Id);
    }

    [Fact]
    public void The_queued_job_uses_the_current_settings()
    {
        _settings.Current.VerifyWithSha256 = true;
        _settings.Current.ConflictPolicy = ConflictPolicy.KeepNewest;

        var a = _tree.Folder("a");
        var destination = _tree.Folder("dest");

        var result = _coordinator.Submit(Request(TransferMode.Copy, destination, a));

        Assert.True(result.Job!.Job.Options.VerifyWithSha256);
        Assert.Equal(ConflictPolicy.KeepNewest, result.Job.Job.Options.ConflictPolicy);
    }

    public void Dispose()
    {
        _queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tree.Dispose();
    }
}
