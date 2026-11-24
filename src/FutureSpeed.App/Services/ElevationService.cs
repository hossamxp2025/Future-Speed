using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

public interface IElevationService
{
    bool IsElevated { get; }

    /// <summary>True when the current process cannot write into <paramref name="directory"/>.</summary>
    bool RequiresElevation(string directory);

    /// <summary>Restarts the app with a UAC prompt. Returns false when the user declines.</summary>
    bool RestartElevated(string? arguments = null);
}

/// <summary>
/// The app runs as the invoking user (see app.manifest). Elevation is requested only
/// when a specific destination actually refuses a write, which keeps the common case
/// prompt-free.
/// </summary>
public sealed class ElevationService : IElevationService
{
    private readonly ILogger<ElevationService> _logger;

    public ElevationService(ILogger<ElevationService> logger) => _logger = logger;

    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    public bool RequiresElevation(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;

        try
        {
            Directory.CreateDirectory(directory);

            // A probe write is the only reliable test: ACLs, read-only volumes and
            // network permissions all surface here and nowhere else.
            var probe = Path.Combine(directory, $".futurespeed-probe-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return !IsElevated;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Write probe failed for {Directory}", directory);
            return false;
        }
    }

    public bool RestartElevated(string? arguments = null)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable)) return false;

            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments ?? string.Empty
            };

            Process.Start(info);
            Application.Current?.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            // Most often the user clicked "No" on the UAC prompt.
            _logger.LogInformation(ex, "Elevated restart was not completed.");
            return false;
        }
    }
}
