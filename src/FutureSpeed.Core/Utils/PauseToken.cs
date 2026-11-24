namespace FutureSpeed.Core.Utils;

/// <summary>
/// A cooperative pause switch. Workers await <see cref="PauseToken.WaitWhilePausedAsync"/>
/// at safe points (between buffers), so pausing never leaves a half-written buffer.
/// </summary>
public sealed class PauseTokenSource
{
    private volatile TaskCompletionSource<bool>? _paused;

    public bool IsPaused => _paused is not null;

    public PauseToken Token => new(this);

    public void Pause()
    {
        // Only the first caller installs the gate.
        Interlocked.CompareExchange(
            ref _paused,
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            null);
    }

    public void Resume()
    {
        var tcs = Interlocked.Exchange(ref _paused, null);
        tcs?.TrySetResult(true);
    }

    internal Task WaitAsync(CancellationToken cancellationToken)
    {
        var tcs = _paused;
        if (tcs is null)
            return Task.CompletedTask;

        return cancellationToken.CanBeCanceled
            ? WaitWithCancellationAsync(tcs.Task, cancellationToken)
            : tcs.Task;
    }

    private static async Task WaitWithCancellationAsync(Task gate, CancellationToken cancellationToken)
    {
        var cancelTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(static s =>
            ((TaskCompletionSource<bool>)s!).TrySetResult(true), cancelTcs).ConfigureAwait(false);

        await Task.WhenAny(gate, cancelTcs.Task).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

public readonly struct PauseToken
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource source) => _source = source;

    public static PauseToken None => default;

    public bool IsPaused => _source?.IsPaused ?? false;

    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
        => _source?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
}
