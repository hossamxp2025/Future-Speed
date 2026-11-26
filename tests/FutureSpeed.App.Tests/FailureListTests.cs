using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.App.ViewModels;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using Xunit;

namespace FutureSpeed.App.Tests;

/// <summary>A report launcher that records what it was asked to open.</summary>
internal sealed class FakeReportLauncher : ITransferReportLauncher
{
    public List<TransferResult> Opened { get; } = new();
    public List<ReportFormat> Saved { get; } = new();
    public string? PathToReturn { get; set; } = @"C:\reports\report.html";

    public Task<string?> OpenAsync(TransferResult result)
    {
        Opened.Add(result);
        return Task.FromResult(PathToReturn);
    }

    public Task<string?> OpenAsync(QueuedJob job) => OpenAsync(
        new TransferResult(job.Job, job.State, 0, 0, 0, 0, TimeSpan.Zero, Array.Empty<TransferItem>()));

    public Task<string?> SaveAsync(TransferResult result, ReportFormat? format = null)
    {
        Saved.Add(format ?? ReportFormat.Html);
        return Task.FromResult(PathToReturn);
    }
}

/// <summary>
/// The on-screen list of files that did not transfer: it must name the files, say why,
/// and give the user something to do about it.
/// </summary>
public sealed class FailureListTests
{
    private static TransferItem Failed(string name, FailureReason reason, long size = 1024) => new()
    {
        SourcePath = @"D:\src\" + name,
        DestinationPath = @"E:\dst\" + name,
        RelativePath = name,
        Size = size,
        Outcome = FileOutcome.Failed,
        Reason = reason,
        NativeErrorCode = reason == FailureReason.InUse ? 32 : null,
        Error = "raw message"
    };

    private static TransferResult ResultWith(params TransferItem[] items)
    {
        var job = new TransferJob { Name = "job", DestinationRoot = @"E:\dst" };
        job.Sources.Add(@"D:\src");

        return new TransferResult(
            job,
            items.Any(i => i.HasFailed) ? JobState.CompletedWithErrors : JobState.Completed,
            0,
            items.Count(i => !i.HasFailed),
            0,
            items.Count(i => i.HasFailed),
            TimeSpan.FromSeconds(3),
            items);
    }

    private static (FailureListViewModel Vm, FakeDialogService Dialogs, FakeReportLauncher Reports) Build(string language = "ar")
    {
        var localization = new LocalizationService();
        localization.SetLanguage(language);

        var dialogs = new FakeDialogService();
        var reports = new FakeReportLauncher();

        return (new FailureListViewModel(localization, dialogs, reports), dialogs, reports);
    }

