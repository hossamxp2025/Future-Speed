using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.App.ViewModels;

/// <summary>One file that did not transfer, ready for the screen.</summary>
public sealed class FailureRowViewModel
{
    private readonly ILocalizationService _localization;

    public FailureRowViewModel(TransferFailure failure, ILocalizationService localization)
    {
        Failure = failure;
        _localization = localization;
    }

    public TransferFailure Failure { get; }

    public string FileName => Failure.FileName;

    /// <summary>The full source path — the first thing the user asks for.</summary>
    public string SourcePath => Failure.SourcePath;

    public string DestinationPath => Failure.DestinationPath ?? "—";
    public long Size => Failure.Size;

    /// <summary>Why it failed, in the UI language.</summary>
    public string Reason => FailureText.What(Failure.Reason, _localization.Culture);

    /// <summary>What to do about it, in the UI language.</summary>
    public string Action => FailureText.Fix(Failure.Reason, _localization.Culture);

    public string Code => Failure.Code;

    public string Stage => _localization["Fail_Origin_" + Failure.Origin];

    /// <summary>Technical message from the OS, shown as a tooltip rather than a headline.</summary>
    public string? Message => Failure.Message;

    public string? SourceFolder => Failure.SourceFolder;
}

/// <summary>
/// The list of files a job could not transfer, plus the actions that make it useful:
/// copy the list, open the detailed report, open a file's folder.
///
/// Shared by the compact window, the main window's current-operation card and the
/// history page, so all three say the same thing about the same job.
/// </summary>
public sealed partial class FailureListViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;
    private readonly IDialogService _dialogs;
    private readonly ITransferReportLauncher _reports;

    private QueuedJob? _job;
    private TransferResult? _result;

    public FailureListViewModel(
        ILocalizationService localization,
        IDialogService dialogs,
        ITransferReportLauncher reports)
    {
        _localization = localization;
        _dialogs = dialogs;
        _reports = reports;
    }

    public ObservableCollection<FailureRowViewModel> Rows { get; } = new();

    [ObservableProperty] private int _count;
    [ObservableProperty] private long _missingBytes;
    [ObservableProperty] private string _byReason = string.Empty;
    [ObservableProperty] private FailureRowViewModel? _selected;

    public bool HasFailures => Count > 0;

    /// <summary>Follows a live or finished job.</summary>
    public void Track(QueuedJob? job)
    {
        _job = job;
        _result = job?.Result;
        Update();
    }

    /// <summary>Shows a finished result (the history page has no queued job any more).</summary>
    public void Show(TransferResult result)
    {
        _result = result;
        _job = null;
        Update();
    }

    /// <summary>Re-reads the job and refreshes the rows; called on every progress tick.</summary>
    public void Update()
    {
        var summary = BuildSummary();

        // Rebuild only when the count changed: the list is stable while a job runs, and
        // replacing it on every tick would fight the user's scrolling and selection.
        if (summary.Count != Rows.Count)
        {
            var selectedPath = Selected?.SourcePath;

            Rows.Clear();
            foreach (var failure in summary.Failures)
                Rows.Add(new FailureRowViewModel(failure, _localization));

            Selected = Rows.FirstOrDefault(r =>
                string.Equals(r.SourcePath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }

        Count = summary.Count;
        MissingBytes = summary.FailedBytes;
        ByReason = string.Join(
            " · ",
            summary.ByReason.Select(g => $"{g.Count} × {FailureText.What(g.Reason, _localization.Culture)}"));

        OnPropertyChanged(nameof(HasFailures));
    }

    private FailureSummary BuildSummary()
    {
        if (_result is { } result)
            return FailureSummary.FromItems(result.Items, result.Job.Scan?.Problems);

        // Running job: the scan carries the live outcome of every planned file.
        if (_job is { } job)
        {
            _result = job.Result;
            return FailureSummary.FromJob(job.Job);
        }

        return FailureSummary.Empty;
    }

    /// <summary>A plain-text list, for pasting into a message or a ticket.</summary>
    public string BuildClipboardText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_localization["Fail_Title"]);
        sb.AppendLine(_localization.Format("Fail_Count", Count));
        sb.AppendLine();

        var index = 1;
        foreach (var row in Rows)
        {
            sb.AppendLine($"{index++}. {row.FileName}");
            sb.AppendLine($"   {_localization["Fail_Source"]}: {row.SourcePath}");
            sb.AppendLine($"   {_localization["Fail_Destination"]}: {row.DestinationPath}");
            sb.AppendLine($"   {_localization["Fail_Reason"]}: {row.Reason} [{row.Code}]");
            sb.AppendLine($"   {_localization["Fail_Action"]}: {row.Action}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [RelayCommand]
    private void CopyList()
    {
        if (Count == 0) return;

        if (_dialogs.CopyText(BuildClipboardText()))
            _dialogs.Info(_localization["Fail_Copied"]);
    }

    [RelayCommand]
    private async Task OpenReportAsync()
    {
        var result = _result ?? _job?.Result;

        // Mid-run there is no finished result yet, so the launcher builds one from the
        // job as it stands; the report is then a snapshot, which is what the user wants.
        var path = result is not null
            ? await _reports.OpenAsync(result)
            : _job is not null
                ? await _reports.OpenAsync(_job)
                : null;

        if (path is null) _dialogs.Error(_localization["Fail_ReportFailed"]);
    }

    [RelayCommand]
    private void OpenFolder(FailureRowViewModel? row)
    {
        row ??= Selected;
        if (row is null)
        {
            _dialogs.Info(_localization["Fail_SelectRow"]);
            return;
        }

        // Select the file itself when it is still there, otherwise just its folder.
        _dialogs.OpenInExplorer(File.Exists(row.SourcePath) ? row.SourcePath : row.SourceFolder ?? row.SourcePath);
    }

    public string FormatMissing() => ByteFormatter.FormatSize(MissingBytes, CultureInfo.CurrentCulture);
}
