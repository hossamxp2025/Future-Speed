using FutureSpeed.Core.Integration;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class InstanceNamingTests
{
    [Fact]
    public void The_names_are_stable_across_calls()
    {
        // They must also be stable across processes, which rules out string.GetHashCode().
        Assert.Equal(InstanceNaming.MutexName("hossam"), InstanceNaming.MutexName("hossam"));
        Assert.Equal(InstanceNaming.PipeName("hossam"), InstanceNaming.PipeName("hossam"));
    }

    [Fact]
    public void The_names_are_derived_from_the_user_name()
    {
        Assert.Equal("FutureSpeed.singleinstance.v1.hossam", InstanceNaming.MutexName("hossam"));
        Assert.Equal("FutureSpeed.ipc.v1.hossam", InstanceNaming.PipeName("hossam"));
    }

    [Fact]
    public void Two_users_never_share_a_channel()
        => Assert.NotEqual(InstanceNaming.PipeName("hossam"), InstanceNaming.PipeName("other"));

    [Theory]
    [InlineData("حسام", "حسام")]   // Arabic letters are letters: Windows object names are Unicode
    [InlineData("domain\\user name", "domainusername")]
    [InlineData("", "user")]
    [InlineData(null, "user")]
    public void Unusual_user_names_are_sanitised(string? user, string expected)
        => Assert.Equal(expected, InstanceNaming.Sanitize(user));

    [Fact]
    public void The_pipe_and_mutex_names_differ()
        => Assert.NotEqual(InstanceNaming.PipeName("x"), InstanceNaming.MutexName("x"));
}