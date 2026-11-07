using System.Linq;
using System.Windows;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Mock;
using Ventrail.Themes;
using Ventrail.ViewModels;
using Ventrail.Views;

namespace Ventrail;

/// <summary>
/// Composition root: the ONE place where concrete classes are chosen and wired together.
/// Everything else (ViewModel, View) only knows about interfaces, so swapping a stub for the
/// real service in later weeks means changing a single line here.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "--demo" (the "Ventrail (Demo data)" launch profile) uses fake data: handy for
        // building the UI, testing, and showing the supervisor without a real repository.
        bool demoMode = e.Args.Contains("--demo");

        IRepositoryManager repository = demoMode ? new MockRepositoryManager() : new RepositoryManager();
        IIntelligentStorageEngine storageEngine = new IntelligentStorageEngine();   // ML.NET advisor (Week 8)

        // Week 8: loading ML.NET and the model takes a few seconds the first time, so start it in
        // the background now. By the time a folder is opened the advisor is ready. Never throws.
        _ = System.Threading.Tasks.Task.Run(() => storageEngine.IsModelLoaded);
        INetworkNodeCoordinator network = demoMode
            ? new MockNetworkNodeCoordinator()                                        // demo: never touches the firewall
            : new NetworkNodeCoordinator();                                           // Week 10: firewall + listening
        ISpecializedFileHandler fileHandler = new SpecializedFileHandler();         // optional feature
        IRecentRepositoryStore recentStore = demoMode
            ? new InMemoryRecentRepositoryStore()                                    // demo: never touches disk
            : new JsonRecentRepositoryStore();

        var viewModel = new MainDashboardViewModel(repository, storageEngine, network, fileHandler, recentStore, demoMode);

        // Redesign: menus and drop-downs open left-aligned under their header (like GitHub Desktop),
        // even on PCs where Windows' "handedness" setting would open them to the left.
        // (Read it once first: WPF fills its cache on the first read, which would undo the change.)
        _ = SystemParameters.MenuDropAlignment;
        typeof(SystemParameters).GetField("_menuDropAlignment",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?
            .SetValue(null, false);

        // Redesign: light / dark theme, remembered between sessions (View > Theme in the menu).
        viewModel.CurrentTheme = ThemeManager.Load();
        ThemeManager.Apply(viewModel.CurrentTheme, save: false);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainDashboardViewModel.CurrentTheme))
                ThemeManager.Apply(viewModel.CurrentTheme);
        };

        var window = new MainWindow { DataContext = viewModel };
        ThemeManager.SetTitleBar(window, ThemeManager.IsDark);
        window.Show();

        if (demoMode)
            _ = viewModel.InitializeDashboardAsync(@"C:\Demo\Thesis");
    }
}
