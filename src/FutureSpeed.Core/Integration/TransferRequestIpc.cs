using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FutureSpeed.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.Core.Integration;

/// <summary>One job handed from an Explorer invocation to the running instance.</summary>
public sealed class TransferRequest
{
    public TransferMode Mode { get; set; } = TransferMode.Copy;
    public List<string> Sources { get; set; } = new();
    public string Destination { get; set; } = string.Empty;
    public bool Compact { get; set; } = true;

    /// <summary>Set when the request came from the clipboard rather than a verb's argument list.</summary>
    public bool FromClipboard { get; set; }

    /// <summary>A second launch with no work: just bring the running instance forward.</summary>
    public bool ActivateMainWindow { get; set; }

    /// <summary>
    /// "Copy/Move with Future Speed": only place the selection on the clipboard. Several of
    /// these arrive at once for one selection and are merged into a single clipboard write.
    /// </summary>
    public bool StageOnly { get; set; }

    public bool HasWork => ActivateMainWindow || FromClipboard || StageOnly ||
                           (Sources.Count > 0 && Destination.Length > 0);

    public static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    public static TransferRequest? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TransferRequest>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed class TransferRequestEventArgs : EventArgs
{
    public TransferRequestEventArgs(TransferRequest request) => Request = request;
    public TransferRequest Request { get; }
}

/// <summary>
/// Single-instance channel. The first process owns the pipe; later invocations (one per
/// Explorer menu click) send their request and exit, so there is never a second transfer
/// engine for the same work.
/// </summary>
public sealed class TransferRequestServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listener;
    private int _disposed;

    public TransferRequestServer(string? pipeName = null, ILogger? logger = null)
    {
        _pipeName = pipeName ?? DefaultPipeName();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Per-user name, so two signed-in users never share a channel.</summary>
    public static string DefaultPipeName() => InstanceNaming.PipeName();

    public string PipeName => _pipeName;

    public event EventHandler<TransferRequestEventArgs>? RequestReceived;

    public void Start()
    {
        _listener ??= Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var json = await reader.ReadToEndAsync(_shutdown.Token).ConfigureAwait(false);

                var request = TransferRequest.FromJson(json);
                if (request is null)
                {
                    _logger.LogWarning("Ignored a malformed transfer request on the IPC channel.");
                    continue;
                }

                RequestReceived?.Invoke(this, new TransferRequestEventArgs(request));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC listener error; retrying.");
                try
                {
                    await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        _shutdown.Cancel();

        // Unblock WaitForConnectionAsync by connecting to ourselves.
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            await client.ConnectAsync(200).ConfigureAwait(false);
        }
        catch
        {
            // Nothing was listening; that is fine.
        }

        if (_listener is not null)
        {
            try
            {
                await _listener.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch
            {
                // Shutting down anyway.
            }
        }

        _shutdown.Dispose();
    }
}

/// <summary>
/// Names shared by every launch of the app. They must be computed the same way in every
/// process: <c>string.GetHashCode()</c> is randomised per process in .NET and would give
/// each launch its own "single instance".
/// </summary>
public static class InstanceNaming
{
    public static string Sanitize(string? user)
    {
        var clean = new string((user ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "user" : clean;
    }

    public static string PipeName(string? user = null)
        => "FutureSpeed.ipc.v1." + Sanitize(user ?? Environment.UserName);

    public static string MutexName(string? user = null)
        => "FutureSpeed.singleinstance.v1." + Sanitize(user ?? Environment.UserName);
}

/// <summary>Sends a request to the instance that owns the pipe.</summary>
public static class TransferRequestClient
{
    /// <summary>Returns false when no instance is listening, so the caller can become the host.</summary>
    public static async Task<bool> TrySendAsync(
        TransferRequest request,
        string? pipeName = null,
        int timeoutMilliseconds = 1500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        pipeName ??= TransferRequestServer.DefaultPipeName();

        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);

            var writer = new StreamWriter(client, new UTF8Encoding(false));
            await writer.WriteAsync(request.ToJson()).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
