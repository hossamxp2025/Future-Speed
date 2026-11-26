using System.ComponentModel;
using System.Globalization;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Unit;

/// <summary>
/// A failed file is useless to the user as "an error". These tests guard the translation
/// from an OS exception to a cause the UI can name and act on.
/// </summary>
public sealed class FailureClassifierTests
{
    [Theory]
    [InlineData(32, FailureReason.InUse)]            // ERROR_SHARING_VIOLATION
    [InlineData(33, FailureReason.InUse)]            // ERROR_LOCK_VIOLATION
    [InlineData(5, FailureReason.AccessDenied)]
    [InlineData(2, FailureReason.NotFound)]
    [InlineData(3, FailureReason.NotFound)]
    [InlineData(112, FailureReason.DiskFull)]
    [InlineData(39, FailureReason.DiskFull)]
    [InlineData(19, FailureReason.ReadOnlyTarget)]
    [InlineData(206, FailureReason.PathTooLong)]
    [InlineData(123, FailureReason.InvalidName)]
    [InlineData(21, FailureReason.DestinationUnavailable)]
    [InlineData(67, FailureReason.NetworkError)]
    [InlineData(23, FailureReason.DeviceError)]
    public void A_win32_code_maps_to_its_cause(int code, FailureReason expected)
        => Assert.Equal(expected, FailureClassifier.FromNativeCode(code));

    [Fact]
    public void An_unknown_code_is_not_guessed()
        => Assert.Equal(FailureReason.Unknown, FailureClassifier.FromNativeCode(0x4242));

    [Fact]
    public void The_win32_code_is_read_out_of_the_hresult()
    {
        // This is exactly the shape Windows gives for a file held open by another program.
        var exception = new IOException("The process cannot access the file", unchecked((int)0x80070020));

        var diagnosis = FailureClassifier.Classify(exception);

        Assert.Equal(FailureReason.InUse, diagnosis.Reason);
        Assert.Equal(32, diagnosis.NativeCode);
        Assert.Equal("InUse (32)", diagnosis.Code);
    }

    [Fact]
    public void A_win32_exception_is_read_directly()
        => Assert.Equal(FailureReason.AccessDenied, FailureClassifier.Classify(new Win32Exception(5)).Reason);

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException), FailureReason.AccessDenied)]
    [InlineData(typeof(PathTooLongException), FailureReason.PathTooLong)]
    [InlineData(typeof(FileNotFoundException), FailureReason.NotFound)]
    [InlineData(typeof(DirectoryNotFoundException), FailureReason.NotFound)]
    public void The_exception_type_is_the_fallback(Type type, FailureReason expected)
    {
        var exception = (Exception)Activator.CreateInstance(type)!;
        Assert.Equal(expected, FailureClassifier.Classify(exception).Reason);
    }

    [Fact]
    public void A_single_inner_exception_is_unwrapped()
    {
        var aggregate = new AggregateException(new UnauthorizedAccessException());
        Assert.Equal(FailureReason.AccessDenied, FailureClassifier.Classify(aggregate).Reason);
    }

    [Fact]
    public void Nothing_classifies_as_unknown_rather_than_throwing()
        => Assert.Equal(FailureReason.Unknown, FailureClassifier.Classify(null).Reason);

    [Theory]
    [InlineData("ar")]
    [InlineData("en")]
    public void Every_reason_has_a_sentence_and_a_fix_in_both_languages(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);

        foreach (var reason in Enum.GetValues<FailureReason>())
        {
            if (reason == FailureReason.None) continue;

            Assert.False(string.IsNullOrWhiteSpace(FailureText.What(reason, culture)), reason.ToString());
            Assert.False(string.IsNullOrWhiteSpace(FailureText.Fix(reason, culture)), reason.ToString());
        }
    }

    [Fact]
    public void The_two_languages_do_not_return_the_same_text()
    {
        var arabic = FailureText.What(FailureReason.InUse, CultureInfo.GetCultureInfo("ar"));
        var english = FailureText.What(FailureReason.InUse, CultureInfo.GetCultureInfo("en"));

        Assert.NotEqual(arabic, english);
    }

    [Fact]
    public void A_locked_file_is_worth_retrying_but_a_bad_name_is_not()
    {
        Assert.True(FailureText.IsRetryable(FailureReason.InUse));
        Assert.False(FailureText.IsRetryable(FailureReason.InvalidName));
    }
}

