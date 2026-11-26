using System.Buffers;
using System.Collections.Concurrent;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Transfer;

/// <summary>
/// The copy/move engine. Phases: scan -> space check -> create folders -> copy files
/// (N workers) -> restore folder metadata. Nothing is written outside the destination
/// root, and in Move mode a source is deleted only after its copy succeeded (and, when
/// verification is on, only after the hashes matched).
/// </summary>
public sealed class TransferEngine : ITransferEngine
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(3);

    private const int ErrorDiskFull = 0x70;       // ERROR_DISK_FULL
    private const int ErrorHandleDiskFull = 0x27; // ERROR_HANDLE_DISK_FULL

    private readonly IFileSystemScanner _scanner;
    private readonly ISpaceValidator _spaceValidator;
    private readonly IDriveService _drives;
    private readonly IHashService _hashService;
    private readonly IConflictResolver _conflictResolver;
    private readonly IJobStateStore? _stateStore;
    private readonly ILogger<TransferEngine> _logger;

    public TransferEngine(
        IFileSystemScanner scanner,
        ISpaceValidator spaceValidator,
        IDriveService drives,
        IHashService hashService,
        IConflictResolver conflictResolver,
        IJobStateStore? stateStore = null,
        ILogger<TransferEngine>? logger = null)
    {
        _scanner = scanner;
        _spaceValidator = spaceValidator;
        _drives = drives;
        _hashService = hashService;
        _conflictResolver = conflictResolver;
        _stateStore = stateStore;
        _logger = logger ?? NullLogger<TransferEngine>.Instance;
    }

    public async Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
        => await ExecuteAsync(job, progress, pauseToken, new SkipSignal(), cancellationToken).ConfigureAwait(false);

    public async Task<TransferResult> ExecuteAsync(
        TransferJob job,
        IProgress<TransferProgress>? progress,
        PauseToken pauseToken,
        SkipSignal skipSignal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        skipSignal ??= new SkipSignal();

        var options = job.Options ?? new TransferOptions();
        var destinationRoot = PathUtils.Normalize(job.DestinationRoot);

        // ---- destination sanity -------------------------------------------------
        var destinationProblems = PathUtils.ValidateDestinationAgainstSources(destinationRoot, job.Sources);
        if (destinationProblems.Count > 0)
        {
            job.State = JobState.Failed;
            job.FailureMessage = string.Join(" | ", destinationProblems);
            throw new InvalidDestinationException(destinationProblems);
        }

        job.StartedAt = DateTimeOffset.Now;

        // ---- scan ---------------------------------------------------------------
        if (job.Scan is null)
        {
            job.State = JobState.Scanning;
            progress?.Report(new TransferProgress
            {
                JobId = job.Id,
                State = JobState.Scanning,
                PhaseLabel = "scan"
            });

            job.Scan = await _scanner.ScanAsync(
                job.Sources,
                destinationRoot,
                new Progress<string>(path => progress?.Report(new TransferProgress
                {
                    JobId = job.Id,
                    State = JobState.Scanning,
                    PhaseLabel = "scan",
                    CurrentFilePath = path
                })),
                cancellationToken).ConfigureAwait(false);
        }

        var scan = job.Scan;

        // ---- space check --------------------------------------------------------
        job.State = JobState.Validating;
        var validation = _spaceValidator.Validate(scan, destinationRoot, options);
        if (validation.Status is SpaceValidationStatus.Insufficient or SpaceValidationStatus.InvalidDestination)
        {
            job.State = JobState.Failed;
            job.FailureMessage = validation.Message ?? "Insufficient free space.";
            throw new InsufficientSpaceException(validation);
        }

        // ---- resume ------------------------------------------------------------
        var alreadyDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.EnableResume && _stateStore is not null)
        {
            var checkpoint = await _stateStore.LoadAsync(job.Id, cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null)
            {
                foreach (var done in checkpoint.CompletedRelativePaths)
                    alreadyDone.Add(done);
                _logger.LogInformation("Resuming job {JobId}: {Count} files already finished.", job.Id, alreadyDone.Count);
            }
        }

        // ---- folders ------------------------------------------------------------
        foreach (var directory in scan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(PathUtils.ToExtendedPath(directory.DestinationPath));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not create directory {Path}", directory.DestinationPath);
            }
        }

        // ---- tuning -------------------------------------------------------------
        var sourceKind = scan.Files.Count > 0 ? _drives.GetKindFor(scan.Files[0].SourcePath) : DriveKindHint.Unknown;
        var destinationKind = _drives.GetKindFor(destinationRoot);
        var averageSize = scan.Files.Count > 0 ? scan.TotalBytes / Math.Max(1, scan.Files.Count) : 0;
        var (bufferSize, workers) = TransferTuner.Resolve(options, averageSize, sourceKind, destinationKind);

        _logger.LogInformation(
            "Job {JobId}: {Files} files, {Bytes} bytes, {Workers} workers, {Buffer} byte buffer ({SourceKind} -> {DestKind}).",
            job.Id, scan.FileCount, scan.TotalBytes, workers, bufferSize, sourceKind, destinationKind);

        // ---- run ----------------------------------------------------------------
        var runtime = new JobRuntime(job, scan, options, bufferSize, pauseToken, skipSignal)
        {
            RateLimiter = new RateLimiter(options.MaxBytesPerSecond),
            SameVolume = _drives.AreOnSameVolume(
                scan.Files.Count > 0 ? scan.Files[0].SourcePath : destinationRoot,
                destinationRoot)
        };

        job.State = JobState.Running;
        runtime.Speed.Start();

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressTask = PublishProgressAsync(runtime, progress, progressCts.Token);
        var checkpointTask = options.EnableResume && _stateStore is not null
            ? PersistCheckpointsAsync(runtime, progressCts.Token)
            : Task.CompletedTask;

        var finalState = JobState.Completed;

        try
        {
            var pending = scan.Files
                .Where(f => !alreadyDone.Contains(f.RelativePath))
                .ToList();

            foreach (var skipped in scan.Files.Where(f => alreadyDone.Contains(f.RelativePath)))
            {
                skipped.Outcome = FileOutcome.Copied;
                Interlocked.Increment(ref runtime.CompletedFiles);
                Interlocked.Add(ref runtime.TransferredBytes, skipped.Size);
            }

            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = workers,
                    CancellationToken = cancellationToken
                },
                (item, ct) => ProcessFileAsync(runtime, item, ct)).ConfigureAwait(false);

            if (runtime.FailedFiles > 0)
                finalState = JobState.CompletedWithErrors;
        }
        catch (OperationCanceledException)
        {
            finalState = JobState.Canceled;
        }
        catch (TransferAbortedException ex)
        {
            _logger.LogError(ex, "Job {JobId} aborted.", job.Id);
            job.FailureMessage = ex.Message;
            finalState = JobState.Failed;
        }
        finally
        {
            runtime.Speed.Stop();
            progressCts.Cancel();
            await SafeAwait(progressTask).ConfigureAwait(false);
            await SafeAwait(checkpointTask).ConfigureAwait(false);
        }

        // ---- folder metadata ----------------------------------------------------
        if (finalState is JobState.Completed or JobState.CompletedWithErrors && options.PreserveTimestamps)
            RestoreDirectoryMetadata(scan, options);

        // ---- move: prune emptied source folders --------------------------------
        if (job.Mode == TransferMode.Move && finalState == JobState.Completed)
            PruneEmptySourceDirectories(job, scan);

        job.State = finalState;
        job.FinishedAt = DateTimeOffset.Now;

        if (_stateStore is not null)
        {
            if (finalState == JobState.Completed)
                await _stateStore.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
            else if (options.EnableResume)
                await SaveCheckpointAsync(runtime, CancellationToken.None).ConfigureAwait(false);
        }

        var result = new TransferResult(
            job,
            finalState,
            Interlocked.Read(ref runtime.TransferredBytes),
            runtime.CompletedFiles,
            runtime.SkippedFiles,
            runtime.FailedFiles,
            runtime.Speed.Elapsed,
            scan.Files);

        _logger.LogInformation(
            "Job {JobId} finished as {State}: {Copied} copied, {Skipped} skipped, {Failed} failed in {Duration}.",
            job.Id, finalState, result.CopiedFiles, result.SkippedFiles, result.FailedFiles, result.Duration);

        return result;
    }

    // =====================================================================
    // per-file pipeline
    // =====================================================================

    private async ValueTask ProcessFileAsync(JobRuntime runtime, TransferItem item, CancellationToken cancellationToken)
    {
        var options = runtime.Options;
        var attempts = Math.Max(0, options.RetryCount) + 1;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                await runtime.PauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                runtime.CurrentFile = item;

                var destination = item.DestinationPath;

                // ---- conflict ---------------------------------------------------
                if (File.Exists(PathUtils.ToExtendedPath(destination)))
                {
                    var action = await ResolveConflictAsync(runtime, item, destination, cancellationToken)
                        .ConfigureAwait(false);

                    switch (action)
                    {
                        case ConflictAction.Skip:
                            item.Outcome = FileOutcome.Skipped;
                            Interlocked.Increment(ref runtime.SkippedFiles);
                            runtime.MarkCompleted(item.RelativePath);
                            return;

                        case ConflictAction.Cancel:
                            throw new OperationCanceledException(cancellationToken);

                        case ConflictAction.AutoRename:
                            destination = PathUtils.GetUniquePath(
                                destination,
                                p => File.Exists(PathUtils.ToExtendedPath(p)) || Directory.Exists(PathUtils.ToExtendedPath(p)));
                            item.Outcome = FileOutcome.Renamed;
                            break;

                        case ConflictAction.Overwrite:
                        default:
                            item.Outcome = FileOutcome.Overwritten;
                            break;
                    }
                }

                item.EffectiveDestinationPath = destination;
                runtime.CurrentDestination = destination;

                // ---- fast same-volume move -------------------------------------
                var canFastMove = runtime.Job.Mode == TransferMode.Move
                                  && options.AllowSameVolumeFastMove
                                  && !options.VerifyWithSha256
                                  && _drives.AreOnSameVolume(item.SourcePath, destination);

                if (canFastMove)
                {
                    ClearReadOnly(destination);
                    Directory.CreateDirectory(PathUtils.ToExtendedPath(Path.GetDirectoryName(destination)!));
                    File.Move(
                        PathUtils.ToExtendedPath(item.SourcePath),
                        PathUtils.ToExtendedPath(destination),
                        overwrite: true);

                    Interlocked.Add(ref runtime.TransferredBytes, item.Size);
                    item.Outcome = FileOutcome.Moved;
                    Interlocked.Increment(ref runtime.CompletedFiles);
                    runtime.MarkCompleted(item.RelativePath);
                    return;
                }

                // ---- copy -------------------------------------------------------
                await CopyFileAsync(runtime, item, destination, cancellationToken).ConfigureAwait(false);

                // ---- verify -----------------------------------------------------
                if (options.VerifyWithSha256)
                {
                    runtime.IsVerifying = true;
                    try
                    {
                        item.SourceHash = await _hashService.ComputeSha256Async(item.SourcePath, cancellationToken).ConfigureAwait(false);
                        item.DestinationHash = await _hashService.ComputeSha256Async(destination, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        runtime.IsVerifying = false;
                    }

                    if (!string.Equals(item.SourceHash, item.DestinationHash, StringComparison.OrdinalIgnoreCase))
                    {
                        item.Outcome = FileOutcome.VerificationFailed;
                        item.Error = "SHA-256 mismatch between source and destination.";
                        item.Reason = FailureReason.VerificationMismatch;
                        item.FailedAt = DateTimeOffset.Now;
                        item.Attempts = attempt;
                        Interlocked.Increment(ref runtime.FailedFiles);
                        runtime.MarkFailed(item.RelativePath);
                        TryDelete(destination);
                        return;
                    }
                }

                // ---- move: delete the source, last of all ----------------------
                if (runtime.Job.Mode == TransferMode.Move)
                {
                    ClearReadOnly(item.SourcePath);
                    File.Delete(PathUtils.ToExtendedPath(item.SourcePath));
                    item.Outcome = FileOutcome.Moved;
                }
                else if (item.Outcome is FileOutcome.Pending)
                {
                    item.Outcome = FileOutcome.Copied;
                }

                Interlocked.Increment(ref runtime.CompletedFiles);
                runtime.MarkCompleted(item.RelativePath);
                return;
            }
            catch (OperationCanceledException)
            {
                item.Outcome = FileOutcome.Canceled;
                throw;
            }
            catch (FileSkippedException)
            {
                // The user abandoned this one file; the rest of the job is unaffected.
                _logger.LogInformation("Skipped on request: {Path}", item.SourcePath);
                item.Outcome = FileOutcome.Skipped;
                item.Error = "Skipped on request.";
                item.Reason = FailureReason.SkippedByUser;
                Interlocked.Increment(ref runtime.SkippedFiles);
                runtime.MarkCompleted(item.RelativePath);
                return;
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                item.Outcome = FileOutcome.Failed;
                item.Error = "The destination volume is full.";
                item.Reason = FailureReason.DiskFull;
                item.NativeErrorCode = FailureClassifier.NativeCode(ex);
                item.FailedAt = DateTimeOffset.Now;
                item.Attempts = attempt;
                Interlocked.Increment(ref runtime.FailedFiles);
                runtime.MarkFailed(item.RelativePath);
                throw new TransferAbortedException("The destination volume ran out of space.", ex);
            }
            catch (Exception ex) when (IsDestinationGone(runtime, ex))
            {
                item.Outcome = FileOutcome.Failed;
                item.Error = "The destination is no longer available.";
                item.Reason = FailureReason.DestinationUnavailable;
                item.NativeErrorCode = FailureClassifier.NativeCode(ex);
                item.FailedAt = DateTimeOffset.Now;
                item.Attempts = attempt;
                Interlocked.Increment(ref runtime.FailedFiles);
                runtime.MarkFailed(item.RelativePath);
                throw new TransferAbortedException(
                    "The destination became unavailable (drive removed or network lost).", ex);
            }
            catch (Exception ex)
            {
                if (attempt < attempts)
                {
                    _logger.LogDebug(ex, "Retry {Attempt}/{Attempts} for {Path}", attempt, attempts, item.SourcePath);
                    try
                    {
                        await Task.Delay(runtime.Options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        item.Outcome = FileOutcome.Canceled;
                        throw;
                    }

                    continue;
                }

                var diagnosis = FailureClassifier.Classify(ex);
                _logger.LogWarning(
                    ex, "File failed: {Path} ({Code})", item.SourcePath, diagnosis.Code);

                item.Outcome = FileOutcome.Failed;
                // Keep the OS text for the log, but the UI and the report read the reason code.
                item.Error = PathUtils.Readable(string.IsNullOrWhiteSpace(ex.Message) ? diagnosis.Message : ex.Message);
                item.Reason = diagnosis.Reason;
                item.NativeErrorCode = diagnosis.NativeCode;
                item.FailedAt = DateTimeOffset.Now;
                item.Attempts = attempt;
                Interlocked.Increment(ref runtime.FailedFiles);
                runtime.MarkFailed(item.RelativePath);

                if (!runtime.Options.ContinueOnError)
                    throw new TransferAbortedException($"Transfer stopped at '{item.RelativePath}': {ex.Message}", ex);

                return;
            }
        }
    }

    private async Task<ConflictAction> ResolveConflictAsync(
        JobRuntime runtime,
        TransferItem item,
        string destination,
        CancellationToken cancellationToken)
    {
        var destinationInfo = new FileInfo(PathUtils.ToExtendedPath(destination));
        var info = new ConflictInfo(
            item.SourcePath,
            destination,
            item.Size,
            destinationInfo.Exists ? destinationInfo.Length : 0,
            item.LastWriteTimeUtc,
            destinationInfo.Exists ? destinationInfo.LastWriteTimeUtc : DateTime.MinValue);

        var policy = runtime.EffectivePolicy;
        var decided = ConflictStrategy.Decide(policy, info);
        if (decided is not null)
            return decided.Value;

        // Ask: serialize so the user sees one dialog at a time even with N workers.
        await runtime.ConflictGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another worker may have answered "apply to all" while we waited.
            var recheck = ConflictStrategy.Decide(runtime.EffectivePolicy, info);
            if (recheck is not null)
                return recheck.Value;

            var decision = await _conflictResolver.ResolveAsync(info, cancellationToken).ConfigureAwait(false);
            if (decision.ApplyToAll)
                runtime.EffectivePolicy = ConflictStrategy.ToPolicy(decision.Action);

            return decision.Action;
        }
        finally
        {
            runtime.ConflictGate.Release();
        }
    }

    private async Task CopyFileAsync(
        JobRuntime runtime,
        TransferItem item,
        string destination,
        CancellationToken cancellationToken)
    {
        var options = runtime.Options;
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(PathUtils.ToExtendedPath(directory));

        var writePath = options.UseTemporaryPartFiles ? PathUtils.GetPartialPath(destination) : destination;
        var bufferSize = options.AutoTune
            ? TransferTuner.GetBufferSize(item.Size, DriveKindHint.Unknown, DriveKindHint.Unknown)
            : runtime.BufferSize;

        // Keep the auto-tuned per-device buffer, but never exceed the file size by much.
        bufferSize = Math.Clamp(
            options.AutoTune ? Math.Min(runtime.BufferSize, Math.Max(TransferTuner.MinBuffer, bufferSize)) : runtime.BufferSize,
            TransferTuner.MinBuffer,
            TransferTuner.MaxBuffer);

        ClearReadOnly(writePath);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        long copied = 0;

        try
        {
            await using (var source = new FileStream(
                             PathUtils.ToExtendedPath(item.SourcePath),
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Open,
                                 Access = FileAccess.Read,
                                 Share = FileShare.Read,
                                 BufferSize = 0,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                             }))
            await using (var target = new FileStream(
                             PathUtils.ToExtendedPath(writePath),
                             new FileStreamOptions
                             {
                                 Mode = FileMode.Create,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 BufferSize = 0,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                                 // Reserving the range up front avoids fragmentation and fails
                                 // fast when the volume cannot hold the file.
                                 PreallocationSize = item.Size > 0 ? item.Size : 0
                             }))
            {
                while (true)
                {
                    await runtime.PauseToken.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    // "Skip current file" is honoured between buffers: the partial file is
                    // rolled back by the catch below and the job carries on with the next file.
                    if (runtime.Skip.Consume())
                        throw new FileSkippedException(item.SourcePath);

                    var read = await source.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                    if (read <= 0) break;

                    if (runtime.RateLimiter.IsEnabled)
                        await runtime.RateLimiter.WaitAsync(read, cancellationToken).ConfigureAwait(false);

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                    copied += read;
                    Interlocked.Add(ref runtime.TransferredBytes, read);
                    runtime.CurrentFileBytes = copied;
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            ApplyMetadata(item, writePath, options);

            if (options.UseTemporaryPartFiles)
            {
                ClearReadOnly(destination);
                File.Move(PathUtils.ToExtendedPath(writePath), PathUtils.ToExtendedPath(destination), overwrite: true);
                // Attributes survive the rename, but re-apply read-only in case it was cleared.
                ApplyAttributes(item, destination, options);
            }
        }
        catch
        {
            // Roll back the partial write; the already-counted bytes are subtracted so the
            // progress numbers stay honest.
            Interlocked.Add(ref runtime.TransferredBytes, -copied);
            if (options.UseTemporaryPartFiles)
                TryDelete(writePath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            runtime.CurrentFileBytes = 0;
        }
    }

    // =====================================================================
    // metadata, cleanup, progress
    // =====================================================================

    private static void ApplyMetadata(TransferItem item, string path, TransferOptions options)
    {
        var extended = PathUtils.ToExtendedPath(path);

        if (options.PreserveTimestamps)
        {
            try
            {
                File.SetLastWriteTimeUtc(extended, item.LastWriteTimeUtc);
                var sourceInfo = new FileInfo(PathUtils.ToExtendedPath(item.SourcePath));
                if (sourceInfo.Exists)
                {
                    File.SetCreationTimeUtc(extended, sourceInfo.CreationTimeUtc);
                    File.SetLastAccessTimeUtc(extended, sourceInfo.LastAccessTimeUtc);
                }
            }
            catch
            {
                // Some file systems (FAT32, some network shares) refuse one or more of these.
            }
        }

        ApplyAttributes(item, path, options);
    }

    private static void ApplyAttributes(TransferItem item, string path, TransferOptions options)
    {
        if (!options.PreserveAttributes) return;

        try
        {
            // Hidden / system / archive / read-only are copied; structural flags are not.
            const FileAttributes copyable =
                FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive |
                FileAttributes.ReadOnly | FileAttributes.NotContentIndexed | FileAttributes.Temporary;

            var attributes = item.Attributes & copyable;
            if (attributes != 0)
                File.SetAttributes(PathUtils.ToExtendedPath(path), attributes);
        }
        catch
        {
            // Not fatal: the data is already there.
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var extended = PathUtils.ToExtendedPath(path);
            if (!File.Exists(extended)) return;

            var attributes = File.GetAttributes(extended);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(extended, attributes & ~FileAttributes.ReadOnly);
        }
        catch
        {
            // Best effort; the copy will report the real error if it still cannot write.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            var extended = PathUtils.ToExtendedPath(path);
            if (File.Exists(extended))
            {
                ClearReadOnly(path);
                File.Delete(extended);
            }
        }
        catch
        {
            // Nothing else we can do; a stray .fspart is harmless and is cleaned on resume.
        }
    }

    private static void RestoreDirectoryMetadata(ScanResult scan, TransferOptions options)
    {
        // Deepest first, so writing a child does not bump the parent's timestamp again.
        foreach (var directory in scan.Directories
                     .OrderByDescending(d => d.DestinationPath.Count(c => c == '\\')))
        {
            try
            {
                var extended = PathUtils.ToExtendedPath(directory.DestinationPath);
                if (!Directory.Exists(extended)) continue;

                Directory.SetLastWriteTimeUtc(extended, directory.LastWriteTimeUtc);

                if (options.PreserveAttributes)
                {
                    const FileAttributes copyable = FileAttributes.Hidden | FileAttributes.System;
                    var attributes = directory.Attributes & copyable;
                    if (attributes != 0)
                        File.SetAttributes(extended, attributes | FileAttributes.Directory);
                }
            }
            catch
            {
                // Non-fatal.
            }
        }
    }

    private void PruneEmptySourceDirectories(TransferJob job, ScanResult scan)
    {
        foreach (var directory in scan.Directories
                     .OrderByDescending(d => d.SourcePath.Count(c => c == '\\')))
        {
            try
            {
                var extended = PathUtils.ToExtendedPath(directory.SourcePath);
                if (!Directory.Exists(extended)) continue;

                // Never remove a selected source root's volume root, and only remove
                // directories that are genuinely empty.
                if (Path.GetPathRoot(directory.SourcePath)?.TrimEnd('\\')
                    .Equals(directory.SourcePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
                    continue;

                if (!Directory.EnumerateFileSystemEntries(extended).Any())
                    Directory.Delete(extended);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not remove emptied source folder {Path}", directory.SourcePath);
            }
        }
    }

    private static async Task PublishProgressAsync(
        JobRuntime runtime,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null) return;

        using var timer = new PeriodicTimer(ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                progress.Report(runtime.Snapshot());
        }
        catch (OperationCanceledException)
        {
            // Final snapshot so the UI lands on exact numbers.
        }

        progress.Report(runtime.Snapshot());
    }

    private async Task PersistCheckpointsAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        if (_stateStore is null) return;

        using var timer = new PeriodicTimer(CheckpointInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await SaveCheckpointAsync(runtime, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Saved by the caller after the run ends.
        }
    }

    private async Task SaveCheckpointAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        if (_stateStore is null) return;

        try
        {
            var job = runtime.Job;
            var checkpoint = new JobCheckpoint
            {
                JobId = job.Id,
                Name = job.Name,
                DestinationRoot = job.DestinationRoot,
                Mode = job.Mode,
                Options = runtime.Options,
                State = job.State,
                UpdatedAt = DateTimeOffset.Now,
                TotalBytes = runtime.Scan.TotalBytes,
                TotalFiles = runtime.Scan.FileCount,
                TransferredBytes = Interlocked.Read(ref runtime.TransferredBytes)
            };

            checkpoint.Sources.AddRange(job.Sources);
            checkpoint.CompletedRelativePaths.AddRange(runtime.CompletedPaths);
            checkpoint.FailedRelativePaths.AddRange(runtime.FailedPaths);

            await _stateStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Checkpoint save failed for job {JobId}", runtime.Job.Id);
        }
    }

    private static bool IsDiskFull(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorDiskFull or ErrorHandleDiskFull;
    }

    private bool IsDestinationGone(JobRuntime runtime, Exception ex)
    {
        if (ex is not (IOException or UnauthorizedAccessException)) return false;

        try
        {
            var root = _drives.GetVolumeRoot(runtime.Job.DestinationRoot);
            return root is not null && !Directory.Exists(PathUtils.ToExtendedPath(root));
        }
        catch
        {
            return false;
        }
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch
        {
            // Progress/checkpoint loops must never fail the job.
        }
    }

    // =====================================================================
    // mutable state for one run
    // =====================================================================

    private sealed class JobRuntime
    {
        public JobRuntime(
            TransferJob job,
            ScanResult scan,
            TransferOptions options,
            int bufferSize,
            PauseToken pauseToken,
            SkipSignal skipSignal)
        {
            Job = job;
            Scan = scan;
            Options = options;
            BufferSize = bufferSize;
            PauseToken = pauseToken;
            Skip = skipSignal;
            EffectivePolicy = options.ConflictPolicy;
        }

        public SkipSignal Skip { get; }

        public TransferJob Job { get; }
        public ScanResult Scan { get; }
        public TransferOptions Options { get; }
        public int BufferSize { get; }
        public PauseToken PauseToken { get; }

        public RateLimiter RateLimiter { get; init; } = new();
        public bool SameVolume { get; init; }

        public SpeedCalculator Speed { get; } = new();
        public SemaphoreSlim ConflictGate { get; } = new(1, 1);

        public volatile ConflictPolicy EffectivePolicy;
        public volatile bool IsVerifying;
        public volatile TransferItem? CurrentFile;
        public volatile string? CurrentDestination;
        public long CurrentFileBytes;

        public long TransferredBytes;
        public int CompletedFiles;
        public int SkippedFiles;
        public int FailedFiles;

        public ConcurrentBag<string> CompletedPaths { get; } = new();
        public ConcurrentBag<string> FailedPaths { get; } = new();

        public void MarkCompleted(string relativePath) => CompletedPaths.Add(relativePath);
        public void MarkFailed(string relativePath) => FailedPaths.Add(relativePath);

        public TransferProgress Snapshot()
        {
            var transferred = Interlocked.Read(ref TransferredBytes);
            Speed.Report(transferred);

            var current = CurrentFile;
            var remaining = Math.Max(0, Scan.TotalBytes - transferred);

            return new TransferProgress
            {
                JobId = Job.Id,
                State = PauseToken.IsPaused ? JobState.Paused : Job.State,
                TotalBytes = Scan.TotalBytes,
                TransferredBytes = transferred,
                TotalFiles = Scan.FileCount,
                CompletedFiles = Volatile.Read(ref CompletedFiles),
                SkippedFiles = Volatile.Read(ref SkippedFiles),
                FailedFiles = Volatile.Read(ref FailedFiles),
                CurrentBytesPerSecond = Speed.CurrentBytesPerSecond,
                AverageBytesPerSecond = Speed.AverageBytesPerSecond,
                Elapsed = Speed.Elapsed,
                Eta = Speed.EstimateRemaining(remaining),
                CurrentFileName = current is null ? null : Path.GetFileName(current.SourcePath),
                CurrentFilePath = current?.SourcePath,
                CurrentFileDestination = CurrentDestination,
                CurrentFileBytes = Interlocked.Read(ref CurrentFileBytes),
                CurrentFileSize = current?.Size ?? 0,
                IsVerifying = IsVerifying,
                PhaseLabel = IsVerifying ? "verify" : null
            };
        }
    }
}
