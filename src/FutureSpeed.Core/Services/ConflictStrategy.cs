using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Pure decision logic for name clashes. Kept free of I/O so the rules are unit-tested
/// directly, and so the engine never has to guess what a policy means.
/// </summary>
public static class ConflictStrategy
{
    /// <summary>
    /// Maps a policy plus the facts about both files to an action.
    /// Returns null for <see cref="ConflictPolicy.Ask"/>: the caller must ask the user.
    /// </summary>
    public static ConflictAction? Decide(ConflictPolicy policy, ConflictInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return policy switch
        {
            ConflictPolicy.Ask => null,
            ConflictPolicy.Overwrite => ConflictAction.Overwrite,
            ConflictPolicy.Skip => ConflictAction.Skip,
            ConflictPolicy.AutoRename => ConflictAction.AutoRename,

            // Second-level precision: the file systems involved store times at different
            // resolutions (FAT32 rounds to 2 s), so a sub-second difference is not a change.
            ConflictPolicy.KeepNewest =>
                info.SourceLastWriteUtc > info.DestinationLastWriteUtc.AddSeconds(2)
                    ? ConflictAction.Overwrite
                    : ConflictAction.Skip,

            ConflictPolicy.OverwriteIfDifferent =>
                info.SourceSize != info.DestinationSize ||
                Math.Abs((info.SourceLastWriteUtc - info.DestinationLastWriteUtc).TotalSeconds) > 2
                    ? ConflictAction.Overwrite
                    : ConflictAction.Skip,

            _ => ConflictAction.Skip
        };
    }

    /// <summary>True when source and destination look identical (same size, same write time).</summary>
    public static bool LooksIdentical(ConflictInfo info)
        => info.SourceSize == info.DestinationSize &&
           Math.Abs((info.SourceLastWriteUtc - info.DestinationLastWriteUtc).TotalSeconds) <= 2;

    /// <summary>Converts a user's one-off answer into a policy that can be applied to all.</summary>
    public static ConflictPolicy ToPolicy(ConflictAction action) => action switch
    {
        ConflictAction.Overwrite => ConflictPolicy.Overwrite,
        ConflictAction.Skip => ConflictPolicy.Skip,
        ConflictAction.AutoRename => ConflictPolicy.AutoRename,
        _ => ConflictPolicy.Ask
    };
}
