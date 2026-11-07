using System.ComponentModel;
using System.Windows;
using Ventrail.Themes;
using Ventrail.ViewModels;

namespace Ventrail.Views;

/// <summary>
/// View only (MVVM): the window gets its ViewModel from App.xaml.cs through DataContext, and
/// everything on screen comes from data binding. The few handlers here are purely visual.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is INotifyPropertyChanged vm)
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainDashboardViewModel.CurrentTheme)) UpdateThemeIcon();
                };
            UpdateThemeIcon();
        };
    }

    // The toolbar's sun/moon button flips between light and dark (View > Theme has "Match Windows").
    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainDashboardViewModel vm)
            vm.CurrentTheme = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
    }

    // Shows where a click takes you: a sun while dark, a moon while light.
    private void UpdateThemeIcon() =>
        ThemeIcon.Text = ThemeManager.IsDark ? "" : "";

    // Choosing something in the repository drop-down closes it.
    private void ClosePopup_Click(object sender, RoutedEventArgs e) => RepoToggle.IsChecked = false;
}
