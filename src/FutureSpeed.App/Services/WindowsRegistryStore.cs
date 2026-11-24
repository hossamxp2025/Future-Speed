using FutureSpeed.Core.Integration;
using Microsoft.Win32;

namespace FutureSpeed.App.Services;

/// <summary>
/// The real registry behind <see cref="IRegistryStore"/>. Only the two class-registration
/// hives are ever touched, and only under the keys the integration owns.
/// </summary>
public sealed class WindowsRegistryStore : IRegistryStore
{
    public WindowsRegistryStore(IntegrationScope scope) => Scope = scope;

    public IntegrationScope Scope { get; }

    private RegistryKey Root => Scope == IntegrationScope.CurrentUser
        ? Registry.CurrentUser
        : Registry.LocalMachine;

    public void SetValue(string keyPath, string? valueName, string value)
    {
        using var key = Root.CreateSubKey(keyPath, writable: true)
                        ?? throw new InvalidOperationException($"Could not create registry key: {keyPath}");

        key.SetValue(valueName ?? string.Empty, value, RegistryValueKind.String);
    }

    public string? GetValue(string keyPath, string? valueName)
    {
        using var key = Root.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(valueName ?? string.Empty) as string;
    }

    public bool KeyExists(string keyPath)
    {
        using var key = Root.OpenSubKey(keyPath, writable: false);
        return key is not null;
    }

    public void DeleteKeyTree(string keyPath)
    {
        try
        {
            Root.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }
}
