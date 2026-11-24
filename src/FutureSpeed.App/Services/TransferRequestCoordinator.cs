using System.IO;
using FutureSpeed.App.Localization;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Transfer;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

public sealed record SubmitResult(QueuedJob? Job, string? Error, bool Informational = false)
{
    public bool Succeeded => Job is not null;
}

public interface ITransferRequestCoordinator
{
    /// <summary>Turns an Explorer request into a queued job, or explains why it cannot.</summary>
    SubmitResult Submit(TransferRequest request);

    /// <summary>Builds a request from the clipboard's file list for "paste into this folder".</summary>
    SubmitResult PasteInto(string destination);
}

/// <summary>
/// The single place where an outside request (Explorer verb, command line, IPC) becomes
/// a job. Every safety rule lives here, so no entry point can bypass them.
/// </summary>
public sealed class TransferRequestCoordinator : ITransferRequestCoordinator
{
    private readonly ITransferQueue _queue;
    private readonly ISettingsService _settings;
    private readonly IClipboardFileReader _clipboard;
    private readonly ILocalizationService _localization;
    private readonly ILogger<TransferRequestCoordinator> _logger;

    public TransferRequestCoordinator(
        ITransferQueue queue,
        ISettingsService settings,
        IClipboardFileReader clipboard,
        ILocalizationService localization,
        ILogger<TransferRequestCoordinator> logger)
    {
        _queue = queue;
        _settings = settings;
        _clipboard = clipboard;
        _localization = localization;
        _logger = logger;
    }

    public SubmitResult PasteInto(string destination)
    {
        var payload = _clipboard.Read();

        // A clipboard holding text or an image is none of our business.
        if (!payload.HasFiles)
        {
            // Not an error: the user pasted with a text or image clipboard, which this
            // app deliberately ignores.
            _logger.LogInformation("Paste requested but the clipboard holds no file list.");
            return new SubmitResult(null, _localization["Integration_ClipboardEmpty"], Informational: true);
        }

        return Submit(new TransferRequest
        {
            Mode = payload.Mode,
            Sources = payload.Files.ToList(),
            Destination = destination,
            Compact = true,
            FromClipboard = true
        });
    }

    public SubmitResult Submit(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var destination = PathUtils.Normalize(request.Destination);
        if (destination.Length == 0)
            return new SubmitResult(null, _localization["Transfer_NoDestination"]);

        var sources = request.Sources
            .Select(PathUtils.Normalize)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)       // one source can only appear once
            .Where(p => File.Exists(PathUtils.ToExtendedPath(p)) || Directory.Exists(PathUtils.ToExtendedPath(p)))
            .ToList();

        if (sources.Count == 0)
            return new SubmitResult(null, _localization["Transfer_NoSources"]);

        var problems = PathUtils.ValidateDestinationAgainstSources(destination, sources);
        if (problems.Count > 0)
        {
            _logger.LogWarning("Rejected an Explorer request: {Problems}", string.Join(" | ", problems));
            return new SubmitResult(null, _localization["Error_SameSourceAndDestination"]);
        }

        try
        {
            Directory.CreateDirectory(PathUtils.ToExtendedPath(destination));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Destination folder could not be created: {Destination}", destination);
            return new SubmitResult(null, ex.Message);
        }

        // An identical job that is still running means Explorer fired the verb twice;
        // reuse it instead of starting a second engine over the same files.
        var duplicate = _queue.Jobs.FirstOrDefault(j =>
            !j.IsFinished &&
            j.Job.Mode == request.Mode &&
            PathUtils.PathsEqual(j.Job.DestinationRoot, destination) &&
            j.Job.Sources.Count == sources.Count &&
            j.Job.Sources.All(s => sources.Contains(PathUtils.Normalize(s), StringComparer.OrdinalIgnoreCase)));

        if (duplicate is not null)
        {
            _logger.LogInformation("Reusing the running job {JobId} for a duplicate Explorer request.", duplicate.Job.Id);
            return new SubmitResult(duplicate, null);
        }

        var options = _settings.Current.ToTransferOptions();

        var job = new TransferJob
        {
            Name = BuildName(sources, destination, request.Mode),
            DestinationRoot = destination,
            Mode = request.Mode,
            Options = options
        };

        job.Sources.AddRange(sources);

        var queued = _queue.Enqueue(job);

        _logger.LogInformation(
            "Queued {Mode} of {Count} item(s) into {Destination} from an Explorer request.",
            request.Mode, sources.Count, destination);

        return new SubmitResult(queued, null);
    }

    private static string BuildName(IReadOnlyList<string> sources, string destination, TransferMode mode)
    {
        var first = Path.GetFileName(sources[0].TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(first)) first = sources[0];

        var extra = sources.Count > 1 ? $" +{sources.Count - 1}" : string.Empty;
        var arrow = mode == TransferMode.Move ? "⇥" : "→";

        return $"{first}{extra} {arrow} {Path.GetFileName(destination.TrimEnd('\\'))}";
    }
}
