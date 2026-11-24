using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class TransferTunerTests
{
    [Theory]
    [InlineData(DriveKindHint.Nvme)]
    [InlineData(DriveKindHint.Ssd)]
    [InlineData(DriveKindHint.Hdd)]
    [InlineData(DriveKindHint.Usb)]
    [InlineData(DriveKindHint.Network)]
    [InlineData(DriveKindHint.Unknown)]
    public void The_buffer_always_stays_within_the_allowed_range(DriveKindHint kind)
    {
        var buffer = TransferTuner.GetBufferSize(50L * 1024 * 1024 * 1024, kind, kind);
        Assert.InRange(buffer, TransferTuner.MinBuffer, TransferTuner.MaxBuffer);
    }

    [Fact]
    public void A_huge_file_does_not_get_a_huge_buffer()
    {
        // Memory use must not scale with file size.
        var buffer = TransferTuner.GetBufferSize(500L * 1024 * 1024 * 1024, DriveKindHint.Nvme, DriveKindHint.Nvme);
        Assert.True(buffer <= TransferTuner.MaxBuffer);
    }

    [Fact]
    public void A_tiny_file_does_not_waste_a_multi_megabyte_buffer()
    {
        var buffer = TransferTuner.GetBufferSize(8 * 1024, DriveKindHint.Nvme, DriveKindHint.Nvme);
        Assert.True(buffer <= 64 * 1024, $"expected a small buffer, got {buffer}");
    }

    [Fact]
    public void The_slower_device_decides_the_profile()
    {
        var nvmeToUsb = TransferTuner.GetBufferSize(10 * 1024 * 1024, DriveKindHint.Nvme, DriveKindHint.Usb);
        var usbOnly = TransferTuner.GetBufferSize(10 * 1024 * 1024, DriveKindHint.Usb, DriveKindHint.Usb);

        Assert.Equal(usbOnly, nvmeToUsb);
    }

    [Fact]
    public void A_spinning_disk_copies_one_file_at_a_time()
        => Assert.Equal(1, TransferTuner.GetWorkerCount(DriveKindHint.Hdd, DriveKindHint.Hdd, cpuCount: 16));

    [Fact]
    public void Flash_storage_copies_several_files_at_once()
        => Assert.True(TransferTuner.GetWorkerCount(DriveKindHint.Nvme, DriveKindHint.Nvme, cpuCount: 16) >= 4);

    [Fact]
    public void A_network_share_uses_queue_depth()
        => Assert.True(TransferTuner.GetWorkerCount(DriveKindHint.Network, DriveKindHint.Network, cpuCount: 8) >= 2);

    [Fact]
    public void Manual_settings_are_honoured_and_clamped()
    {
        var options = new TransferOptions
        {
            AutoTune = false,
            BufferSizeBytes = 512 * 1024 * 1024, // absurd on purpose
            MaxParallelFiles = 500
        };

        var (buffer, workers) = TransferTuner.Resolve(options, 1024, DriveKindHint.Ssd, DriveKindHint.Ssd);

        Assert.Equal(TransferTuner.MaxBuffer, buffer);
        Assert.Equal(TransferTuner.MaxWorkers, workers);
    }

    [Fact]
    public void Auto_tuning_ignores_the_manual_values()
    {
        var options = new TransferOptions { AutoTune = true, MaxParallelFiles = 31 };
        var (_, workers) = TransferTuner.Resolve(options, 1024 * 1024, DriveKindHint.Hdd, DriveKindHint.Hdd);

        Assert.Equal(1, workers);
    }
}
