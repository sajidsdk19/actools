using AcAgent.Infrastructure;
using AcAgent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace AcAgent;

/// <summary>
/// WPF application entry point.
/// Builds the DI container and opens the main window.
/// </summary>
public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Catch any startup error and show it — prevents silent crash
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            MessageBox.Show(ex.ExceptionObject?.ToString(), "AcAgent — Fatal Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);

        try
        {
            // ── Configuration ────────────────────────────────────────────────────
            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables()
                .Build();

            var acRoot = configuration["ACTOOLS_ROOT"]
                ?? configuration["AcRoot"]
                ?? @"C:\Program Files (x86)\Steam\steamapps\common\assettocorsa";

            // ── DI Container ─────────────────────────────────────────────────────
            var sc = new ServiceCollection();

            sc.AddLogging(lb =>
            {
                lb.AddDebug();
                lb.SetMinimumLevel(LogLevel.Debug);
            });

            sc.AddSingleton<AcToolsIntegration>(sp =>
                new AcToolsIntegration(
                    acRoot,
                    sp.GetRequiredService<ILogger<AcToolsIntegration>>()));

            sc.AddSingleton<ReportingService>(sp =>
                new ReportingService(
                    Path.Combine(AppContext.BaseDirectory, "data"),
                    sp.GetRequiredService<ILogger<ReportingService>>()));

            sc.AddSingleton<SessionManager>();
            sc.AddSingleton<GameLauncherService>();
            sc.AddTransient<MainWindow>();

            Services = sc.BuildServiceProvider();

            // ── Initialise reporting (SQLite) ────────────────────────────────────
            var reporting = Services.GetRequiredService<ReportingService>();
            reporting.InitialiseAsync().GetAwaiter().GetResult();

            // ── Show main window ─────────────────────────────────────────────────
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Startup failed:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                "AcAgent — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }


    protected override async void OnExit(ExitEventArgs e)
    {
        if (Services is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        base.OnExit(e);
    }
}