/// <summary>The grouped view the UI and the report are built on.</summary>
public sealed class FailureSummaryTests
{
    private static TransferItem Item(string name, FileOutcome outcome, FailureReason reason, long size = 100)
        => new()
        {
            SourcePath = @"D:\src\" + name,
            DestinationPath = @"E:\dst\" + name,
            RelativePath = name,
            Size = size,
            Outcome = outcome,
            Reason = reason,
            NativeErrorCode = reason == FailureReason.InUse ? 32 : null,
            Error = "raw os message"
        };

    [Fact]
    public void Only_failed_files_become_failures()
    {
        var summary = FailureSummary.FromItems(new[]
        {
            Item("ok.bin", FileOutcome.Copied, FailureReason.None),
            Item("locked.bin", FileOutcome.Failed, FailureReason.InUse, 500),
            Item("bad-hash.bin", FileOutcome.VerificationFailed, FailureReason.VerificationMismatch, 300),
            Item("skipped.bin", FileOutcome.Skipped, FailureReason.None)
        });

        Assert.Equal(2, summary.Count);
        Assert.Equal(1, summary.SkippedFiles);
        Assert.Equal(800, summary.FailedBytes);
    }

    [Fact]
    public void A_failure_carries_the_full_source_path_and_the_code()
    {
        var summary = FailureSummary.FromItems(new[] { Item("locked.bin", FileOutcome.Failed, FailureReason.InUse) });
        var failure = Assert.Single(summary.Failures);

        Assert.Equal(@"D:\src\locked.bin", failure.SourcePath);
        Assert.Equal(@"E:\dst\locked.bin", failure.DestinationPath);
        Assert.Equal("locked.bin", failure.FileName);
        Assert.Equal(@"D:\src", failure.SourceFolder);
        Assert.Equal("InUse (32)", failure.Code);
        Assert.Equal(FailureOrigin.Copy, failure.Origin);
    }

    [Fact]
    public void A_verification_failure_says_where_it_happened()
    {
        var summary = FailureSummary.FromItems(
            new[] { Item("x.bin", FileOutcome.VerificationFailed, FailureReason.VerificationMismatch) });

        Assert.Equal(FailureOrigin.Verification, Assert.Single(summary.Failures).Origin);
    }

    [Fact]
    public void Unreadable_sources_found_during_the_scan_are_failures_too()
    {
        // Without this, a file that could not even be enumerated disappeared silently.
        var summary = FailureSummary.FromItems(
            Array.Empty<TransferItem>(),
            new[]
            {
                new ScanProblem(@"D:\src\secret", "denied", FailureReason.AccessDenied),
                new ScanProblem(@"D:\src\link", "Skipped link / reparse point.", FailureReason.None)
            });

        var failure = Assert.Single(summary.Failures);
        Assert.Equal(FailureOrigin.Scan, failure.Origin);
        Assert.Equal(FailureReason.AccessDenied, failure.Reason);
        Assert.Equal(@"D:\src\secret", failure.SourcePath);
    }

    [Fact]
    public void Causes_are_grouped_most_frequent_first()
    {
        var summary = FailureSummary.FromItems(new[]
        {
            Item("a", FileOutcome.Failed, FailureReason.InUse),
            Item("b", FileOutcome.Failed, FailureReason.InUse),
            Item("c", FileOutcome.Failed, FailureReason.AccessDenied)
        });

        Assert.Equal(FailureReason.InUse, summary.ByReason[0].Reason);
        Assert.Equal(2, summary.ByReason[0].Count);
        Assert.Equal(FailureReason.AccessDenied, summary.ByReason[1].Reason);
    }

    [Fact]
    public void A_failure_without_a_reason_is_reported_as_unknown_not_as_none()
    {
        var summary = FailureSummary.FromItems(new[] { Item("a", FileOutcome.Failed, FailureReason.None) });
        Assert.Equal(FailureReason.Unknown, Assert.Single(summary.Failures).Reason);
    }

    [Fact]
    public void A_clean_job_has_nothing_to_report()
    {
        var summary = FailureSummary.FromItems(new[] { Item("ok", FileOutcome.Copied, FailureReason.None) });

        Assert.False(summary.HasFailures);
        Assert.Equal(0, summary.FailedBytes);
    }
}
