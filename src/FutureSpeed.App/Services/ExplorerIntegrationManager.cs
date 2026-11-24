using FutureSpeed.App.Localization;
using FutureSpeed.Core.Integration;
using Microsoft.Extensions.Logging;

namespace FutureSpeed.App.Services;

public interface IExplorerIntegrationManager
{
    IntegrationStatus GetStatus(IntegrationScope scope = IntegrationScope.CurrentUser);

    /// <summary>The exact registry values that would be written, for the consent dialog.</summary>
    IReadOnlyList<RegistryEntry> Preview(IntegrationScope scope = IntegrationScope.CurrentUser);

    bool Enable(IntegrationScope scope = IntegrationScope.CurrentUser);
    bool Disable(IntegrationScope scope = IntegrationScope.CurrentUser);

    /// <summary>Removes the keys from both hives; used by the uninstaller.</summary>
    void DisableEverywhere();

    string ExecutablePath { get; }
}

/// <summary>
/// Owns the Explorer verbs. Nothing here runs without an explicit user action: the
/// settings page asks first, and the uninstaller calls <see cref="DisableEverywhere"/>.
/// </summary>
public sealed class ExplorerIntegrationManager : IExplorerIntegrationManager
{
    private readonly ILocalizationService _localization;
    private readonly ILogger<ExplorerIntegrationManager> _logger;
    private readonly Func<IntegrationScope, IRegistryStore> _storeFactory;

    public ExplorerIntegrationManager(
        ILocalizationService localization,
        ILogger<ExplorerIntegrationManager> logger,
        Func<IntegrationScope, IRegistryStore>? storeFactory = null)
    {
        _localization = localization;
        _logger = logger;

        // The factory is injectable so the integration can be exercised against an
        // in-memory registry in tests, never the machine's real one.
        _storeFactory = storeFactory ?? (scope => new WindowsRegistryStore(scope));
    }

    public string ExecutablePath => Environment.ProcessPath ?? AppContext.BaseDirectory + "FutureSpeed.exe";

    private IntegrationLabels Labels => _localization.Language == "ar"
        ? IntegrationLabels.Arabic
        : IntegrationLabels.English;

    private ExplorerIntegrationService ServiceFor(IntegrationScope scope)
        => new(_storeFactory(scope));

    public IntegrationStatus GetStatus(IntegrationScope scope = IntegrationScope.CurrentUser)
    {
        try
        {
            return ServiceFor(scope).GetStatus(ExecutablePath);
        }
        catch (Exception ex)
        {
            // A registry failure must never take the app down; the feature simply reports off.
            _logger.LogWarning(ex, "Could not read the Explorer integration status.");
            return new IntegrationStatus(IntegrationState.NotInstalled, scope, null, Array.Empty<string>());
        }
    }

    public IReadOnlyList<RegistryEntry> Preview(IntegrationScope scope = IntegrationScope.CurrentUser)
        => ExplorerIntegrationService.BuildPlan(ExecutablePath, Labels, scope);

    public bool Enable(IntegrationScope scope = IntegrationScope.CurrentUser)
    {
        try
        {
            ServiceFor(scope).Install(ExecutablePath, Labels);
            _logger.LogInformation(
                "Explorer integration installed in {Scope} for {Executable}.", scope, ExecutablePath);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Explorer integration needs elevation for scope {Scope}.", scope);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explorer integration could not be installed.");
            return false;
        }
    }

    public bool Disable(IntegrationScope scope = IntegrationScope.CurrentUser)
    {
        try
        {
            ServiceFor(scope).Uninstall();
            _logger.LogInformation("Explorer integration removed from {Scope}.", scope);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Explorer integration could not be removed from {Scope}.", scope);
            return false;
        }
    }

    public void DisableEverywhere()
    {
        Disable(IntegrationScope.CurrentUser);

        // Machine-wide keys only exist when the user asked for them, and removing them
        // without rights simply fails and is logged.
        try
        {
            ServiceFor(IntegrationScope.AllUsers).Uninstall();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Machine-wide integration keys were not removed (likely absent or not elevated).");
        }
    }
}
