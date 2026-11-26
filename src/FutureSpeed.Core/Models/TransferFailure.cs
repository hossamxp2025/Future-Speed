namespace FutureSpeed.Core.Models;

/// <summary>Where a failure was detected.</summary>
public enum FailureOrigin
{
    /// <summary>While copying or moving the file.</summary>
    Copy = 0,
    /// <summary>While verifying the copy with SHA-256.</summary>
    Verification = 1,
    /// <summary>While enumerating the sources, so the file never reached the copy phase.</summary>
    Scan = 2
}

/// <summary>
/// One file that did not make it, with everything needed to act on it: the full source path,
/// where it was meant to go, why it failed, and the technical code behind that reason.
/// </summary>
public sealed record TransferFailure(
    string SourcePath,
    string? DestinationPath,
    string RelativePath,
    long Size,
    FailureReason Reason,
    string? Message,
    int? NativeErrorCode,
    FailureOrigin Origin,
    int Attempts = 0,
    DateTimeOffset? FailedAt = null)
{
    public string FileName => Path.GetFileName(SourcePath);

    /// <summary>The folder holding the source, so the UI can open it.</summary>
    public string? SourceFolder
    {
        get
        {
            try { return Path.GetDirectoryName(SourcePath); }
            catch { return null; }
        }
    }

    /// <summary>"InUse (32)" — reason plus the OS code when there is one.</summary>
    public string Code => NativeErrorCode is { } code ? $"{Reason} ({code})" : Reason.ToString();
}

/// <summary>Every failure of one job, grouped so a report can lead with the cause.</summary>
public sealed class FailureSummary
{
    public static FailureSummary Empty { get; } = new(Array.Empty<TransferFailure>(), 0, 0);

    public FailureSummary(IReadOnlyList<TransferFailure> failures, int skippedFiles, long failedBytes)
    {
        Failures = failures;
        SkippedFiles = skippedFiles;
        FailedBytes = failedBytes;
    }

    public IReadOnlyList<TransferFailure> Failures { get; }

    /// <summary>Files deliberately skipped (conflict policy or the user) — not failures.</summary>
    public int SkippedFiles { get; }

    /// <summary>Total size of the files that failed, so the user knows what is missing.</summary>
    public long FailedBytes { get; }

    public int Count => Failures.Count;
    public bool HasFailures => Failures.Count > 0;

    /// <summary>Failures per cause, most frequent first.</summary>
    public IReadOnlyList<(FailureReason Reason, int Count)> ByReason => Failures
        .GroupBy(f => f.Reason)
        .Select(g => (g.Key, g.Count()))
        .OrderByDescending(g => g.Item2)
        .ToList();

    /// <summary>Builds the summary from a finished job, including problems found during the scan.</summary>
    public static FailureSummary FromItems(
        IEnumerable<TransferItem> items,
        IEnumerable<ScanProblem>? scanProblems = null)
    {
        var failures = new List<TransferFailure>();
        var skipped = 0;
        long failedBytes = 0;

        foreach (var item in items)
        {
            if (item.Outcome == FileOutcome.Skipped)
            {
                skipped++;
                continue;
            }

            if (!item.HasFailed) continue;

            failedBytes += item.Size;
            failures.Add(new TransferFailure(
                item.SourcePath,
                item.EffectiveDestinationPath ?? item.DestinationPath,
                item.RelativePath,
                item.Size,
                item.Reason == FailureReason.None ? FailureReason.Unknown : item.Reason,
                item.Error,
                item.NativeErrorCode,
                item.Outcome == FileOutcome.VerificationFailed ? FailureOrigin.Verification : FailureOrigin.Copy,
                item.Attempts,
                item.FailedAt));
        }

        if (scanProblems is not null)
        {
            foreach (var problem in scanProblems)
            {
                // A link deliberately not followed is a note, not a failure.
                if (problem.Kind == FailureReason.None) continue;

                failures.Add(new TransferFailure(
                    problem.Path,
                    null,
                    problem.Path,
                    0,
                    problem.Kind,
                    problem.Reason,
                    null,
                    FailureOrigin.Scan));
            }
        }

        return new FailureSummary(failures, skipped, failedBytes);
    }

    /// <summary>Convenience overload for a job whose scan is already attached.</summary>
    public static FailureSummary FromJob(TransferJob job) => job.Scan is null
        ? Empty
        : FromItems(job.Scan.Files, job.Scan.Problems);
}
