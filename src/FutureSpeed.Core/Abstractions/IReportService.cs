using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

public interface IReportService
{
    /// <summary>Writes a report for a finished job and returns the file path.</summary>
    Task<string> SaveAsync(
        TransferResult result,
        ReportFormat format,
        string directory,
        CancellationToken cancellationToken = default);
}
