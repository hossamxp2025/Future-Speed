namespace FutureSpeed.Core.Abstractions;

public interface IHashService
{
    /// <summary>Streams the file and returns its lowercase hex SHA-256.</summary>
    Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default);
}
