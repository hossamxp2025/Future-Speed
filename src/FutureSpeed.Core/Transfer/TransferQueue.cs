using System.Collections.Concurrent;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Transfer;

/// <summary>A job plus the handles needed to pause, resume or cancel it.</summary>
public sealed class QueuedJob
{
    internal QueuedJob(TransferJob job, long sequence)
    {
        Job = job;
        Sequence = sequence;
    }

    public TransferJob Job { get; }
    public long Sequence { get; }

    public JobState State
    {
        get => Job.State;
        internal set => Job.State = value;
    }

    public TransferProgress Progress { get; internal set; } = TransferProgress.Empty;
    public TransferResult? Result { get; internal set; }
    public string? Error { get; internal set; }
    public SpaceValidationResult? SpaceProblem { get; internal set; }

    internal PauseTokenSource Pause { get; } = new();
    internal SkipSignal Skip { get; } = new();
    internal CancellationTokenSource? Cancellation { get; set; }

    public bool IsPaused => Pause.IsPaused;

    public bool IsFinished => State is JobState.Completed or JobState.CompletedWithErrors
        or JobState.Failed or JobState.Canceled;

    public bool IsScheduled => Job.ScheduledFor is { } when && when > DateTimeOffset.Now;
}

public sealed class JobEventArgs : EventArgs
{
    public JobEventArgs(QueuedJob job) => Job = job;
    public QueuedJob Job { get; }
}

public interface ITransferQueue : IAsyncDisposable
{
    IReadOnlyList<QueuedJob> Jobs { get; }

    /// <summary>How many jobs may run at the same time. One by default: parallel jobs fight for the same spindle.</summary>
    int MaxConcurrentJobs { get; set; }

    event EventHandler<JobEventArgs>? JobAdded;
    event EventHandler<JobEventArgs>? JobStarted;
    event EventHandler<JobEventArgs>? JobProgress;
    event EventHandler<JobEventArgs>? JobFinished;
    event EventHandler? QueueChanged;

    QueuedJob Enqueue(TransferJob job, bool startImmediately = true);
    QueuedJob? Find(string jobId);

    void PauseJob(string jobId);
    void ResumeJob(string jobId);
    void CancelJob(string jobId);

    /// <summary>Abandons the file being written right now; the job continues with the next one.</summary>
    void SkipCurrentFile(string jobId);
    bool RemoveJob(string jobId);
    void SetPriority(string jobId, int priority);
    void MoveUp(string jobId);
    void MoveDown(string jobId);

    /// <summary>Re-queues only the files that failed in a finished job.</summary>
    QueuedJob? RetryFailed(string jobId);

    void ClearFinished();
}

/// <summary>
/// Runs queued jobs one (or a few) at a time, honouring priority and schedule.
/// A single pump task owns the lifecycle, so job state transitions happen in one place.
/// </summary>
public sealed class TransferQueue : ITransferQueue
{
    private readonly ITransferEngine _engine;
    private readonly ILogger<TransferQueue> _logger;
    private readonly ConcurrentDictionary<string, QueuedJob> _jobs = new();
    private readonly List<QueuedJob> _order = new();
    private readonly object _orderGate = new();
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    private long _sequence;
    private int _disposed;

    public TransferQueue(ITransferEngine engine, ILogger<TransferQueue>? logger = null)
    {
        _engine = engine;
        _logger = logger ?? NullLogger<TransferQueue>.Instance;
        _slots = new SemaphoreSlim(1, 32);
        _pump = Task.Run(PumpAsync);
    }

    public IReadOnlyList<QueuedJob> Jobs
    {
        get
        {
            lock (_orderGate)
                return _order.ToList();
        }
    }

    private int _maxConcurrentJobs = 1;

    public int MaxConcurrentJobs
    {
        get => _maxConcurrentJobs;
        set
        {
            var target = Math.Clamp(value, 1, 8);
            var delta = target - _maxConcurrentJobs;
            _maxConcurrentJobs = target;
            if (delta > 0) _slots.Release(delta);
            // Shrinking takes effect as running jobs finish; slots are not forcibly reclaimed.
        }
    }

    public event EventHandler<JobEventArgs>? JobAdded;
    public event EventHandler<JobEventArgs>? JobStarted;
    public event EventHandler<JobEventArgs>? JobProgress;
    public event EventHandler<JobEventArgs>? JobFinished;
    public event EventHandler? QueueChanged;

    public QueuedJob Enqueue(TransferJob job, bool startImmediately = true)
    {
        ArgumentNullException.ThrowIfNull(job);

        var queued = new QueuedJob(job, Interlocked.Increment(ref _sequence))
        {
            State = startImmediately ? JobState.Queued : JobState.Created
        };

        _jobs[job.Id] = queued;
        lock (_orderGate)
            _order.Add(queued);

        JobAdded?.Invoke(this, new JobEventArgs(queued));
        QueueChanged?.Invoke(this, EventArgs.Empty);
        return queued;
    }

    public QueuedJob? Find(string jobId)
        => jobId is not null && _jobs.TryGetValue(jobId, out var job) ? job : null;

