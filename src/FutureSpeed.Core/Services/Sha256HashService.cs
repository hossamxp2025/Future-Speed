using System.Security.Cryptography;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Utils;

namespace FutureSpeed.Core.Services;

/// <summary>
/// Streaming SHA-256. The file is read in fixed chunks, so hashing a 500 GB file uses
/// the same memory as hashing a 500 KB one.
/// </summary>
public sealed class Sha256HashService : IHashService
{
    private const int ChunkSize = 1024 * 1024;

    public async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            PathUtils.ToExtendedPath(path),
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                BufferSize = 0,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Hashes with an explicit buffer; used by tests and by the verify phase.</summary>
    public static async Task<string> ComputeAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var sha = SHA256.Create();
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            sha.TransformBlock(buffer, 0, read, null, 0);

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }
}
