using System.Globalization;
using FutureSpeed.Core.Models;

namespace FutureSpeed.Core.Abstractions;

public interface IReportService
{
    /// <summary>Writes a report for a finished job and returns the file path.</summary>
    /// <param name="culture">Language of the human-readable parts; the UI language by default.</param>
    Task<string> SaveAsync(
        TransferResult result,
        ReportFormat format,
        string directory,
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default);
}
