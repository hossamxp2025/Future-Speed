using System.IO;
using System.Windows;
using System.Windows.Threading;
using FutureSpeed.App.Localization;
using FutureSpeed.App.Services;
using FutureSpeed.App.ViewModels;
using FutureSpeed.App.Views;
using FutureSpeed.Core.Abstractions;
using FutureSpeed.Core.Integration;
using FutureSpeed.Core.Services;
using FutureSpeed.Core.Transfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace FutureSpeed.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private Mutex? _singleInstance;
    private TransferRequestServer? _ipcServer;

    public static IServiceProvider Services =>
        (Current as App)?._services ?? throw new InvalidOperationException("Services are not ready yet.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);

        // Settings must exist before logging, because the log directory is configurable.
        // This one instance therefore starts without a logger and is then reused by DI.
        var bootstrapSettings = new SettingsService(NullLogger<SettingsService>.Instance);

        ConfigureSerilog(bootstrapSettings.LogsDirectory);

        _services = BuildServices(bootstrapSettings).BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Future Speed starting (elevated: {Elevated}).",
            _services.GetRequiredService<IElevationService>().IsElevated);

        HookGlobalExceptionHandlers();

        var settings = _services.GetRequiredService<ISettingsService>();
        var localization = _services.GetRequiredService<ILocalizationService>();
        localization.SetLanguage(settings.Current.Language);
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

        // ---- one-shot maintenance commands (used by the installer) -------------
        if (options.Action is CommandLineAction.RegisterExplorer or CommandLineAction.UnregisterExplorer)
        {
            RunIntegrationCommand(options);
            Shutdown();
            return;
        }

        // ---- "Copy/Move with Future Speed": stage the selection, no window ------
        // The verb has no destination yet: it marks the files, exactly like Explorer's
        // own Copy/Cut, and the paste verb does the work.
        if (options.Action is CommandLineAction.Copy or CommandLineAction.Move &&
            string.IsNullOrWhiteSpace(options.Destination))
        {
            StageSelection(options);
            Shutdown();
            return;
        }

        // ---- single instance ---------------------------------------------------
        if (!TryBecomeHost(options))
            return;

        StartIpcServer();

        // ---- a request from Explorer opens the compact window only -------------
        if (options.IsTransfer)
        {
            var handled = HandleStartupTransfer(options);
            if (handled) return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_services is null) return;

        var existing = Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing is not null)
        {
            existing.Show();
            existing.Activate();
            return;
        }

        var shell = _services.GetRequiredService<MainWindow>();
        shell.DataContext = _services.GetRequiredService<MainViewModel>();
        MainWindow = shell;
        shell.Show();
    }

    /// <summary>
    /// Returns false when another instance already owns the channel: the request is sent
    /// there and this process exits, so one engine owns each job.
    /// </summary>
    private bool TryBecomeHost(CommandLineOptions options)
    {
        // The name must be identical in every process: string.GetHashCode() is randomised
        // per process in .NET, so it cannot be used here.
        _singleInstance = new Mutex(initiallyOwned: true, InstanceNaming.MutexName(), out var createdNew);

        if (createdNew) return true;

        var request = BuildRequest(options);
        var sent = TransferRequestClient.TrySendAsync(request).GetAwaiter().GetResult();

        _logger?.LogInformation("Another instance is running; request forwarded: {Sent}.", sent);

        if (!sent)
        {
            // The mutex exists but nobody answers (a crashed instance): carry on alone.
            return true;
        }

        Shutdown();
        return false;
    }


    private TransferRequest BuildRequest(CommandLineOptions options)
    {
        if (!options.IsTransfer)
            return new TransferRequest { ActivateMainWindow = true };

        return new TransferRequest
        {
            Mode = options.Mode,
            Sources = options.Sources.ToList(),
            Destination = options.Destination ?? string.Empty,
            Compact = options.Compact,
            FromClipboard = options.Action == CommandLineAction.PasteTo
        };
    }

    private void StartIpcServer()
    {
        if (_services is null) return;

        _ipcServer = new TransferRequestServer(logger: _services.GetRequiredService<ILogger<App>>());
        _ipcServer.RequestReceived += (_, args) =>
            Dispatcher.BeginInvoke(() => HandleRequest(args.Request));

        _ipcServer.Start();
    }

    /// <summary>Handles a request that arrived over IPC from a second launch.</summary>
    private void HandleRequest(TransferRequest request)
    {
        if (_services is null) return;

        if (request.ActivateMainWindow)
        {
            ShowMainWindow();
            return;
        }

        var coordinator = _services.GetRequiredService<ITransferRequestCoordinator>();

        var result = request.FromClipboard && request.Sources.Count == 0
            ? coordinator.PasteInto(request.Destination)
            : coordinator.Submit(request);

        if (!result.Succeeded)
        {
            Report(result);
            return;
        }

        OpenCompactWindow(result.Job!);
    }

    /// <summary>Starts the job described on the command line. Returns true when a window was shown.</summary>
    private bool HandleStartupTransfer(CommandLineOptions options)
    {
        if (_services is null) return false;

        var coordinator = _services.GetRequiredService<ITransferRequestCoordinator>();
        var dialogs = _services.GetRequiredService<IDialogService>();

        var result = options.Action == CommandLineAction.PasteTo
            ? coordinator.PasteInto(options.Destination ?? string.Empty)
            : coordinator.Submit(BuildRequest(options));

        if (!result.Succeeded)
        {
            Report(result);

            // An ignored clipboard is not a reason to pop the main window open.
            if (result.Informational)
            {
                Shutdown();
                return true;
            }

            // Nothing to show: fall back to the normal window rather than exiting silently.
            return false;
        }

        if (!options.Compact && !_services.GetRequiredService<ISettingsService>().Current.PreferCompactWindow)
        {
            ShowMainWindow();
            return true;
        }

        OpenCompactWindow(result.Job!);
        return true;
    }

    /// <summary>An ignored clipboard is information, a rejected request is an error.</summary>
    private void Report(SubmitResult result)
    {
        if (_services is null) return;

        var dialogs = _services.GetRequiredService<IDialogService>();
        var message = result.Error ?? string.Empty;

        if (result.Informational) dialogs.Info(message);
        else dialogs.Error(message);
    }

    private void OpenCompactWindow(QueuedJob job)
    {
        if (_services is null) return;

        var existing = Windows.OfType<TransferCompactWindow>()
            .FirstOrDefault(w => w.JobId == job.Job.Id);

        if (existing is not null)
        {
            existing.Show();
            existing.Activate();
            return;
        }

        var window = _services.GetRequiredService<TransferCompactWindow>();
        window.Attach(job);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Puts the Explorer selection on the clipboard with the right drop effect, so either
    /// "Paste with Future Speed" or Windows' own paste can act on it.
    /// </summary>
    private void StageSelection(CommandLineOptions options)
    {
        if (_services is null) return;

        var clipboard = _services.GetRequiredService<IClipboardFileReader>();
        var localization = _services.GetRequiredService<ILocalizationService>();
        var notifications = _services.GetRequiredService<INotificationService>();

        var operation = options.Action == CommandLineAction.Move
            ? ClipboardOperation.Move
            : ClipboardOperation.Copy;

        if (clipboard.Write(options.Sources, operation))
        {
            _logger?.LogInformation(
                "Staged {Count} item(s) on the clipboard for {Operation}.", options.Sources.Count, operation);

            notifications.NotifyInfo(localization.Format("Integration_Staged", options.Sources.Count));
        }
        else
        {
            _services.GetRequiredService<IDialogService>().Error(localization["Integration_Failed"]);
        }
    }

    private void RunIntegrationCommand(CommandLineOptions options)
    {
        if (_services is null) return;

        var manager = _services.GetRequiredService<IExplorerIntegrationManager>();
        var scope = options.AllUsers ? IntegrationScope.AllUsers : IntegrationScope.CurrentUser;

        if (options.Action == CommandLineAction.RegisterExplorer)
            manager.Enable(scope);
        else
            manager.DisableEverywhere();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ipcServer is not null)
                _ipcServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));

            if (_services is not null)
            {
                _services.GetRequiredService<IDriveWatcher>().Stop();

                // Cancelling in-flight work writes a final checkpoint, so the next launch
                // can offer to resume instead of starting over.
                var queue = _services.GetRequiredService<ITransferQueue>();
                queue.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));

                (_services.GetRequiredService<INotificationService>() as IDisposable)?.Dispose();

                // The container holds IAsyncDisposable singletons, so it must be disposed
                // asynchronously; a plain Dispose() throws for those.
                _services.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown cleanup failed.");
        }
        finally
        {
            try
            {
                _singleInstance?.ReleaseMutex();
                _singleInstance?.Dispose();
            }
            catch
            {
                // The mutex was never owned by this process.
            }

            Log.CloseAndFlush();
            base.OnExit(e);
        }
    }

    private static void ConfigureSerilog(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(logsDirectory, "futurespeed-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                shared: true)
            .CreateLogger();
    }

    private static IServiceCollection BuildServices(SettingsService settings)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });

        // ---- app services ---------------------------------------------------
        services.AddSingleton<ISettingsService>(settings);
        services.AddSingleton<ILocalizationService>(LocalizationService.Instance);
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IDemoDataService, DemoDataService>();
        services.AddSingleton<IDriveWatcher, DriveWatcher>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IClipboardFileReader, WpfClipboardFileReader>();
        services.AddSingleton<IExplorerIntegrationManager, ExplorerIntegrationManager>();
        services.AddSingleton<ITransferRequestCoordinator, TransferRequestCoordinator>();

        // ---- engine ---------------------------------------------------------
        services.AddSingleton<IDriveService>(sp =>
            new DriveService(sp.GetRequiredService<ILogger<DriveService>>()));

        services.AddSingleton<IFileSystemScanner>(sp =>
            new FileSystemScanner(sp.GetRequiredService<ILogger<FileSystemScanner>>()));

        services.AddSingleton<ISpaceValidator>(sp =>
            new SpaceValidator(sp.GetRequiredService<IDriveService>()));

        services.AddSingleton<IHashService>(_ => new Sha256HashService());

        services.AddSingleton<IConflictResolver>(sp =>
            new DialogConflictResolver(sp.GetRequiredService<ILocalizationService>()));

        services.AddSingleton<IJobStateStore>(sp =>
            new JsonJobStateStore(
                sp.GetRequiredService<ISettingsService>().StateDirectory,
                sp.GetRequiredService<ILogger<JsonJobStateStore>>()));

        services.AddSingleton<IReportService>(_ => new ReportService());
        services.AddSingleton<ISyncService>(sp => new SyncService(sp.GetRequiredService<ILogger<SyncService>>()));
        services.AddSingleton<IBackupService>(_ => new BackupService());
        services.AddSingleton<IDiskImagingProvider>(_ => new UnavailableDiskImagingProvider());

        services.AddSingleton<ITransferEngine>(sp => new TransferEngine(
            sp.GetRequiredService<IFileSystemScanner>(),
            sp.GetRequiredService<ISpaceValidator>(),
            sp.GetRequiredService<IDriveService>(),
            sp.GetRequiredService<IHashService>(),
            sp.GetRequiredService<IConflictResolver>(),
            sp.GetRequiredService<IJobStateStore>(),
            sp.GetRequiredService<ILogger<TransferEngine>>()));

        services.AddSingleton<ITransferQueue>(sp => new TransferQueue(
            sp.GetRequiredService<ITransferEngine>(),
            sp.GetRequiredService<ILogger<TransferQueue>>()));

        // ---- view models ----------------------------------------------------
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DrivesViewModel>();
        services.AddSingleton<NewTransferViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<BackupViewModel>();
        services.AddSingleton<SyncViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HelpViewModel>();

        // ---- views ----------------------------------------------------------
        services.AddSingleton<MainWindow>();

        // One compact window (and view model) per transfer, so several Explorer jobs
        // can each have their own window.
        services.AddTransient<CompactTransferViewModel>();
        services.AddTransient<TransferCompactWindow>();
        services.AddSingleton<Func<MainWindow>>(sp => () =>
        {
            var window = sp.GetRequiredService<MainWindow>();
            window.DataContext = sp.GetRequiredService<MainViewModel>();
            return window;
        });

        return services;
    }

    private void HookGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                _logger?.LogCritical(ex, "Unhandled domain exception.");
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception.");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception.");

        try
        {
            var localization = _services?.GetService<ILocalizationService>();
            var message = localization?["Error_Unexpected"] ?? e.Exception.Message;
            MessageBox.Show(
                $"{message}\n\n{e.Exception.Message}",
                localization?["Error_Title"] ?? "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Never fail inside the failure handler.
        }

        // The app stays usable: a failed operation must not take the window down.
        e.Handled = true;
    }
}
