using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Services;

/// <summary>
/// A resolver for headless runs (scheduled jobs, sync, tests): answers from a fixed
/// policy instead of asking a human. <see cref="ConflictPolicy.Ask"/> degrades to Skip,
/// because an unattended run must never silently overwrite.
/// </summary>
public sealed class PolicyConflictResolver : IConflictResolver
{
    private readonly ConflictPolicy _policy;

    public PolicyConflictResolver(ConflictPolicy policy = ConflictPolicy.Skip)
        => _policy = policy == ConflictPolicy.Ask ? ConflictPolicy.Skip : policy;

    public Task<ConflictDecision> ResolveAsync(ConflictInfo conflict, CancellationToken cancellationToken = default)
    {
        var action = ConflictStrategy.Decide(_policy, conflict) ?? ConflictAction.Skip;
        return Task.FromResult(new ConflictDecision(action, ApplyToAll: true));
    }
}
