using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Services;

/// <summary>Writes a per-job report listing every file and its outcome.</summary>
public sealed class ReportService : IReportService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<string> SaveAsync(
        TransferResult result,
        ReportFormat format,
        string directory,
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var name = PathUtils.SanitizeFileName(
            string.IsNullOrWhiteSpace(result.Job.Name) ? result.Job.Id : result.Job.Name);

        var extension = format switch
        {
            ReportFormat.Json => "json",
            ReportFormat.Csv => "csv",
            ReportFormat.Html => "html",
            _ => "txt"
        };

        var path = Path.Combine(directory, $"FutureSpeed-{name}-{stamp}.{extension}");

        var content = format switch
        {
            ReportFormat.Json => BuildJson(result),
            ReportFormat.Csv => BuildCsv(result),
            ReportFormat.Html => ReportHtmlBuilder.Build(result, culture ?? CultureInfo.CurrentUICulture),
            _ => BuildText(result, culture ?? CultureInfo.CurrentUICulture)
        };

        // UTF-8 with BOM so Excel opens the CSV with Arabic paths intact.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ReportFormat.Csv);
        await File.WriteAllTextAsync(path, content, encoding, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string BuildJson(TransferResult result)
    {
        var summary = FailureSummary.FromItems(result.Items, result.Job.Scan?.Problems);

        var payload = new
        {
            product = "Future Speed File Transfer",
            jobId = result.Job.Id,
            jobName = result.Job.Name,
            mode = result.Job.Mode.ToString(),
            destination = result.Job.DestinationRoot,
            sources = result.Job.Sources,
            state = result.FinalState.ToString(),
            startedAt = result.Job.StartedAt,
            finishedAt = result.Job.FinishedAt,
            durationSeconds = Math.Round(result.Duration.TotalSeconds, 2),
            totals = new
            {
                files = result.Items.Count,
                copied = result.CopiedFiles,
                skipped = result.SkippedFiles,
                failed = result.FailedFiles,
                transferredBytes = result.TransferredBytes,
                averageBytesPerSecond = result.Duration.TotalSeconds > 0
                    ? (long)(result.TransferredBytes / result.Duration.TotalSeconds)
                    : 0
            },
            verification = new
            {
                enabled = result.Job.Options.VerifyWithSha256,
                verified = result.Items.Count(i => i.SourceHash is not null && i.DestinationHash is not null),
                mismatches = result.Items.Count(i => i.Outcome == FileOutcome.VerificationFailed)
            },
            // Listed on its own so a script can read "what did not transfer" without
            // filtering the whole file list.
            failures = summary.Failures.Select(f => new
            {
                file = f.FileName,
                source = f.SourcePath,
                destination = f.DestinationPath,
                relativePath = f.RelativePath,
                size = f.Size,
                reason = f.Reason.ToString(),
                reasonText = FailureText.What(f.Reason, CultureInfo.InvariantCulture),
                suggestedAction = FailureText.Fix(f.Reason, CultureInfo.InvariantCulture),
                nativeErrorCode = f.NativeErrorCode,
                origin = f.Origin.ToString(),
                attempts = f.Attempts,
                failedAt = f.FailedAt,
                message = f.Message
            }),
            failureCountsByReason = summary.ByReason.Select(g => new { reason = g.Reason.ToString(), count = g.Count }),
            notTransferredBytes = summary.FailedBytes,
            files = result.Items.Select(i => new
            {
                relativePath = i.RelativePath,
                source = i.SourcePath,
                destination = i.EffectiveDestinationPath ?? i.DestinationPath,
                size = i.Size,
                outcome = i.Outcome.ToString(),
                reason = i.Reason == FailureReason.None ? null : i.Reason.ToString(),
                nativeErrorCode = i.NativeErrorCode,
                attempts = i.Attempts == 0 ? (int?)null : i.Attempts,
                error = i.Error,
                sha256Source = i.SourceHash,
                sha256Destination = i.DestinationHash
            }),
            scanProblems = result.Job.Scan?.Problems.Select(p => new { path = p.Path, reason = p.Reason, kind = p.Kind.ToString() })
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static string BuildCsv(TransferResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RelativePath,Source,Destination,SizeBytes,Outcome,Reason,ReasonText,NativeErrorCode,Attempts,SuggestedAction,Error,Sha256Source,Sha256Destination");

        foreach (var item in result.Items)
        {
            sb.Append(Escape(item.RelativePath)).Append(',');
            sb.Append(Escape(item.SourcePath)).Append(',');
            sb.Append(Escape(item.EffectiveDestinationPath ?? item.DestinationPath)).Append(',');
            sb.Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Escape(item.Outcome.ToString())).Append(',');
            sb.Append(Escape(item.Reason == FailureReason.None ? string.Empty : item.Reason.ToString())).Append(',');
            sb.Append(Escape(item.Reason == FailureReason.None
                ? string.Empty
                : FailureText.What(item.Reason, CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Escape(item.NativeErrorCode?.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Escape(item.Attempts == 0 ? string.Empty : item.Attempts.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Escape(item.HasFailed
                ? FailureText.Fix(item.Reason, CultureInfo.InvariantCulture)
                : string.Empty)).Append(',');
            sb.Append(Escape(item.Error)).Append(',');
            sb.Append(Escape(item.SourceHash)).Append(',');
            sb.Append(Escape(item.DestinationHash)).AppendLine();
        }

        return sb.ToString();

        static string Escape(string? value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
            var escaped = value.Replace("\"", "\"\"");
            return needsQuotes ? $"\"{escaped}\"" : escaped;
        }
    }

    /// <summary>
    /// The plain-text report. It leads with what did not transfer, because that is the part a
    /// user needs: full source path, intended destination, cause, and what to do about it.
    /// </summary>
    private static string BuildText(TransferResult result, CultureInfo culture)
    {
        var arabic = culture.TwoLetterISOLanguageName.Equals("ar", StringComparison.OrdinalIgnoreCase);
        var summary = FailureSummary.FromItems(result.Items, result.Job.Scan?.Problems);
        var average = result.Duration.TotalSeconds > 0 ? result.TransferredBytes / result.Duration.TotalSeconds : 0;

        var sb = new StringBuilder();
        sb.AppendLine("Future Speed File Transfer - transfer report");
        sb.AppendLine(new string('=', 78));
        sb.AppendLine($"Job          : {result.Job.Name} ({result.Job.Id})");
        sb.AppendLine($"Mode         : {result.Job.Mode}");
        sb.AppendLine($"Destination  : {result.Job.DestinationRoot}");
        sb.AppendLine($"Sources      : {string.Join("; ", result.Job.Sources)}");
        sb.AppendLine($"State        : {result.FinalState}");
        sb.AppendLine($"Started      : {result.Job.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Finished     : {result.Job.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration     : {ByteFormatter.FormatDuration(result.Duration, culture)}");
        sb.AppendLine($"Transferred  : {ByteFormatter.FormatSize(result.TransferredBytes, culture)}");
        sb.AppendLine($"Not moved    : {ByteFormatter.FormatSize(summary.FailedBytes, culture)}");
        sb.AppendLine($"Average speed: {ByteFormatter.FormatSpeed(average, culture)}");
        sb.AppendLine($"Files        : {result.Items.Count} total / {result.CopiedFiles} done / {result.SkippedFiles} skipped / {summary.Count} failed");
        sb.AppendLine($"Verification : {(result.Job.Options.VerifyWithSha256 ? "SHA-256 enabled" : "disabled")}");
        sb.AppendLine();

        // ---- failures first -----------------------------------------------------
        if (summary.HasFailures)
        {
            sb.AppendLine(arabic ? "ملفات لم تُنقل" : "FILES THAT DID NOT TRANSFER");
            sb.AppendLine(new string('=', 78));

            foreach (var (reason, count) in summary.ByReason)
                sb.AppendLine($"  {count,5} x {FailureText.What(reason, culture)}");

            sb.AppendLine();

            var index = 1;
            foreach (var failure in summary.Failures)
            {
                sb.AppendLine($"[{index++}] {failure.FileName}  ({ByteFormatter.FormatSize(failure.Size, culture)})");
                sb.AppendLine($"     {(arabic ? "المصدر   " : "source   ")}: {failure.SourcePath}");
                sb.AppendLine($"     {(arabic ? "الوجهة   " : "target   ")}: {failure.DestinationPath ?? "-"}");
                sb.AppendLine($"     {(arabic ? "السبب    " : "reason   ")}: {FailureText.What(failure.Reason, culture)} [{failure.Code}]");
                if (!string.IsNullOrWhiteSpace(failure.Message))
                    sb.AppendLine($"     {(arabic ? "الرسالة  " : "message  ")}: {failure.Message}");
                sb.AppendLine($"     {(arabic ? "الإجراء  " : "action   ")}: {FailureText.Fix(failure.Reason, culture)}");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine(arabic
                ? "لم يفشل أي ملف في هذه العملية."
                : "No file failed in this transfer.");
            sb.AppendLine();
        }

        // ---- skipped ------------------------------------------------------------
        var skipped = result.Items.Where(i => i.Outcome == FileOutcome.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine(arabic ? "ملفات تم تخطيها" : "SKIPPED FILES");
            sb.AppendLine(new string('-', 78));
            foreach (var item in skipped)
                sb.AppendLine($"  {item.SourcePath}");
            sb.AppendLine();
        }

        // ---- full list ----------------------------------------------------------
        sb.AppendLine(arabic ? "كل الملفات" : "ALL FILES");
        sb.AppendLine(new string('-', 78));
        foreach (var item in result.Items)
        {
            sb.Append("  [").Append(item.Outcome).Append("] ")
              .Append(item.SourcePath)
              .Append("  (")
              .Append(ByteFormatter.FormatSize(item.Size, culture))
              .Append(')');

            if (item.Reason != FailureReason.None)
                sb.Append("  <").Append(item.ReasonCode).Append('>');

            if (!string.IsNullOrEmpty(item.DestinationHash))
                sb.Append("  sha256=").Append(item.DestinationHash);

            sb.AppendLine();
        }

        sb.AppendLine();

        if (result.Job.Scan?.Problems.Count > 0)
        {
            sb.AppendLine(arabic ? "مشاكل أثناء فحص المصدر" : "SCAN PROBLEMS");
            sb.AppendLine(new string('-', 78));
            foreach (var problem in result.Job.Scan.Problems)
                sb.AppendLine($"  {problem.Path} :: [{problem.Kind}] {problem.Reason}");
        }

        return sb.ToString();
    }
}
