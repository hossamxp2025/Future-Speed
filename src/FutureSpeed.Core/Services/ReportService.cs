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
            _ => "txt"
        };

        var path = Path.Combine(directory, $"FutureSpeed-{name}-{stamp}.{extension}");

        var content = format switch
        {
            ReportFormat.Json => BuildJson(result),
            ReportFormat.Csv => BuildCsv(result),
            _ => BuildText(result)
        };

        // UTF-8 with BOM so Excel opens the CSV with Arabic paths intact.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == ReportFormat.Csv);
        await File.WriteAllTextAsync(path, content, encoding, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string BuildJson(TransferResult result)
    {
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
            files = result.Items.Select(i => new
            {
                relativePath = i.RelativePath,
                source = i.SourcePath,
                destination = i.EffectiveDestinationPath ?? i.DestinationPath,
                size = i.Size,
                outcome = i.Outcome.ToString(),
                error = i.Error,
                sha256Source = i.SourceHash,
                sha256Destination = i.DestinationHash
            }),
            scanProblems = result.Job.Scan?.Problems.Select(p => new { path = p.Path, reason = p.Reason })
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    private static string BuildCsv(TransferResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RelativePath,Source,Destination,SizeBytes,Outcome,Error,Sha256Source,Sha256Destination");

        foreach (var item in result.Items)
        {
            sb.Append(Escape(item.RelativePath)).Append(',');
            sb.Append(Escape(item.SourcePath)).Append(',');
            sb.Append(Escape(item.EffectiveDestinationPath ?? item.DestinationPath)).Append(',');
            sb.Append(item.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Escape(item.Outcome.ToString())).Append(',');
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

    private static string BuildText(TransferResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Future Speed File Transfer - transfer report");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Job          : {result.Job.Name} ({result.Job.Id})");
        sb.AppendLine($"Mode         : {result.Job.Mode}");
        sb.AppendLine($"Destination  : {result.Job.DestinationRoot}");
        sb.AppendLine($"Sources      : {string.Join("; ", result.Job.Sources)}");
        sb.AppendLine($"State        : {result.FinalState}");
        sb.AppendLine($"Started      : {result.Job.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Finished     : {result.Job.FinishedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Duration     : {ByteFormatter.FormatDuration(result.Duration, CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Transferred  : {ByteFormatter.FormatSize(result.TransferredBytes, CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Average speed: {ByteFormatter.FormatSpeed(result.Duration.TotalSeconds > 0 ? result.TransferredBytes / result.Duration.TotalSeconds : 0, CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Files        : {result.Items.Count} total / {result.CopiedFiles} done / {result.SkippedFiles} skipped / {result.FailedFiles} failed");
        sb.AppendLine($"Verification : {(result.Job.Options.VerifyWithSha256 ? "SHA-256 enabled" : "disabled")}");
        sb.AppendLine();

        sb.AppendLine("Files");
        sb.AppendLine(new string('-', 60));
        foreach (var item in result.Items)
        {
            sb.Append("  [").Append(item.Outcome).Append("] ")
              .Append(item.RelativePath)
              .Append("  (")
              .Append(ByteFormatter.FormatSize(item.Size, CultureInfo.InvariantCulture))
              .Append(')');

            if (!string.IsNullOrEmpty(item.DestinationHash))
                sb.Append("  sha256=").Append(item.DestinationHash);

            sb.AppendLine();
        }

        sb.AppendLine();

        var failures = result.Items.Where(i => i.Outcome is FileOutcome.Failed or FileOutcome.VerificationFailed).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("Failed files");
            sb.AppendLine(new string('-', 60));
            foreach (var item in failures)
                sb.AppendLine($"  [{item.Outcome}] {item.RelativePath} :: {item.Error}");
            sb.AppendLine();
        }

        var skipped = result.Items.Where(i => i.Outcome == FileOutcome.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine("Skipped files");
            sb.AppendLine(new string('-', 60));
            foreach (var item in skipped)
                sb.AppendLine($"  {item.RelativePath}");
            sb.AppendLine();
        }

        if (result.Job.Scan?.Problems.Count > 0)
        {
            sb.AppendLine("Scan problems");
            sb.AppendLine(new string('-', 60));
            foreach (var problem in result.Job.Scan.Problems)
                sb.AppendLine($"  {problem.Path} :: {problem.Reason}");
        }

        return sb.ToString();
    }
}
