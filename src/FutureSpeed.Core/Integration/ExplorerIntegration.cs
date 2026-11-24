namespace FutureSpeed.Core.Integration;

/// <summary>Where the Explorer verbs are written.</summary>
public enum IntegrationScope
{
    /// <summary>HKEY_CURRENT_USER: no administrator rights needed. The default.</summary>
    CurrentUser = 0,

    /// <summary>HKEY_LOCAL_MACHINE: every user on the machine, needs elevation.</summary>
    AllUsers = 1
}

public enum IntegrationState
{
    NotInstalled = 0,
    Installed = 1,
    /// <summary>Some keys exist, or they point at a different executable (e.g. after a move).</summary>
    Stale = 2
}

public sealed record IntegrationStatus(
    IntegrationState State,
    IntegrationScope Scope,
    string? RegisteredExecutable,
    IReadOnlyList<string> PresentKeys)
{
    public bool IsInstalled => State == IntegrationState.Installed;
}

/// <summary>Labels shown in the Explorer menu, supplied by the UI in the current language.</summary>
public sealed record IntegrationLabels(string Copy, string Move, string Paste)
{
    public static IntegrationLabels English { get; } =
        new("Copy with Future Speed", "Move with Future Speed", "Paste with Future Speed");

    public static IntegrationLabels Arabic { get; } =
        new("نسخ بواسطة Future Speed", "نقل بواسطة Future Speed", "لصق بواسطة Future Speed");
}

/// <summary>A single registry value the integration owns.</summary>
public sealed record RegistryEntry(string KeyPath, string? ValueName, string Value);

/// <summary>
/// Minimal registry surface, so the integration logic can be unit tested against an
/// in-memory store and asserted to leave nothing behind.
/// </summary>
public interface IRegistryStore
{
    /// <summary>Root hive of this store (CurrentUser or AllUsers).</summary>
    IntegrationScope Scope { get; }

    void SetValue(string keyPath, string? valueName, string value);
    string? GetValue(string keyPath, string? valueName);
    bool KeyExists(string keyPath);
    void DeleteKeyTree(string keyPath);
}

/// <summary>
/// Installs and removes the Explorer context-menu verbs.
///
/// Design notes:
///  - Only plain registry verbs are used: no shell extension DLL, no COM server, nothing
///    loaded inside explorer.exe. A broken entry can never destabilise Explorer, and
///    removal is a key delete.
///  - Written under HKCU by default, so no administrator rights are involved.
///  - MultiSelectModel=Player asks Explorer to launch one process for the whole selection
///    instead of one per file.
/// </summary>
public sealed class ExplorerIntegrationService
{
    public const string CopyVerb = "FutureSpeed.Copy";
    public const string MoveVerb = "FutureSpeed.Move";
    public const string PasteVerb = "FutureSpeed.Paste";

    /// <summary>Marker value so a half-written or foreign set of keys can be recognised.</summary>
    public const string OwnerValueName = "FutureSpeedIntegration";
    public const string OwnerValue = "1";

    private readonly IRegistryStore _store;

    public ExplorerIntegrationService(IRegistryStore store) => _store = store;

    private static string ClassesRoot(IntegrationScope scope)
        => scope == IntegrationScope.CurrentUser ? @"Software\Classes" : @"Software\Classes";

    /// <summary>The five keys the integration owns, in deletion order.</summary>
    public static IReadOnlyList<string> VerbKeys(IntegrationScope scope)
    {
        var root = ClassesRoot(scope);
        return new[]
        {
            $@"{root}\*\shell\{CopyVerb}",
            $@"{root}\*\shell\{MoveVerb}",
            $@"{root}\Directory\shell\{CopyVerb}",
            $@"{root}\Directory\shell\{MoveVerb}",
            $@"{root}\Directory\shell\{PasteVerb}",
            $@"{root}\Directory\Background\shell\{PasteVerb}"
        };
    }

    /// <summary>
    /// Everything that will be written, as data. The UI shows it before asking for
    /// consent, and the tests assert against it.
    /// </summary>
    public static IReadOnlyList<RegistryEntry> BuildPlan(
        string executablePath,
        IntegrationLabels labels,
        IntegrationScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(labels);

        var exe = executablePath.Trim('"');
        var root = ClassesRoot(scope);
        var entries = new List<RegistryEntry>();

        void AddVerb(string keyPath, string label, string arguments)
        {
            entries.Add(new RegistryEntry(keyPath, "MUIVerb", label));
            entries.Add(new RegistryEntry(keyPath, "Icon", $"\"{exe}\",0"));
            entries.Add(new RegistryEntry(keyPath, "MultiSelectModel", "Player"));
            entries.Add(new RegistryEntry(keyPath, OwnerValueName, OwnerValue));
            entries.Add(new RegistryEntry($@"{keyPath}\command", null, $"\"{exe}\" {arguments}"));
        }

        // Files and folders: copy / move the selection.
        AddVerb($@"{root}\*\shell\{CopyVerb}", labels.Copy, "--explorer-copy \"%1\"");
        AddVerb($@"{root}\*\shell\{MoveVerb}", labels.Move, "--explorer-move \"%1\"");
        AddVerb($@"{root}\Directory\shell\{CopyVerb}", labels.Copy, "--explorer-copy \"%1\"");
        AddVerb($@"{root}\Directory\shell\{MoveVerb}", labels.Move, "--explorer-move \"%1\"");

        // Paste into a folder (right-click on the folder, or on its empty background).
        AddVerb($@"{root}\Directory\shell\{PasteVerb}", labels.Paste, "--paste-to \"%1\" --compact");
        AddVerb($@"{root}\Directory\Background\shell\{PasteVerb}", labels.Paste, "--paste-to \"%V\" --compact");

        return entries;
    }

    public void Install(string executablePath, IntegrationLabels labels)
    {
        // Remove first so a rename or a reinstall cannot leave a stale command behind.
        Uninstall();

        foreach (var entry in BuildPlan(executablePath, labels, _store.Scope))
            _store.SetValue(entry.KeyPath, entry.ValueName, entry.Value);
    }

    public void Uninstall()
    {
        foreach (var key in VerbKeys(_store.Scope))
            _store.DeleteKeyTree(key);
    }

    public IntegrationStatus GetStatus(string? expectedExecutable = null)
    {
        var keys = VerbKeys(_store.Scope);
        var present = keys.Where(_store.KeyExists).ToList();

        if (present.Count == 0)
            return new IntegrationStatus(IntegrationState.NotInstalled, _store.Scope, null, present);

        var command = _store.GetValue($@"{keys[0]}\command", null);
        var registeredExe = ExtractExecutable(command);

        if (present.Count != keys.Count)
            return new IntegrationStatus(IntegrationState.Stale, _store.Scope, registeredExe, present);

        if (!string.IsNullOrWhiteSpace(expectedExecutable) &&
            !string.Equals(registeredExe, expectedExecutable.Trim('"'), StringComparison.OrdinalIgnoreCase))
            return new IntegrationStatus(IntegrationState.Stale, _store.Scope, registeredExe, present);

        return new IntegrationStatus(IntegrationState.Installed, _store.Scope, registeredExe, present);
    }

    /// <summary>Pulls the executable path out of a <c>"C:\...\app.exe" --args</c> command string.</summary>
    public static string? ExtractExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = command.Trim();
        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
