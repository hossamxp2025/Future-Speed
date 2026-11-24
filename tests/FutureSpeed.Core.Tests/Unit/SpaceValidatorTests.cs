using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

/// <summary>A drive service with scripted free space, so space maths is tested without a disk.</summary>
internal sealed class FakeDriveService : IDriveService
{
    public long? FreeSpace { get; set; }
    public DriveKindHint Kind { get; set; } = DriveKindHint.Ssd;
    public bool SameVolume { get; set; } = true;

    public IReadOnlyList<DriveModel> GetDrives() => Array.Empty<DriveModel>();

    public long? GetAvailableFreeSpace(string path) => FreeSpace;

    public string? GetVolumeRoot(string path) => Path.GetPathRoot(path);

    public DriveModel? GetDriveFor(string path) => new()
    {
        RootPath = GetVolumeRoot(path) ?? @"X:\",
        Label = "TEST",
        FileSystem = "NTFS",
        TotalBytes = 1_000_000_000,
        FreeBytes = FreeSpace ?? 0,
        Kind = Kind,
        IsReady = true
    };

    public bool AreOnSameVolume(string pathA, string pathB) => SameVolume;

    public DriveKindHint GetKindFor(string path) => Kind;
}

public sealed class SpaceValidatorTests
{
    private const long Mb = 1024 * 1024;
    private const long Gb = 1024 * Mb;

    private static ScanResult Scan(params (string Name, long Size)[] files)
    {
        var scan = new ScanResult();
        foreach (var (name, size) in files)
        {
            scan.Files.Add(new TransferItem
            {
                SourcePath = @"C:\src\" + name,
                DestinationPath = @"D:\dst\" + name,
                RelativePath = name,
                Size = size,
                LastWriteTimeUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
            });
        }

        scan.TotalBytes = scan.Files.Sum(f => f.Size);
        return scan;
    }

    private static SpaceValidator Validator(
        FakeDriveService drives,
        Func<string, bool>? exists = null,
        Func<string, long>? size = null,
        Func<string, DateTime>? lastWrite = null)
        => new(
            drives,
            exists ?? (_ => false),
            size ?? (_ => 0),
            lastWrite ?? (_ => DateTime.MinValue));

    [Fact]
    public void Enough_free_space_passes()
    {
        var drives = new FakeDriveService { FreeSpace = 10 * Gb };
        var result = Validator(drives).Validate(Scan(("a.bin", 1 * Gb)), @"D:\dst", new TransferOptions());

        Assert.Equal(SpaceValidationStatus.Sufficient, result.Status);
        Assert.True(result.CanProceed);
        Assert.Equal(0, result.MissingBytes);
    }

    [Fact]
    public void Too_little_free_space_blocks_the_transfer_and_reports_the_shortfall()
    {
        var drives = new FakeDriveService { FreeSpace = 1 * Gb };
        var result = Validator(drives).Validate(Scan(("a.bin", 4 * Gb)), @"D:\dst", new TransferOptions());

        Assert.Equal(SpaceValidationStatus.Insufficient, result.Status);
        Assert.False(result.CanProceed);
        Assert.True(result.MissingBytes >= 3 * Gb);
        Assert.Equal(1 * Gb, result.AvailableBytes);
    }

    [Fact]
    public void The_safety_margin_is_included_in_the_requirement()
    {
        // Exactly the file size free: the margin must push this into "insufficient".
        var drives = new FakeDriveService { FreeSpace = 100 * Mb };
        var result = Validator(drives).Validate(Scan(("a.bin", 100 * Mb)), @"D:\dst", new TransferOptions());

        Assert.Equal(SpaceValidationStatus.Insufficient, result.Status);
    }

    [Fact]
    public void Unknown_free_space_is_reported_as_undeterminable_and_needs_an_override()
    {
        var drives = new FakeDriveService { FreeSpace = null };
        var result = Validator(drives).Validate(Scan(("a.bin", 1 * Mb)), @"D:\dst", new TransferOptions());

        Assert.Equal(SpaceValidationStatus.Undeterminable, result.Status);
        Assert.True(result.RequiresOverride);
        Assert.True(result.CanProceed);
    }

    [Fact]
    public void An_empty_destination_path_is_invalid()
    {
        var result = Validator(new FakeDriveService { FreeSpace = Gb })
            .Validate(Scan(("a.bin", Mb)), string.Empty, new TransferOptions());

        Assert.Equal(SpaceValidationStatus.InvalidDestination, result.Status);
        Assert.False(result.CanProceed);
    }

    [Fact]
    public void Files_that_will_be_skipped_cost_nothing()
    {
        var drives = new FakeDriveService { FreeSpace = 200 * Mb };
        var validator = Validator(drives, exists: _ => true, size: _ => 500 * Mb);

        var options = new TransferOptions { ConflictPolicy = ConflictPolicy.Skip };
        var result = validator.Validate(Scan(("a.bin", 4 * Gb)), @"D:\dst", options);

        Assert.Equal(SpaceValidationStatus.Sufficient, result.Status);
    }

    [Fact]
    public void Auto_rename_needs_the_full_size_even_when_the_name_exists()
    {
        var drives = new FakeDriveService { FreeSpace = 2 * Gb };
        var validator = Validator(drives, exists: _ => true, size: _ => 4 * Gb);

        var options = new TransferOptions { ConflictPolicy = ConflictPolicy.AutoRename };
        var result = validator.Validate(Scan(("a.bin", 4 * Gb)), @"D:\dst", options);

        Assert.Equal(SpaceValidationStatus.Insufficient, result.Status);
    }

    [Fact]
    public void Ask_is_costed_as_the_worst_case()
    {
        // "Ask" could end in overwrite, so the check must assume the space is needed.
        var drives = new FakeDriveService { FreeSpace = 1 * Gb };
        var validator = Validator(drives, exists: _ => true, size: _ => 4 * Gb);

        var result = validator.Validate(Scan(("a.bin", 4 * Gb)), @"D:\dst", new TransferOptions());

        Assert.Equal(SpaceValidationStatus.Insufficient, result.Status);
        Assert.Equal(4 * Gb, result.ReclaimableBytes);
    }

    [Fact]
    public void KeepNewest_only_counts_files_that_will_actually_be_written()
    {
        var newer = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var drives = new FakeDriveService { FreeSpace = 500 * Mb };

        // Destination copies are newer than the source, so nothing is written.
        var validator = Validator(drives, exists: _ => true, size: _ => 4 * Gb, lastWrite: _ => newer);

        var options = new TransferOptions { ConflictPolicy = ConflictPolicy.KeepNewest };
        var result = validator.Validate(Scan(("a.bin", 4 * Gb)), @"D:\dst", options);

        Assert.Equal(SpaceValidationStatus.Sufficient, result.Status);
    }

    [Fact]
    public void The_requirement_is_the_sum_of_all_files()
    {
        var drives = new FakeDriveService { FreeSpace = 100 * Gb };
        var result = Validator(drives).Validate(
            Scan(("a.bin", 1 * Gb), ("b.bin", 2 * Gb), ("c.bin", 512 * Mb)),
            @"D:\dst",
            new TransferOptions());

        Assert.True(result.RequiredBytes >= 3 * Gb + 512 * Mb);
        Assert.Equal(SpaceValidationStatus.Sufficient, result.Status);
    }
}
