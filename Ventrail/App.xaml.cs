using System.Linq;
using System.Windows;
using Ventrail.Services.Contracts;
using Ventrail.Services.Implementations;
using Ventrail.Services.Mock;
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
        IIntelligentStorageEngine storageEngine = new IntelligentStorageEngine();   // stub until Weeks 8-9
        INetworkNodeCoordinator network = new NetworkNodeCoordinator();             // stub until Weeks 10-12
        ISpecializedFileHandler fileHandler = new SpecializedFileHandler();         // optional feature

        var viewModel = new MainDashboardViewModel(repository, storageEngine, network, fileHandler, demoMode);
        var window = new MainWindow { DataContext = viewModel };
        window.Show();

        if (demoMode)
            _ = viewModel.InitializeDashboardAsync(@"C:\Demo\Thesis");
    }
}
