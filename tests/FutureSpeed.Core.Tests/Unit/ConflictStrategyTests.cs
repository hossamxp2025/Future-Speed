using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class ConflictStrategyTests
{
    private static ConflictInfo Conflict(
        long sourceSize = 100,
        long destinationSize = 100,
        int sourceAgeSeconds = 0,
        int destinationAgeSeconds = 0)
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        return new ConflictInfo(
            @"C:\src\a.txt",
            @"C:\dst\a.txt",
            sourceSize,
            destinationSize,
            now.AddSeconds(-sourceAgeSeconds),
            now.AddSeconds(-destinationAgeSeconds));
    }

    [Fact]
    public void Ask_returns_no_decision_so_the_caller_must_ask()
        => Assert.Null(ConflictStrategy.Decide(ConflictPolicy.Ask, Conflict()));

    [Theory]
    [InlineData(ConflictPolicy.Overwrite, ConflictAction.Overwrite)]
    [InlineData(ConflictPolicy.Skip, ConflictAction.Skip)]
    [InlineData(ConflictPolicy.AutoRename, ConflictAction.AutoRename)]
    public void Fixed_policies_map_to_their_action(ConflictPolicy policy, ConflictAction expected)
        => Assert.Equal(expected, ConflictStrategy.Decide(policy, Conflict()));

    [Fact]
    public void KeepNewest_overwrites_when_the_source_is_newer()
    {
        var conflict = Conflict(sourceAgeSeconds: 0, destinationAgeSeconds: 3600);
        Assert.Equal(ConflictAction.Overwrite, ConflictStrategy.Decide(ConflictPolicy.KeepNewest, conflict));
    }

    [Fact]
    public void KeepNewest_skips_when_the_destination_is_newer()
    {
        var conflict = Conflict(sourceAgeSeconds: 3600, destinationAgeSeconds: 0);
        Assert.Equal(ConflictAction.Skip, ConflictStrategy.Decide(ConflictPolicy.KeepNewest, conflict));
    }

    [Fact]
    public void KeepNewest_tolerates_a_sub_two_second_difference()
    {
        // FAT32 stores write times with 2-second resolution; a 1-second delta is not a change.
        var conflict = Conflict(sourceAgeSeconds: 0, destinationAgeSeconds: 1);
        Assert.Equal(ConflictAction.Skip, ConflictStrategy.Decide(ConflictPolicy.KeepNewest, conflict));
    }

    [Fact]
    public void OverwriteIfDifferent_skips_identical_files()
    {
        var conflict = Conflict(sourceSize: 500, destinationSize: 500);
        Assert.Equal(ConflictAction.Skip, ConflictStrategy.Decide(ConflictPolicy.OverwriteIfDifferent, conflict));
    }

    [Fact]
    public void OverwriteIfDifferent_overwrites_on_a_size_difference()
    {
        var conflict = Conflict(sourceSize: 500, destinationSize: 400);
        Assert.Equal(ConflictAction.Overwrite, ConflictStrategy.Decide(ConflictPolicy.OverwriteIfDifferent, conflict));
    }

    [Fact]
    public void OverwriteIfDifferent_overwrites_on_a_date_difference()
    {
        var conflict = Conflict(sourceAgeSeconds: 0, destinationAgeSeconds: 600);
        Assert.Equal(ConflictAction.Overwrite, ConflictStrategy.Decide(ConflictPolicy.OverwriteIfDifferent, conflict));
    }

    [Fact]
    public void LooksIdentical_compares_size_and_time()
    {
        Assert.True(ConflictStrategy.LooksIdentical(Conflict(100, 100)));
        Assert.False(ConflictStrategy.LooksIdentical(Conflict(100, 101)));
        Assert.False(ConflictStrategy.LooksIdentical(Conflict(100, 100, destinationAgeSeconds: 30)));
    }

    [Theory]
    [InlineData(ConflictAction.Overwrite, ConflictPolicy.Overwrite)]
    [InlineData(ConflictAction.Skip, ConflictPolicy.Skip)]
    [InlineData(ConflictAction.AutoRename, ConflictPolicy.AutoRename)]
    [InlineData(ConflictAction.Cancel, ConflictPolicy.Ask)]
    public void An_answer_can_be_promoted_to_a_policy(ConflictAction action, ConflictPolicy expected)
        => Assert.Equal(expected, ConflictStrategy.ToPolicy(action));

    [Fact]
    public async Task The_headless_resolver_never_overwrites_when_asked_to_ask()
    {
        var resolver = new PolicyConflictResolver(ConflictPolicy.Ask);
        var decision = await resolver.ResolveAsync(Conflict());

        Assert.Equal(ConflictAction.Skip, decision.Action);
        Assert.True(decision.ApplyToAll);
    }
}
