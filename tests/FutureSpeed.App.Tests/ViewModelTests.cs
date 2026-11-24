using System.IO;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.App.ViewModels;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Models;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using Xunit;

namespace FutureSpeed.App.Tests;

public sealed class MultiSelectTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeSettingsService _settings;
    private readonly TransferQueue _queue;

    public MultiSelectTests()
    {
        _settings = new FakeSettingsService(Path.Combine(_tree.Root, "appdata"));

        var drives = new DriveService();
        _queue = new TransferQueue(new TransferEngine(
            new FileSystemScanner(),
            new SpaceValidator(drives),
            drives,
            new Sha256HashService(),
            new PolicyConflictResolver(ConflictPolicy.Skip)));
    }

    private NewTransferViewModel CreateTransferViewModel()
    {
        var drives = new DriveService();
        return new NewTransferViewModel(
            new FileSystemScanner(),
            new SpaceValidator(drives),
            _queue,
            _settings,
            _dialogs,
            new ElevationService(TestLogger.For<ElevationService>()),
            new NavigationService(),
            LocalizationService.Instance,
            TestLogger.For<NewTransferViewModel>());
    }

    private BackupViewModel CreateBackupViewModel()
        => new(
            new BackupService(),
            _queue,
            _settings,
            _dialogs,
            new UnavailableDiskImagingProvider(),
            LocalizationService.Instance,
            TestLogger.For<BackupViewModel>());

    [Fact]
    public void Add_folder_accepts_several_folders_at_once()
    {
        var a = _tree.Folder("one");
        var b = _tree.Folder("two");
        var c = _tree.Folder("three");

        var viewModel = CreateTransferViewModel();
        _dialogs.FoldersPicks.Enqueue(new[] { a, b, c });

        viewModel.AddFolderCommand.Execute(null);

        Assert.Equal(3, viewModel.Sources.Count);
    }

    [Fact]
    public void Add_folder_twice_does_not_duplicate_a_folder()
    {
        var a = _tree.Folder("one");
        var b = _tree.Folder("two");

        var viewModel = CreateTransferViewModel();
        _dialogs.FoldersPicks.Enqueue(new[] { a, b });
        _dialogs.FoldersPicks.Enqueue(new[] { a, b.ToUpperInvariant() });

        viewModel.AddFolderCommand.Execute(null);
        viewModel.AddFolderCommand.Execute(null);

        Assert.Equal(2, viewModel.Sources.Count);
    }

    [Fact]
    public void Add_files_accepts_several_files_at_once()
    {
        var one = _tree.File("1.bin");
        var two = _tree.File("2.bin");

        var viewModel = CreateTransferViewModel();
        _dialogs.FilePicks.Enqueue(new[] { one, two });

        viewModel.AddFilesCommand.Execute(null);

        Assert.Equal(2, viewModel.Sources.Count);
    }

    [Fact]
    public void Cancelling_the_folder_picker_adds_nothing()
    {
        var viewModel = CreateTransferViewModel();
        _dialogs.FoldersPicks.Enqueue(null);

        viewModel.AddFolderCommand.Execute(null);

        Assert.Empty(viewModel.Sources);
    }

    [Fact]
    public void Dropped_paths_are_added_and_de_duplicated()
    {
        var a = _tree.Folder("one");
        var file = _tree.File("x.bin");

        var viewModel = CreateTransferViewModel();
        viewModel.AddPaths(new[] { a, file, a, Path.Combine(_tree.Root, "missing") });

        Assert.Equal(2, viewModel.Sources.Count);
    }

    [Fact]
    public void Backup_accepts_several_folders_without_duplicates()
    {
        var a = _tree.Folder("one");
        var b = _tree.Folder("two");

        var viewModel = CreateBackupViewModel();
        _dialogs.FoldersPicks.Enqueue(new[] { a, b, a });

        viewModel.AddFolderCommand.Execute(null);

        Assert.Equal(2, viewModel.Sources.Count);
    }

    public void Dispose()
    {
        _queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tree.Dispose();
    }
}

