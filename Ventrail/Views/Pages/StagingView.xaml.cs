using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Ventrail.Views.Pages;

/// <summary>View only: content comes from data binding to MainDashboardViewModel (inherited DataContext).</summary>
public partial class StagingView : UserControl
{
    public StagingView()
    {
        InitializeComponent();
    }

    // Week 9: "Edit Manually" moves the keyboard focus into the commit box. Focus is a view-only
    // concern, so it lives here; the command (run right after this) fills in the text.
    private void EditManually_Click(object sender, RoutedEventArgs e)
    {
        CommitBox.Focus();
        Dispatcher.BeginInvoke(() => CommitBox.CaretIndex = CommitBox.Text.Length, DispatcherPriority.Input);
    }
}
