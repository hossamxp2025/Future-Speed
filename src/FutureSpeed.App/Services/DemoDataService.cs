using System.IO;
using System.Security.Cryptography;

namespace FutureSpeed.App.Services;

public sealed record DemoDataResult(string Root, int FileCount, long TotalBytes);

public interface IDemoDataService
{
    /// <summary>
    /// Creates a throw-away folder tree in the app's own temp area so the UI can be
    /// exercised (progress, conflicts, verification) without touching real user files.
    /// </summary>
    Task<DemoDataResult> CreateAsync(CancellationToken cancellationToken = default);

    string DemoRoot { get; }
}

public sealed class DemoDataService : IDemoDataService
{
    private readonly ISettingsService _settings;

    public DemoDataService(ISettingsService settings) => _settings = settings;

    public string DemoRoot => Path.Combine(_settings.AppDataDirectory, "demo-data");

    public async Task<DemoDataResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(DemoRoot, "source");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(DemoRoot, "destination"));

        // A mix of sizes: many tiny files (worst case for per-file overhead) plus a few
        // large ones (where buffering and throughput actually show).
        var plan = new (string Folder, int Count, int SizeKb)[]
        {
            ("documents", 40, 24),
            ("documents/reports", 15, 180),
            ("photos", 20, 2048),
            ("video", 2, 61440),
            ("empty-folder", 0, 0)
        };

        var files = 0;
        long bytes = 0;
        var buffer = new byte[256 * 1024];
        RandomNumberGenerator.Fill(buffer);

        foreach (var (folder, count, sizeKb) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory);

            for (var i = 1; i <= count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var path = Path.Combine(directory, $"demo-{i:D3}.bin");
                var target = (long)sizeKb * 1024;

                await using var stream = new FileStream(
                    path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 256, useAsync: true);

                long written = 0;
                while (written < target)
                {
                    var chunk = (int)Math.Min(buffer.Length, target - written);
                    await stream.WriteAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
                    written += chunk;
                }

                files++;
                bytes += written;
            }
        }

        return new DemoDataResult(root, files, bytes);
    }
}
