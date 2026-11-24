using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;

namespace FutureSpeed.Core.Tests.Integration;

/// <summary>
/// A disposable temp folder with a source/destination pair plus a ready-made engine.
/// Everything lives under the system temp folder and is deleted afterwards.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "FutureSpeed.Tests", Guid.NewGuid().ToString("N"));
        Source = Path.Combine(Root, "source");
        Destination = Path.Combine(Root, "destination");
        StateDirectory = Path.Combine(Root, "state");

        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Destination);
        Directory.CreateDirectory(StateDirectory);
    }

    public string Root { get; }
    public string Source { get; }
    public string Destination { get; }
    public string StateDirectory { get; }

    public string CreateFile(string relativePath, int sizeBytes, byte fill = 0xAB)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var data = new byte[sizeBytes];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)((fill + i) % 256);

        File.WriteAllBytes(path, data);
        return path;
    }

    public string CreateDestinationFile(string relativePath, int sizeBytes, byte fill = 0x11)
    {
        var path = Path.Combine(Destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var data = new byte[sizeBytes];
        Array.Fill(data, fill);
        File.WriteAllBytes(path, data);
        return path;
    }

    public string DestinationPathOf(string relativePath)
        => Path.Combine(Destination, "source", relativePath);

    public TransferEngine CreateEngine(
        IConflictResolver? resolver = null,
        IDriveService? drives = null,
        IJobStateStore? stateStore = null)
        => new(
            new FileSystemScanner(),
            new SpaceValidator(drives ?? new DriveService()),
            drives ?? new DriveService(),
            new Sha256HashService(),
            resolver ?? new PolicyConflictResolver(ConflictPolicy.Overwrite),
            stateStore);

    public TransferJob CreateJob(
        TransferMode mode = TransferMode.Copy,
        Action<TransferOptions>? configure = null,
        string? jobId = null)
    {
        var options = new TransferOptions
        {
            AutoTune = false,
            BufferSizeBytes = 64 * 1024,
            MaxParallelFiles = 2,
            ConflictPolicy = ConflictPolicy.Overwrite,
            EnableResume = false,
            RetryCount = 0
        };

        configure?.Invoke(options);

        var job = new TransferJob
        {
            Id = jobId ?? Guid.NewGuid().ToString("N"),
            Name = "test",
            DestinationRoot = Destination,
            Mode = mode,
            Options = options
        };

        job.Sources.Add(Source);
        return job;
    }

    public static bool FilesHaveSameContent(string a, string b)
    {
        var left = File.ReadAllBytes(a);
        var right = File.ReadAllBytes(b);
        return left.AsSpan().SequenceEqual(right);
    }

    public IEnumerable<string> LeftoverPartFiles()
        => Directory.Exists(Destination)
            ? Directory.EnumerateFiles(Destination, "*.fspart", SearchOption.AllDirectories)
            : Array.Empty<string>();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A locked temp folder must never fail a test run.
        }
    }
}
