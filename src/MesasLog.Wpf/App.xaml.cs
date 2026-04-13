using System.IO;
using System.Windows;
using MesasLog.Application.DependencyInjection;
using MesasLog.Core;
using MesasLog.Infrastructure.Data;
using MesasLog.Infrastructure.Logging;
using MesasLog.Wpf.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MesasLog.Wpf;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        if (!SingleInstanceGuard.TryAcquire(out var msg))
        {
            MessageBox.Show(msg, AppBranding.MainWindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var asm = typeof(App).Assembly;

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory);
                BundledAppFiles.AddEmbeddedAppSettings(cfg, asm);
                cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureLogging((ctx, logging) =>
            {
                logging.ClearProviders();
                var opt = ctx.Configuration.GetSection(MesasLogOptions.SectionName).Get<MesasLogOptions>() ?? new();
                var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, opt.Logging.LogDirectory));
                var level = Enum.TryParse<LogLevel>(opt.Logging.MinimumLevel, true, out var l) ? l : LogLevel.Information;
                logging.AddProvider(new FileLoggerProvider(dir, level));
                logging.SetMinimumLevel(level);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.Configure<MesasLogOptions>(ctx.Configuration.GetSection(MesasLogOptions.SectionName));
                services.AddMesasLogServices();
                services.AddTransient<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        try
        {
            var schema = _host.Services.GetRequiredService<SchemaInitializer>();
            await schema.EnsureDatabaseExistsAsync();
            await schema.EnsureCoreSchemaAsync();
            await BundledAppFiles.ApplyMesasSnapshotAsync(schema);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Não foi possível preparar o banco: " + ex.Message,
                AppBranding.MainWindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var main = _host.Services.GetRequiredService<MainWindow>();
        main.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
            await _host.StopAsync();
        _host?.Dispose();
        base.OnExit(e);
    }
}
