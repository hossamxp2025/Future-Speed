using System.Globalization;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Utils;
using Xunit;

namespace FutureSpeed.Core.Tests.Integration;

/// <summary>
/// End to end: a file Windows will not let us read must come back as a named cause with its
/// full path, and the report the user opens must actually contain that path.
/// </summary>
public sealed class FailureReportingTests
{
    [Fact]
    public async Task A_file_locked_by_another_program_fails_with_its_path_and_cause()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("good.bin", 4096);
        var locked = workspace.CreateFile("locked.bin", 4096);

        // FileShare.None is exactly what an open document does to us.
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var engine = workspace.CreateEngine();
            var job = workspace.CreateJob(configure: o => o.ContinueOnError = true);

            var result = await engine.ExecuteAsync(job, null, PauseToken.None, CancellationToken.None);

            Assert.Equal(JobState.CompletedWithErrors, result.FinalState);
            Assert.Equal(1, result.FailedFiles);

            var failure = Assert.Single(FailureSummary.FromItems(result.Items, job.Scan?.Problems).Failures);

            Assert.Equal(locked, failure.SourcePath);
            Assert.Equal("locked.bin", failure.FileName);
            Assert.Equal(FailureReason.InUse, failure.Reason);
            Assert.Equal(32, failure.NativeErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(failure.DestinationPath));

            // The one that could be read still went through.
            Assert.True(File.Exists(workspace.DestinationPathOf("good.bin")));
        }
    }

    [Fact]
    public async Task A_locked_file_is_never_deleted_by_a_move()
    {
        using var workspace = new TestWorkspace();
        var locked = workspace.CreateFile("locked.bin", 2048);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var engine = workspace.CreateEngine();
            var result = await engine.ExecuteAsync(
                workspace.CreateJob(TransferMode.Move, o => o.ContinueOnError = true),
                null,
                PauseToken.None,
                CancellationToken.None);

            Assert.Equal(1, result.FailedFiles);
        }

        Assert.True(File.Exists(locked));
    }

    [Fact]
    public async Task The_html_report_names_the_file_the_cause_and_the_fix()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("ok.bin", 1024);
        var locked = workspace.CreateFile("locked.bin", 1024);

        string reportPath;

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var engine = workspace.CreateEngine();
            var result = await engine.ExecuteAsync(
                workspace.CreateJob(configure: o => o.ContinueOnError = true),
                null,
                PauseToken.None,
                CancellationToken.None);

            reportPath = await new ReportService().SaveAsync(
                result,
                ReportFormat.Html,
                Path.Combine(workspace.Root, "reports"),
                CultureInfo.GetCultureInfo("ar"));
        }

        Assert.EndsWith(".html", reportPath);

        var html = await File.ReadAllTextAsync(reportPath);

        Assert.Contains(locked, html);                                              // the full path
        // HTML-encoded: the guillemets in the Arabic wording come out as entities.
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode(FailureText.What(FailureReason.InUse, CultureInfo.GetCultureInfo("ar"))),
            html);
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode(FailureText.Fix(FailureReason.InUse, CultureInfo.GetCultureInfo("ar"))),
            html);
        Assert.Contains("InUse (32)", html);                                        // the technical code
        Assert.Contains("dir=\"rtl\"", html);                                       // Arabic report is RTL
        Assert.Contains("ملفات لم تُنقل", html);
    }

    [Fact]
    public async Task The_english_report_is_ltr_and_in_english()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateFile("ok.bin", 512);

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(workspace.CreateJob(), null, PauseToken.None, CancellationToken.None);

        var path = await new ReportService().SaveAsync(
            result, ReportFormat.Html, Path.Combine(workspace.Root, "reports"), CultureInfo.GetCultureInfo("en"));

        var html = await File.ReadAllTextAsync(path);

        Assert.Contains("dir=\"ltr\"", html);
        Assert.Contains("Transfer report", html);
        Assert.Contains("no failed file", html);
    }

    [Fact]
    public async Task The_text_and_csv_reports_carry_the_reason_and_the_full_path()
    {
        using var workspace = new TestWorkspace();
        var locked = workspace.CreateFile("locked.bin", 1024);

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var engine = workspace.CreateEngine();
            var result = await engine.ExecuteAsync(
                workspace.CreateJob(configure: o => o.ContinueOnError = true),
                null,
                PauseToken.None,
                CancellationToken.None);

            var reports = Path.Combine(workspace.Root, "reports");
            var service = new ReportService();

            var txt = await File.ReadAllTextAsync(
                await service.SaveAsync(result, ReportFormat.Txt, reports, CultureInfo.GetCultureInfo("en")));
            Assert.Contains(locked, txt);
            Assert.Contains("InUse (32)", txt);
            Assert.Contains("FILES THAT DID NOT TRANSFER", txt);

            var csv = await File.ReadAllTextAsync(
                await service.SaveAsync(result, ReportFormat.Csv, reports, CultureInfo.GetCultureInfo("en")));
            Assert.Contains("ReasonText", csv);
            Assert.Contains("InUse", csv);
            Assert.Contains(locked, csv);

            var json = await File.ReadAllTextAsync(
                await service.SaveAsync(result, ReportFormat.Json, reports, CultureInfo.GetCultureInfo("en")));
            using var document = System.Text.Json.JsonDocument.Parse(json);

            var failures = document.RootElement.GetProperty("failures");
            Assert.Equal(1, failures.GetArrayLength());

            var failure = failures[0];
            Assert.Equal(locked, failure.GetProperty("source").GetString());
            Assert.Equal("InUse", failure.GetProperty("reason").GetString());
            Assert.Equal(32, failure.GetProperty("nativeErrorCode").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(failure.GetProperty("suggestedAction").GetString()));
        }
    }

    [Fact]
    public async Task A_source_that_vanished_is_reported_as_not_found()
    {
        using var workspace = new TestWorkspace();
        var job = workspace.CreateJob();
        job.Sources.Clear();
        job.Sources.Add(Path.Combine(workspace.Source, "never-existed"));

        var engine = workspace.CreateEngine();
        var result = await engine.ExecuteAsync(job, null, PauseToken.None, CancellationToken.None);

        var problem = Assert.Single(job.Scan!.Problems);
        Assert.Equal(FailureReason.NotFound, problem.Kind);

        var failure = Assert.Single(FailureSummary.FromItems(result.Items, job.Scan.Problems).Failures);
        Assert.Equal(FailureOrigin.Scan, failure.Origin);
        Assert.Contains("never-existed", failure.SourcePath);
    }
}