    [Fact]
    public void An_empty_list_stays_hidden()
    {
        var (vm, _, _) = Build();
        vm.Show(ResultWith(new TransferItem
        {
            SourcePath = @"D:\src\ok.bin",
            DestinationPath = @"E:\dst\ok.bin",
            RelativePath = "ok.bin",
            Outcome = FileOutcome.Copied
        }));

        Assert.False(vm.HasFailures);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Each_row_carries_the_full_path_the_reason_and_the_action()
    {
        var (vm, _, _) = Build();
        vm.Show(ResultWith(Failed("locked.bin", FailureReason.InUse)));

        var row = Assert.Single(vm.Rows);

        Assert.Equal("locked.bin", row.FileName);
        Assert.Equal(@"D:\src\locked.bin", row.SourcePath);
        Assert.Equal(@"E:\dst\locked.bin", row.DestinationPath);
        Assert.Equal("InUse (32)", row.Code);
        Assert.Contains("مفتوح", row.Reason);              // Arabic UI
        Assert.False(string.IsNullOrWhiteSpace(row.Action));
        Assert.Equal(@"D:\src", row.SourceFolder);
    }

    [Fact]
    public void The_language_decides_the_wording()
    {
        var (vm, _, _) = Build("en");
        vm.Show(ResultWith(Failed("locked.bin", FailureReason.InUse)));

        Assert.Contains("open in another program", Assert.Single(vm.Rows).Reason);
    }

    [Fact]
    public void The_summary_counts_the_causes_and_the_missing_bytes()
    {
        var (vm, _, _) = Build("en");
        vm.Show(ResultWith(
            Failed("a.bin", FailureReason.InUse, 1000),
            Failed("b.bin", FailureReason.InUse, 2000),
            Failed("c.bin", FailureReason.AccessDenied, 500)));

        Assert.Equal(3, vm.Count);
        Assert.Equal(3500, vm.MissingBytes);
        Assert.Contains("2 ×", vm.ByReason);
    }

    [Fact]
    public void Copying_the_list_puts_every_path_on_the_clipboard()
    {
        var (vm, dialogs, _) = Build("en");
        vm.Show(ResultWith(Failed("a.bin", FailureReason.InUse), Failed("b.bin", FailureReason.AccessDenied)));

        vm.CopyListCommand.Execute(null);

        var text = Assert.Single(dialogs.CopiedText);
        Assert.Contains(@"D:\src\a.bin", text);
        Assert.Contains(@"D:\src\b.bin", text);
        Assert.Contains("InUse (32)", text);
        Assert.Single(dialogs.Infos);
    }

    [Fact]
    public void Nothing_is_copied_when_nothing_failed()
    {
        var (vm, dialogs, _) = Build();
        vm.CopyListCommand.Execute(null);

        Assert.Empty(dialogs.CopiedText);
    }

    [Fact]
    public async Task The_report_button_opens_the_report_for_this_job()
    {
        var (vm, dialogs, reports) = Build();
        var result = ResultWith(Failed("a.bin", FailureReason.InUse));
        vm.Show(result);

        await vm.OpenReportCommand.ExecuteAsync(null);

        Assert.Same(result, Assert.Single(reports.Opened));
        Assert.Empty(dialogs.Errors);
    }

    [Fact]
    public async Task A_report_that_cannot_be_written_is_reported_once()
    {
        var (vm, dialogs, reports) = Build();
        reports.PathToReturn = null;
        vm.Show(ResultWith(Failed("a.bin", FailureReason.InUse)));

        await vm.OpenReportCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Errors);
    }

    [Fact]
    public void Opening_the_folder_of_a_missing_file_falls_back_to_its_directory()
    {
        var (vm, dialogs, _) = Build();
        vm.Show(ResultWith(Failed("gone.bin", FailureReason.NotFound)));

        vm.OpenFolderCommand.Execute(vm.Rows[0]);

        Assert.Equal(@"D:\src", Assert.Single(dialogs.Opened));
    }

    [Fact]
    public void Opening_a_folder_with_no_selection_asks_for_one()
    {
        var (vm, dialogs, _) = Build();
        vm.Show(ResultWith(Failed("a.bin", FailureReason.InUse)));

        vm.OpenFolderCommand.Execute(null);

        Assert.Empty(dialogs.Opened);
        Assert.Single(dialogs.Infos);
    }

    [Fact]
    public void Scan_problems_appear_in_the_list_with_their_stage()
    {
        var (vm, _, _) = Build("en");

        var job = new TransferJob { Name = "job", DestinationRoot = @"E:\dst" };
        job.Scan = new ScanResult();
        job.Scan.Problems.Add(new ScanProblem(@"D:\src\secret", "denied", FailureReason.AccessDenied));

        vm.Show(new TransferResult(job, JobState.CompletedWithErrors, 0, 0, 0, 1, TimeSpan.Zero,
            Array.Empty<TransferItem>()));

        var row = Assert.Single(vm.Rows);
        Assert.Equal(@"D:\src\secret", row.SourcePath);
        Assert.Equal("while scanning the source", row.Stage);
    }

    [Fact]
    public void The_selected_row_survives_a_refresh()
    {
        var (vm, _, _) = Build();
        vm.Show(ResultWith(Failed("a.bin", FailureReason.InUse), Failed("b.bin", FailureReason.InUse)));

        vm.Selected = vm.Rows[1];
        vm.Update();

        Assert.Equal(@"D:\src\b.bin", vm.Selected!.SourcePath);
    }
}
