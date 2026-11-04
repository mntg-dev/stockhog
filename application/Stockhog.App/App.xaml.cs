using System.Windows;
using System.Windows.Threading;
using Stockhog.Application;
using Stockhog.Application.Notifications;
using Stockhog.Infrastructure;
using Stockhog.Infrastructure.Alpaca;
using Stockhog.Infrastructure.Anthropic;
using Stockhog.Infrastructure.Lean;
using System.Globalization;
using System.Reflection;
using System.IO;
using Stockhog.App.Hosting;
using Stockhog.Infrastructure.Configuration;
using Stockhog.Infrastructure.Jobs;
using Stockhog.Presentation;
using Stockhog.Presentation.Common;
using Stockhog.Presentation.Instruments;
using Stockhog.Presentation.Localization;
using Stockhog.Presentation.Logging;
using Stockhog.Presentation.Settings;
using Stockhog.Presentation.Shell;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Stockhog.App;

/// <summary>
/// Composition root. The Generic Host provides configuration (appsettings, user secrets, environment variables),
/// DI, Serilog logging and hosted background services.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Resource key of the <see cref="InstrumentCatalog"/>: every instrument, for fields that download or trade.</summary>
    public const string SymbolSuggestionsKey = "SymbolSuggestions";

    /// <summary>Resource key of the <see cref="DownloadedSymbolCatalog"/>: symbols with stored data, for fields that run on it.</summary>
    public const string DownloadedSymbolSuggestionsKey = "DownloadedSymbolSuggestions";

    private IHost? _host;
    private SingleInstanceGuard? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything opens the database or talks to the broker: a second copy would trade the same account twice.
        _singleInstance = new SingleInstanceGuard();
        if (!_singleInstance.IsFirstInstance)
        {
            if (!SingleInstanceGuard.ActivateOtherInstance())
                MessageBox.Show("Stockhog is already running.", "Stockhog", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            // Data of the app's former name, EmTrading: copied before anything (logging, settings, the database) opens it.
            var copiedFolder = LegacyDataMigration.CopyAppDataFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            // User preferences are needed before the first window: the language applies to everything created afterwards.
            var settingsStore = new JsonUserSettingsStore(JsonUserSettingsStore.DefaultPath);
            var userSettings = settingsStore.Load();
            Loc.Instance.SetCulture(Loc.Choose(userSettings.Culture, CultureInfo.CurrentUICulture));

            // Nothing starts, the broker included, until the risk notice is accepted.
            if (TermsOfUse.NeedsAcceptance(userSettings))
            {
                var accepted = MessageBox.Show(Loc.Get("Terms_Text"), Loc.Get("Terms_Title"), MessageBoxButton.YesNo, MessageBoxImage.Information,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!accepted)
                {
                    Shutdown(0);
                    return;
                }

                userSettings = TermsOfUse.Accept(userSettings);
                settingsStore.Save(userSettings);
            }

            _host = CreateHost(e.Args, settingsStore, userSettings);
            var copiedDatabase = LegacyDataMigration.CopyDatabase(_host.Services.GetRequiredService<IOptions<StorageOptions>>().Value.DatabaseFile);
            if (copiedFolder || copiedDatabase)
                Logger?.LogInformation("Copied EmTrading data: app data folder {Folder}, database {Database}", copiedFolder, copiedDatabase);

            await _host.StartAsync();

            // Every SymbolBox finds its suggestions through one of these resources; views are created after this point.
            // Names of downloaded symbols come from the instrument list, so it loads first.
            var instruments = _host.Services.GetRequiredService<InstrumentCatalog>();
            await instruments.LoadAsync();
            Resources[SymbolSuggestionsKey] = instruments;
            var downloaded = _host.Services.GetRequiredService<DownloadedSymbolCatalog>();
            await downloaded.LoadAsync();
            Resources[DownloadedSymbolSuggestionsKey] = downloaded;

            MainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow.Show();

            // After the window is up, so a slow or missing network never delays the start.
            _ = _host.Services.GetRequiredService<UpdateBannerViewModel>().CheckAsync();
        }
        catch (Exception ex)
        {
            Logger?.LogCritical(ex, "Application failed to start");
            MessageBox.Show(ex.Message, "Stockhog failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            // Run off the UI thread so a hosted service awaiting on the dispatcher cannot deadlock shutdown.
            Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult();
            _host.Dispose();
        }

        Log.CloseAndFlush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static IHost CreateHost(string[] args, IUserSettingsStore settingsStore, UserSettings userSettings)
    {
        var settings = new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(App).Assembly.GetName().Name
        };
#if DEBUG
        // Development loads appsettings.Development.json and user secrets.
        settings.EnvironmentName ??= Environments.Development;
#endif

        var builder = Host.CreateApplicationBuilder(settings);

        builder.Services.AddSingleton(settingsStore);
        builder.Services.AddSingleton(userSettings);
        builder.Services.AddSingleton(CreateEnvironmentInfo);
        builder.Services.AddSingleton<IUserConfirmation, MessageBoxConfirmation>();
        builder.Services.AddSingleton<IExternalLinks, BrowserLinks>();
        builder.Services.AddSingleton<INotifier, WindowsNotifier>();

        builder.Services.AddSerilog((services, logger) => logger
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new LogFeedSink(services.GetRequiredService<LogFeed>())));

        // Clean Architecture composition: the app layer, shared infrastructure, one adapter per external system, the UI.
        builder.Services
            .AddApplicationServices()
            .AddInfrastructureServices(builder.Configuration)
            .AddAlpacaMarketData(builder.Configuration)
            .AddAlpacaTrading()
            .AddLeanEngine(builder.Configuration)
            .AddAnthropicAssistant(builder.Configuration)
            .AddPresentationServices();

        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    private static AppEnvironmentInfo CreateEnvironmentInfo(IServiceProvider services)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        var logFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockhog", "logs");
        return new AppEnvironmentInfo(
            storage.DataFolder,
            storage.ResultsFolder,
            storage.DatabaseFile,
            logFolder,
            services.GetRequiredService<IOptions<AlpacaOptions>>().Value.HasCredentials,
            services.GetRequiredService<IOptions<JobOptions>>().Value.MaxParallelJobs,
            AppVersion());
    }

    /// <summary>The version the release stamped in (from its tag), without the build metadata.</summary>
    private static string AppVersion()
    {
        var informational = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private ILogger<App>? Logger => _host?.Services.GetService<ILogger<App>>();

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception, terminating: {Terminating}", args.IsTerminating);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