    public void PauseJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null || job.IsFinished) return;

        job.Pause.Pause();
        if (job.State == JobState.Running) job.State = JobState.Paused;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null) return;

        if (job.IsFinished && job.State is JobState.Canceled or JobState.CompletedWithErrors or JobState.Failed)
        {
            // Restart an interrupted job: the engine skips what the checkpoint says is done.
            job.Job.Scan = null;
            job.Result = null;
            job.Error = null;
            job.State = JobState.Queued;
        }
        else
        {
            job.Pause.Resume();
            if (job.State == JobState.Paused) job.State = JobState.Running;
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CancelJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null || job.IsFinished) return;

        // Resume first: a paused worker must wake up to observe the cancellation.
        job.Pause.Resume();
        job.Cancellation?.Cancel();

        if (job.State is JobState.Queued or JobState.Created)
        {
            job.State = JobState.Canceled;
            JobFinished?.Invoke(this, new JobEventArgs(job));
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SkipCurrentFile(string jobId)
    {
        var job = Find(jobId);
        if (job is null || job.IsFinished) return;

        job.Skip.Request();

        // A paused job would never reach the check, so wake it up.
        if (job.IsPaused)
        {
            job.Pause.Resume();
            if (job.State == JobState.Paused) job.State = JobState.Running;
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool RemoveJob(string jobId)
    {
        var job = Find(jobId);
        if (job is null) return false;
        if (!job.IsFinished && job.State is JobState.Running or JobState.Paused) return false;

        _jobs.TryRemove(jobId, out _);
        lock (_orderGate)
            _order.Remove(job);

        QueueChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SetPriority(string jobId, int priority)
    {
        var job = Find(jobId);
        if (job is null) return;

        job.Job.Priority = priority;
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MoveUp(string jobId) => Reorder(jobId, -1);
    public void MoveDown(string jobId) => Reorder(jobId, +1);

    private void Reorder(string jobId, int delta)
    {
        lock (_orderGate)
        {
            var index = _order.FindIndex(j => j.Job.Id == jobId);
            if (index < 0) return;

            var target = Math.Clamp(index + delta, 0, _order.Count - 1);
            if (target == index) return;

            var job = _order[index];
            _order.RemoveAt(index);
            _order.Insert(target, job);

            // Priorities follow the visible order so the pump agrees with the list.
            for (var i = 0; i < _order.Count; i++)
                _order[i].Job.Priority = i;
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    public QueuedJob? RetryFailed(string jobId)
    {
        var finished = Find(jobId);
        if (finished?.Result is null) return null;

        var failed = finished.Result.Items
            .Where(i => i.Outcome is FileOutcome.Failed or FileOutcome.VerificationFailed)
            .ToList();

        if (failed.Count == 0) return null;

        var retry = new TransferJob
        {
            Name = finished.Job.Name + " · retry",
            DestinationRoot = finished.Job.DestinationRoot,
            Mode = finished.Job.Mode,
            Options = finished.Job.Options.Clone()
        };

        retry.Sources.AddRange(failed.Select(f => f.SourcePath));

        // Reuse the known plan so the retry does not rescan the whole tree.
        var scan = new ScanResult();
        foreach (var item in failed)
        {
            scan.Files.Add(new TransferItem
            {
                SourcePath = item.SourcePath,
                DestinationPath = item.DestinationPath,
                RelativePath = item.RelativePath,
                Size = item.Size,
                LastWriteTimeUtc = item.LastWriteTimeUtc,
                Attributes = item.Attributes
            });
        }

        scan.TotalBytes = scan.Files.Sum(f => f.Size);
        retry.Scan = scan;

        return Enqueue(retry);
    }

    public void ClearFinished()
    {
        lock (_orderGate)
        {
            foreach (var job in _order.Where(j => j.IsFinished).ToList())
            {
                _order.Remove(job);
                _jobs.TryRemove(job.Job.Id, out _);
            }
        }

        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    // =====================================================================
    // pump
    // =====================================================================

    private async Task PumpAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false);

                var next = DequeueNext();
                if (next is null) continue;

                await _slots.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                _ = RunJobAsync(next).ContinueWith(
                    _ => _slots.Release(),
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue pump error.");
            }
        }
    }

    private QueuedJob? DequeueNext()
    {
        lock (_orderGate)
        {
            return _order
                .Where(j => j.State == JobState.Queued && !j.IsScheduled)
                .OrderBy(j => j.Job.Priority)
                .ThenBy(j => j.Sequence)
                .FirstOrDefault();
        }
    }

    private async Task RunJobAsync(QueuedJob queued)
    {
        queued.State = JobState.Running;
        queued.Error = null;
        queued.SpaceProblem = null;
        queued.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        JobStarted?.Invoke(this, new JobEventArgs(queued));
        QueueChanged?.Invoke(this, EventArgs.Empty);

        var progress = new Progress<TransferProgress>(snapshot =>
        {
            queued.Progress = snapshot;
            JobProgress?.Invoke(this, new JobEventArgs(queued));
        });

        try
        {
            queued.Skip.Clear();
            queued.Result = await _engine.ExecuteAsync(
                queued.Job,
                progress,
                queued.Pause.Token,
                queued.Skip,
                queued.Cancellation.Token).ConfigureAwait(false);

            queued.State = queued.Result.FinalState;
        }
        catch (InsufficientSpaceException ex)
        {
            queued.SpaceProblem = ex.Validation;
            queued.Error = ex.Message;
            queued.State = JobState.Failed;
        }
        catch (OperationCanceledException)
        {
            queued.State = JobState.Canceled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed.", queued.Job.Id);
            queued.Error = ex.Message;
            queued.State = JobState.Failed;
        }
        finally
        {
            queued.Cancellation?.Dispose();
            queued.Cancellation = null;
            JobFinished?.Invoke(this, new JobEventArgs(queued));
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposal must be idempotent: the app shuts the queue down explicitly so a final
        // checkpoint is written, and the DI container then disposes it a second time.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _shutdown.Cancel();

        foreach (var job in Jobs.Where(j => !j.IsFinished))
        {
            job.Pause.Resume();
            job.Cancellation?.Cancel();
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // Shutting down.
        }

        _shutdown.Dispose();
        _slots.Dispose();
    }
}
