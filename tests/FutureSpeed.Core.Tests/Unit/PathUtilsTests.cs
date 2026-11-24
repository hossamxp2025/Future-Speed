using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

public sealed class PathUtilsTests
{
    [Theory]
    [InlineData(@"C:\data\reports", @"C:\data\reports\q1.pdf", true)]
    [InlineData(@"C:\data\reports", @"C:\data\reports", true)]
    [InlineData(@"C:\data\reports", @"C:\data\reports-old\q1.pdf", false)]
    [InlineData(@"C:\data\reports", @"C:\data", false)]
    public void IsInside_detects_containment(string root, string candidate, bool expected)
        => Assert.Equal(expected, PathUtils.IsInside(root, candidate));

    [Fact]
    public void CombineSecure_builds_a_path_under_the_root()
    {
        var result = PathUtils.CombineSecure(@"C:\dest", @"docs\a.txt");
        Assert.Equal(@"C:\dest\docs\a.txt", result);
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"docs\..\..\outside.txt")]
    [InlineData(@"..\..\windows\system32\evil.dll")]
    public void CombineSecure_rejects_path_traversal(string relative)
        => Assert.Throws<UnauthorizedAccessException>(() => PathUtils.CombineSecure(@"C:\dest", relative));

    [Fact]
    public void CombineSecure_rejects_a_rooted_relative_path()
        => Assert.Throws<UnauthorizedAccessException>(() => PathUtils.CombineSecure(@"C:\dest", @"D:\elsewhere\a.txt"));

    [Fact]
    public void GetUniquePath_appends_a_counter_until_the_name_is_free()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\dest\report.pdf",
            @"C:\dest\report (2).pdf"
        };

        var unique = PathUtils.GetUniquePath(@"C:\dest\report.pdf", taken.Contains);

        Assert.Equal(@"C:\dest\report (3).pdf", unique);
    }

    [Fact]
    public void GetUniquePath_keeps_a_free_name_unchanged()
        => Assert.Equal(@"C:\dest\new.txt", PathUtils.GetUniquePath(@"C:\dest\new.txt", _ => false));

    [Fact]
    public void ToExtendedPath_prefixes_local_and_unc_paths()
    {
        Assert.Equal(@"\\?\C:\data\x.txt", PathUtils.ToExtendedPath(@"C:\data\x.txt"));
        Assert.Equal(@"\\?\UNC\server\share\x.txt", PathUtils.ToExtendedPath(@"\\server\share\x.txt"));
    }

    [Fact]
    public void ToExtendedPath_is_idempotent()
    {
        var once = PathUtils.ToExtendedPath(@"C:\data\x.txt");
        Assert.Equal(once, PathUtils.ToExtendedPath(once));
    }

    [Fact]
    public void FromExtendedPath_round_trips()
    {
        Assert.Equal(@"C:\data\x.txt", PathUtils.FromExtendedPath(@"\\?\C:\data\x.txt"));
        Assert.Equal(@"\\server\share\x.txt", PathUtils.FromExtendedPath(@"\\?\UNC\server\share\x.txt"));
    }

    [Fact]
    public void A_destination_equal_to_the_source_is_rejected()
    {
        var errors = PathUtils.ValidateDestinationAgainstSources(@"C:\data", new[] { @"C:\data" });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_destination_inside_the_source_is_rejected()
    {
        var errors = PathUtils.ValidateDestinationAgainstSources(@"C:\data\backup", new[] { @"C:\data" });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_sibling_destination_is_accepted()
    {
        var errors = PathUtils.ValidateDestinationAgainstSources(@"D:\backup", new[] { @"C:\data", @"C:\photos" });
        Assert.Empty(errors);
    }

    [Fact]
    public void SanitizeFileName_strips_invalid_characters()
    {
        var sanitized = PathUtils.SanitizeFileName("a:b*c?d.txt");
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('*', sanitized);
        Assert.DoesNotContain('?', sanitized);
    }
}
