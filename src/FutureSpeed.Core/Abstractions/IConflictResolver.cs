using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

public sealed record ConflictInfo(
    string SourcePath,
    string DestinationPath,
    long SourceSize,
    long DestinationSize,
    DateTime SourceLastWriteUtc,
    DateTime DestinationLastWriteUtc);

public enum ConflictAction
{
    Overwrite,
    Skip,
    AutoRename,
    Cancel
}

public sealed record ConflictDecision(ConflictAction Action, bool ApplyToAll);

/// <summary>
/// Resolves a name clash. The engine calls this only when the effective policy is
/// <see cref="ConflictPolicy.Ask"/>; the UI implementation shows a dialog, headless
/// implementations return a fixed answer.
/// </summary>
public interface IConflictResolver
{
    Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken = default);
}
