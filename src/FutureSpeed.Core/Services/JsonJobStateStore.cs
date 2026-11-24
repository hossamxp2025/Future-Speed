using System.Text.Json;
using System.Text.Json.Serialization;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Checkpoints on disk, one JSON file per job. Writes go to a temporary file first and
/// are then renamed, so a crash in the middle of a save cannot corrupt the state that
/// the next launch needs in order to offer a resume.
/// </summary>
public sealed class JsonJobStateStore : IJobStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly ILogger<JsonJobStateStore> _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public JsonJobStateStore(string directory, ILogger<JsonJobStateStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _logger = logger ?? NullLogger<JsonJobStateStore>.Instance;
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string jobId)
        => Path.Combine(_directory, PathUtils.SanitizeFileName(jobId) + ".job.json");

    public async Task SaveAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = PathFor(checkpoint.JobId);
            var temp = target + ".tmp";

            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, checkpoint, SerializerOptions, cancellationToken).ConfigureAwait(false);

            File.Move(temp, target, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save checkpoint for job {JobId}", checkpoint.JobId);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<JobCheckpoint?> LoadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(jobId);
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<JobCheckpoint>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Corrupt checkpoint ignored: {Path}", path);
            return null;
        }
    }

    public async Task<IReadOnlyList<JobCheckpoint>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<JobCheckpoint>();
        if (!Directory.Exists(_directory)) return list;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.job.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                var checkpoint = await JsonSerializer.DeserializeAsync<JobCheckpoint>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (checkpoint is not null) list.Add(checkpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Corrupt checkpoint ignored: {Path}", file);
            }
        }

        return list.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    public Task DeleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(jobId);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete checkpoint for job {JobId}", jobId);
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<JobCheckpoint>> LoadResumableAsync(CancellationToken cancellationToken = default)
    {
        var all = await LoadAllAsync(cancellationToken).ConfigureAwait(false);
        return all
            .Where(c => c.State is JobState.Running or JobState.Paused or JobState.Queued or JobState.CompletedWithErrors)
            .ToList();
    }
}
