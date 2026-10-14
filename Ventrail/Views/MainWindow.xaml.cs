using System.Windows;
using Ventrail.Services.Implementations;
using Ventrail.ViewModels;

namespace Ventrail
{
    /// <summary>
    /// View only: no logic lives here (MVVM). It just connects the window to its ViewModel.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainDashboardViewModel(new RepositoryManager());
        }
    }
}