public sealed class IntegrationSettingsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeSettingsService _settings;
    private readonly FakeExplorerIntegrationManager _integration = new();
    private readonly TransferQueue _queue;

    public IntegrationSettingsTests()
    {
        _settings = new FakeSettingsService(Path.Combine(_tree.Root, "appdata"));

        var drives = new DriveService();
        _queue = new TransferQueue(new TransferEngine(
            new FileSystemScanner(),
            new SpaceValidator(drives),
            drives,
            new Sha256HashService(),
            new PolicyConflictResolver(ConflictPolicy.Skip)));
    }

    private SettingsViewModel CreateViewModel()
        => new(
            _settings,
            new ThemeService(),
            LocalizationService.Instance,
            _dialogs,
            new DemoDataService(_settings),
            _queue,
            _integration,
            TestLogger.For<SettingsViewModel>());

    [Fact]
    public void Nothing_is_written_to_the_registry_without_consent()
    {
        _dialogs.ConfirmAnswer = false;
        var viewModel = CreateViewModel();

        viewModel.EnableIntegrationCommand.Execute(null);

        Assert.Equal(0, _integration.EnableCalls);
        Assert.False(_settings.Current.ExplorerIntegrationEnabled);
    }

    [Fact]
    public void The_consent_dialog_lists_the_keys_that_will_be_added()
    {
        _dialogs.ConfirmAnswer = false;
        var viewModel = CreateViewModel();

        viewModel.EnableIntegrationCommand.Execute(null);

        var message = Assert.Single(_dialogs.ConfirmMessages);
        Assert.Contains(@"Software\Classes", message);
        Assert.Contains("FutureSpeed.Paste", message);
    }

    [Fact]
    public void Accepting_installs_the_verbs_and_remembers_the_choice()
    {
        _dialogs.ConfirmAnswer = true;
        var viewModel = CreateViewModel();

        viewModel.EnableIntegrationCommand.Execute(null);

        Assert.Equal(1, _integration.EnableCalls);
        Assert.True(_settings.Current.ExplorerIntegrationEnabled);
        Assert.True(viewModel.IntegrationInstalled);
    }

    [Fact]
    public void Disabling_removes_the_verbs_and_clears_the_setting()
    {
        _dialogs.ConfirmAnswer = true;
        var viewModel = CreateViewModel();

        viewModel.EnableIntegrationCommand.Execute(null);
        viewModel.DisableIntegrationCommand.Execute(null);

        Assert.Equal(1, _integration.DisableCalls);
        Assert.False(_settings.Current.ExplorerIntegrationEnabled);
        Assert.False(viewModel.IntegrationInstalled);
    }

    [Fact]
    public void A_failed_installation_is_reported_and_not_remembered()
    {
        _dialogs.ConfirmAnswer = true;
        _integration.EnableSucceeds = false;

        var viewModel = CreateViewModel();
        viewModel.EnableIntegrationCommand.Execute(null);

        Assert.False(_settings.Current.ExplorerIntegrationEnabled);
        Assert.False(viewModel.IntegrationInstalled);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.StatusMessage));
    }

    [Fact]
    public void The_compact_window_preference_is_saved()
    {
        var viewModel = CreateViewModel();
        viewModel.PreferCompactWindow = false;

        viewModel.SaveCommand.Execute(null);

        Assert.False(_settings.Current.PreferCompactWindow);
    }

    public void Dispose()
    {
        _queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _tree.Dispose();
    }
}

public sealed class ExplorerIntegrationManagerTests
{
    private static ExplorerIntegrationManager Create(FakeRegistryStore store)
        => new(
            LocalizationService.Instance,
            TestLogger.For<ExplorerIntegrationManager>(),
            _ => store);

    [Fact]
    public void Enabling_writes_the_verbs_and_disabling_removes_them_all()
    {
        var store = new FakeRegistryStore(IntegrationScope.CurrentUser);
        var manager = Create(store);

        Assert.True(manager.Enable());
        Assert.NotEmpty(store.AllKeys);
        Assert.True(manager.GetStatus().IsInstalled);

        Assert.True(manager.Disable());
        Assert.Empty(store.AllKeys);
        Assert.False(manager.GetStatus().IsInstalled);
    }

    [Fact]
    public void A_registry_failure_is_reported_without_throwing()
    {
        var store = new FakeRegistryStore(IntegrationScope.AllUsers) { FailWrites = true };
        var manager = Create(store);

        // The app must stay usable when the integration cannot be installed.
        Assert.False(manager.Enable(IntegrationScope.AllUsers));
        Assert.False(manager.GetStatus(IntegrationScope.AllUsers).IsInstalled);
    }

    [Fact]
    public void DisableEverywhere_is_safe_when_nothing_is_installed()
    {
        var store = new FakeRegistryStore(IntegrationScope.CurrentUser);
        var manager = Create(store);

        manager.DisableEverywhere();

        Assert.Empty(store.AllKeys);
    }
}
