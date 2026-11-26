using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

/// <summary>
/// Explorer can start one process per selected item, so several staging requests arrive
/// for a single user action. They must become one clipboard write.
/// </summary>
public sealed class ClipboardStagingBufferTests
{
    [Fact]
    public void An_empty_buffer_yields_nothing()
    {
        var buffer = new ClipboardStagingBuffer();

        Assert.False(buffer.HasPending);
        Assert.False(buffer.TakeAll().HasFiles);
    }

    [Fact]
    public void Several_single_file_launches_merge_into_one_selection()
    {
        var buffer = new ClipboardStagingBuffer();

        // 25 launches, one file each — exactly the case that produced a dialog per process.
        for (var i = 1; i <= 25; i++)
            buffer.Add(new[] { $@"D:\data\file-{i}.bin" }, ClipboardOperation.Copy);

        var payload = buffer.TakeAll();

        Assert.True(payload.HasFiles);
        Assert.Equal(25, payload.Files.Count);
        Assert.Equal(ClipboardOperation.Copy, payload.Operation);
    }

    [Fact]
    public void The_same_file_arriving_twice_is_staged_once()
    {
        var buffer = new ClipboardStagingBuffer();

        buffer.Add(new[] { @"D:\data\a.bin" }, ClipboardOperation.Copy);
        buffer.Add(new[] { @"D:\DATA\A.BIN" }, ClipboardOperation.Copy);
        buffer.Add(new[] { @"D:\data\a.bin" }, ClipboardOperation.Copy);

        Assert.Single(buffer.TakeAll().Files);
    }

    [Fact]
    public void A_cut_in_the_batch_makes_the_whole_batch_a_move()
    {
        // Losing the move would leave the originals behind without telling anyone.
        var buffer = new ClipboardStagingBuffer();

        buffer.Add(new[] { @"D:\a.bin" }, ClipboardOperation.Copy);
        buffer.Add(new[] { @"D:\b.bin" }, ClipboardOperation.Move);

        Assert.Equal(ClipboardOperation.Move, buffer.TakeAll().Operation);
        Assert.Equal(TransferMode.Move, new ClipboardFilePayload(ClipboardOperation.Move, Array.Empty<string>()).Mode);
    }

    [Fact]
    public void Taking_empties_the_buffer()
    {
        var buffer = new ClipboardStagingBuffer();
        buffer.Add(new[] { @"D:\a.bin" }, ClipboardOperation.Copy);

        Assert.True(buffer.TakeAll().HasFiles);
        Assert.False(buffer.HasPending);
        Assert.False(buffer.TakeAll().HasFiles);
    }

    [Fact]
    public void Blank_and_null_entries_are_ignored()
    {
        var buffer = new ClipboardStagingBuffer();

        buffer.Add(null, ClipboardOperation.Copy);
        buffer.Add(new[] { "", "   " }, ClipboardOperation.Copy);

        Assert.False(buffer.TakeAll().HasFiles);
    }

    [Fact]
    public void Arabic_paths_and_spaces_survive_the_merge()
    {
        var buffer = new ClipboardStagingBuffer();
        buffer.Add(new[] { @"D:\مشاريعي\ملف أول.txt" }, ClipboardOperation.Copy);
        buffer.Add(new[] { @"D:\My Folder\second file.txt" }, ClipboardOperation.Copy);

        var files = buffer.TakeAll().Files;

        Assert.Contains(@"D:\مشاريعي\ملف أول.txt", files);
        Assert.Contains(@"D:\My Folder\second file.txt", files);
    }

    [Fact]
    public async Task Concurrent_launches_do_not_lose_an_item()
    {
        var buffer = new ClipboardStagingBuffer();

        await Parallel.ForEachAsync(
            Enumerable.Range(1, 200),
            (i, _) =>
            {
                buffer.Add(new[] { $@"D:\data\f{i}.bin" }, ClipboardOperation.Copy);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(200, buffer.TakeAll().Files.Count);
    }

    [Fact]
    public void A_staging_request_is_recognised_as_work()
    {
        var request = new TransferRequest { StageOnly = true, Sources = { @"D:\a.bin" } };

        Assert.True(request.HasWork);

        var round = TransferRequest.FromJson(request.ToJson());
        Assert.NotNull(round);
        Assert.True(round!.StageOnly);
    }
}
