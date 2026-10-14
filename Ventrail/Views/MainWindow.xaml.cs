using System.Windows;

namespace Ventrail.Views;

/// <summary>
/// View only. No logic lives here (MVVM): the window gets its ViewModel from App.xaml.cs
/// through DataContext, and everything on screen comes from data binding.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
