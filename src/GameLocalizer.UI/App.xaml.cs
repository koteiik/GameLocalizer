using System.Windows;
using System.IO;
using GameLocalizer.Core.Detection;
using GameLocalizer.Core.Interfaces;
using GameLocalizer.Core.Localization;
using GameLocalizer.Core.Models;
using GameLocalizer.Infrastructure.Database;
using GameLocalizer.Infrastructure.FileSystem;
using GameLocalizer.Infrastructure.GameDiscovery;
using GameLocalizer.Infrastructure.Logging;
using GameLocalizer.Infrastructure.TranslationProviders;
using GameLocalizer.Infrastructure.Update;
using GameLocalizer.UI.ViewModels;
using GameLocalizer.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace GameLocalizer.UI;

public partial class App : Application
{
    private ServiceProvider? services;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLocalizer");
        var collection = new ServiceCollection();
        collection.AddLogging(b => b.AddProvider(new FileLoggerProvider(Path.Combine(data, "logs"))));
        collection.AddSingleton<IGameDiscoveryService, SteamDiscoveryService>();
        collection.AddSingleton<IEngineDetector, EngineDetector>();
        foreach (var adapter in new ILocalizationAdapter[] { new JsonLocalizationAdapter(), new XmlLocalizationAdapter(), new CsvLocalizationAdapter(), new CsvLocalizationAdapter('\t'), new IniLocalizationAdapter(), new PoLocalizationAdapter(), new PlainTextLocalizationAdapter() }) collection.AddSingleton(adapter);
        collection.AddSingleton<ResourceScanner>(); collection.AddSingleton<BackupService>();
        collection.AddSingleton<ScanPipeline>(); collection.AddSingleton<ScanWorkspaceService>();
        collection.AddSingleton(new ScanResultRepository(Path.Combine(data, "scans", "scan-" + Guid.NewGuid().ToString("N") + ".db")));
        collection.AddSingleton<ITranslationProvider, MockTranslationProvider>();
        collection.AddSingleton<ITranslationMemoryService>(new TranslationMemoryService(Path.Combine(data, "memory.db")));
        collection.AddSingleton<TranslationService>(); collection.AddSingleton(new SettingsService(data)); collection.AddSingleton<UpdateService>();
        collection.AddSingleton<MainViewModel>();
        services = collection.BuildServiceProvider();
        services.GetRequiredService<ILogger<App>>().LogInformation("GameLocalizer {Version} startup", ApplicationVersion.Label);
        var vm = services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = vm }; MainWindow = window;
        var closing = false;
        window.Closing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true;
            if (vm.Busy) { vm.Cancel(); return; }
            try
            {
                await vm.ShutdownAsync();
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                closing = true; window.Close();
            }
            catch (Exception ex) { MessageBox.Show("Не удалось сохранить правки: " + ex.Message); }
        };
        window.Show(); vm.InitializeCommand.Execute(null);
    }
    protected override void OnExit(ExitEventArgs e) { services?.Dispose(); base.OnExit(e); }
}
