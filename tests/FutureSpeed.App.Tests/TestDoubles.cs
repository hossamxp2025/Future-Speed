using System.IO;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FutureSpeed.App.Tests;

/// <summary>A dialog service that answers from a script instead of showing windows.</summary>
internal sealed class FakeDialogService : IDialogService
{
    public Queue<string[]?> FilePicks { get; } = new();
    public Queue<string?> FolderPicks { get; } = new();
    public Queue<string[]?> FoldersPicks { get; } = new();

    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<string> Infos { get; } = new();
    public List<string> Opened { get; } = new();
    public int ConfirmCount { get; private set; }
    public List<string> ConfirmMessages { get; } = new();
    public SpaceValidationResult? LastSpaceProblem { get; private set; }

    public string[]? PickFiles(string? title = null) => FilePicks.Count > 0 ? FilePicks.Dequeue() : null;

    public string? PickFolder(string? title = null) => FolderPicks.Count > 0 ? FolderPicks.Dequeue() : null;

    public string[]? PickFolders(string? title = null) => FoldersPicks.Count > 0 ? FoldersPicks.Dequeue() : null;

    public bool Confirm(string message, string? title = null)
    {
        ConfirmCount++;
        ConfirmMessages.Add(message);
        return ConfirmAnswer;
    }

    public void Info(string message, string? title = null) => Infos.Add(message);

    public void Error(string message, string? title = null) => Errors.Add(message);

    public bool ShowSpaceProblem(SpaceValidationResult validation)
    {
        LastSpaceProblem = validation;
        return false;
    }

    public void OpenInExplorer(string path) => Opened.Add(path);

    public void OpenFile(string path) => OpenedFiles.Add(path);

    public bool CopyText(string text)
    {
        CopiedText.Add(text);
        return CopyTextSucceeds;
    }

    public List<string> OpenedFiles { get; } = new();
    public List<string> CopiedText { get; } = new();
    public bool CopyTextSucceeds { get; set; } = true;
}

/// <summary>A clipboard that holds whatever a test puts in it.</summary>
internal sealed class FakeClipboard : IClipboardFileReader
{
    public ClipboardFilePayload Payload { get; set; } = ClipboardFilePayload.Empty;
    public List<string> Written { get; } = new();
    public ClipboardOperation WrittenOperation { get; private set; }

    public ClipboardFilePayload Read() => Payload;

    public bool Write(IReadOnlyList<string> files, ClipboardOperation operation)
    {
        Written.Clear();
        Written.AddRange(files);
        WrittenOperation = operation;
        return true;
    }
}

/// <summary>Settings held in memory, pointed at a temp folder.</summary>
internal sealed class FakeSettingsService : ISettingsService
{
    public FakeSettingsService(string root)
    {
        AppDataDirectory = root;
        Directory.CreateDirectory(root);
    }

    public AppSettings Current { get; private set; } = new();

    public string AppDataDirectory { get; }
    public string LogsDirectory => Path.Combine(AppDataDirectory, "logs");
    public string ReportsDirectory => Path.Combine(AppDataDirectory, "reports");
    public string StateDirectory => Path.Combine(AppDataDirectory, "state");
    public string DefaultBackupRoot => Path.Combine(AppDataDirectory, "backups");

    public int SaveCount { get; private set; }

    public event EventHandler? SettingsChanged;

    public void Load() { }

    public void Save()
    {
        SaveCount++;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
    }
}

internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public FakeRegistryStore(IntegrationScope scope) => Scope = scope;

    public IntegrationScope Scope { get; }

    public IReadOnlyCollection<string> AllKeys => _keys.Keys;

    public bool FailWrites { get; set; }

    public void SetValue(string keyPath, string? valueName, string value)
    {
        if (FailWrites) throw new UnauthorizedAccessException("no rights");

        if (!_keys.TryGetValue(keyPath, out var values))
            _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        values[valueName ?? string.Empty] = value;
    }

    public string? GetValue(string keyPath, string? valueName)
        => _keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName ?? string.Empty, out var v) ? v : null;

    public bool KeyExists(string keyPath) => _keys.ContainsKey(keyPath);

    public void DeleteKeyTree(string keyPath)
    {
        foreach (var key in _keys.Keys
                     .Where(k => k.Equals(keyPath, StringComparison.OrdinalIgnoreCase) ||
                                 k.StartsWith(keyPath + "\\", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _keys.Remove(key);
        }
    }
}

internal sealed class FakeExplorerIntegrationManager : IExplorerIntegrationManager
{
    public IntegrationState State { get; set; } = IntegrationState.NotInstalled;
    public bool EnableSucceeds { get; set; } = true;
    public int EnableCalls { get; private set; }
    public int DisableCalls { get; private set; }

    public string ExecutablePath => @"C:\test\FutureSpeed.exe";

    public IntegrationStatus GetStatus(IntegrationScope scope = IntegrationScope.CurrentUser)
        => new(State, scope, ExecutablePath, Array.Empty<string>());

    public IReadOnlyList<RegistryEntry> Preview(IntegrationScope scope = IntegrationScope.CurrentUser)
        => ExplorerIntegrationService.BuildPlan(ExecutablePath, IntegrationLabels.English, scope);

    public bool Enable(IntegrationScope scope = IntegrationScope.CurrentUser)
    {
        EnableCalls++;
        if (EnableSucceeds) State = IntegrationState.Installed;
        return EnableSucceeds;
    }

    public bool Disable(IntegrationScope scope = IntegrationScope.CurrentUser)
    {
        DisableCalls++;
        State = IntegrationState.NotInstalled;
        return true;
    }

    public void DisableEverywhere() => Disable();
}

internal static class TestLogger
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;
}

/// <summary>A disposable temp folder with a source tree, for view-model tests.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "FutureSpeed.AppTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Folder(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string name, int size = 64)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Temp cleanup must never fail a test.
        }
    }
}
