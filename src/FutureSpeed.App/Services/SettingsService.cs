using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    string AppDataDirectory { get; }
    string LogsDirectory { get; }
    string ReportsDirectory { get; }
    string StateDirectory { get; }
    string DefaultBackupRoot { get; }

    event EventHandler? SettingsChanged;

    void Load();
    void Save();
    void Replace(AppSettings settings);
}

/// <summary>
/// Settings live in %APPDATA%\FutureSpeed\settings.json. A corrupt file is renamed
/// instead of deleted, so a user can recover their values if something went wrong.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;

        AppDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FutureSpeed");

        Directory.CreateDirectory(AppDataDirectory);
        _settingsPath = Path.Combine(AppDataDirectory, "settings.json");

        Current = new AppSettings();
        Load();
    }

    public AppSettings Current { get; private set; }

    public string AppDataDirectory { get; }

    public string LogsDirectory => Resolve(Current.LogsDirectory, Path.Combine(AppDataDirectory, "logs"));

    public string ReportsDirectory => Resolve(Current.ReportsDirectory, Path.Combine(AppDataDirectory, "reports"));

    public string StateDirectory => Path.Combine(AppDataDirectory, "state");

    public string DefaultBackupRoot => Resolve(
        Current.BackupRoot,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FutureSpeed Backups"));

    public event EventHandler? SettingsChanged;

    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                Save();
                return;
            }

            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is not null)
            {
                Current = Sanitize(loaded);
                return;
            }

            _logger.LogWarning("Settings file was empty; defaults applied.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read settings; defaults applied.");
            TryQuarantine();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var temp = _settingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, _settingsPath, overwrite: true);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings.");
        }
    }

    public void Replace(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Current = Sanitize(settings);
        Save();
    }

    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.Language = settings.Language is "en" or "ar" ? settings.Language : "ar";
        settings.UiScale = Math.Clamp(settings.UiScale, 0.8, 1.6);
        settings.BufferSizeKb = Math.Clamp(settings.BufferSizeKb, 4, 16 * 1024);
        settings.MaxParallelFiles = Math.Clamp(settings.MaxParallelFiles, 1, 32);
        settings.MaxConcurrentJobs = Math.Clamp(settings.MaxConcurrentJobs, 1, 8);
        settings.MaxSpeedMbPerSecond = Math.Clamp(settings.MaxSpeedMbPerSecond, 0, 100_000);
        settings.RecentDestinations = settings.RecentDestinations
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
        return settings;
    }

    private static string Resolve(string? configured, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
        try
        {
            Directory.CreateDirectory(path);
            return path;
        }
        catch
        {
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var broken = _settingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Move(_settingsPath, broken, overwrite: true);
        }
        catch
        {
            // Nothing more to do; defaults are already in use.
        }
    }
}
