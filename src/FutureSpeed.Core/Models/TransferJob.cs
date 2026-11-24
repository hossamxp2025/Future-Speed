namespace FutureSpeed.Core.Models;

/// <summary>A unit of work: a set of sources copied or moved into one destination root.</summary>
public sealed class TransferJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<string> Sources { get; init; } = new();

    public string DestinationRoot { get; set; } = string.Empty;

    public TransferMode Mode { get; set; } = TransferMode.Copy;

    public TransferOptions Options { get; set; } = new();

    public JobState State { get; set; } = JobState.Created;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Lower number runs first in the queue.</summary>
    public int Priority { get; set; }

    /// <summary>When set, the queue will not start the job before this moment.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Scan outcome; populated by the engine before the copy phase.</summary>
    public ScanResult? Scan { get; set; }

    public string? FailureMessage { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
