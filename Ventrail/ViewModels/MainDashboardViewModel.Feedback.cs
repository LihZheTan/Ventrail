using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ventrail.Models;

namespace Ventrail.ViewModels;

/// <summary>
/// Week 5: everything that tells the user what is happening.
///  - Toast notifications (success / error / info), auto-hiding.
///  - A reusable in-app confirm dialog (firewall permission, restore, exit).
///  - Progress for long operations (opening a folder, the commit progress screen).
///  - Session statistics + activity log for the Session Summary page (Fig 3.7).
/// Kept in its own file (partial class) so the main ViewModel stays focused on version control.
/// </summary>
public partial class MainDashboardViewModel
{
    // ===================== Toasts =====================

    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastMessage = string.Empty;
    [ObservableProperty] private ToastKind _toastKind = ToastKind.Info;

    private CancellationTokenSource? _toastTimer;

    /// <summary>Shows a small notification in the corner that hides itself after a few seconds.</summary>
    public async void ShowToast(string message, ToastKind kind = ToastKind.Info, int seconds = 4)
    {
        _toastTimer?.Cancel();
        var timer = _toastTimer = new CancellationTokenSource();

        ToastMessage = message;
        ToastKind = kind;
        IsToastVisible = true;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(kind == ToastKind.Error ? seconds + 3 : seconds), timer.Token);
            IsToastVisible = false;
        }
        catch (TaskCanceledException)
        {
            // A newer toast replaced this one.
        }
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastTimer?.Cancel();
        IsToastVisible = false;
    }

    // ===================== Confirm dialog =====================

    [ObservableProperty] private bool _isDialogOpen;
    [ObservableProperty] private string _dialogTitle = string.Empty;
    [ObservableProperty] private string _dialogMessage = string.Empty;
    [ObservableProperty] private string _dialogConfirmText = "OK";
    [ObservableProperty] private string _dialogCancelText = "Cancel";
    [ObservableProperty] private string _dialogIcon = "";   // info

    private TaskCompletionSource<bool>? _dialogResult;

    /// <summary>
    /// Opens the in-app dialog and waits for the user's answer (true = confirm).
    /// Used like: if (!await AskAsync(...)) return;
    /// </summary>
    public Task<bool> AskAsync(string title, string message, string confirmText, string cancelText,
                               string icon = "")
    {
        _dialogResult?.TrySetResult(false);   // close any dialog that was still open
        _dialogResult = new TaskCompletionSource<bool>();

        DialogTitle = title;
        DialogMessage = message;
        DialogConfirmText = confirmText;
        DialogCancelText = cancelText;
        DialogIcon = icon;
        IsDialogOpen = true;
        return _dialogResult.Task;
    }

    [RelayCommand]
    private void ConfirmDialog() => CloseDialog(true);

    // Also bound to Esc, which closes the diff viewer (Week 7) when no dialog is open.
    [RelayCommand]
    private void CancelDialog()
    {
        if (IsDialogOpen) CloseDialog(false);
        else IsDiffOpen = false;
    }

    private void CloseDialog(bool result)
    {
        IsDialogOpen = false;
        _dialogResult?.TrySetResult(result);
    }

    // ===================== Progress =====================

    // Spinner overlay while a folder is being opened and scanned.
    [ObservableProperty] private bool _isLoadingRepository;

    // Full-screen "Committing Changes..." screen (proposal mockup, Fig 3.4).
    [ObservableProperty] private bool _isCommitting;
    [ObservableProperty] private double _commitProgress;           // 0-100
    [ObservableProperty] private string _commitProgressText = string.Empty;

    // ===================== Session summary =====================

    public ObservableCollection<ActivityEntry> ActivityLog { get; } = new();

    [ObservableProperty] private int _sessionCommits;
    [ObservableProperty] private int _sessionFilesSaved;
    [ObservableProperty] private int _sessionTransfers;          // real value from Week 12
    [ObservableProperty] private long _sessionBytesShared;       // real value from Week 12

    public string SessionSharedDisplay => SessionBytesShared switch
    {
        < 1024 * 1024 => $"{SessionBytesShared / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{SessionBytesShared / (1024.0 * 1024):F0} MB",
        _ => $"{SessionBytesShared / (1024.0 * 1024 * 1024):F1} GB"
    };

    partial void OnSessionBytesSharedChanged(long value) => OnPropertyChanged(nameof(SessionSharedDisplay));

    // Oldest first, like the mockup's "Session Activity Log".
    private void LogActivity(string text)
    {
        ActivityLog.Add(new ActivityEntry { Text = text });
        OnPropertyChanged(nameof(HasActivity));
    }

    public bool HasActivity => ActivityLog.Count > 0;

    /// <summary>Left-rail Exit: show the Session Summary first (Fig 3.7) instead of closing straight away.</summary>
    [RelayCommand]
    private void ShowSessionSummary() => CurrentPage = AppPage.Summary;

    /// <summary>"Continue Workflow" on the summary loops back to the start (Fig 3.7).</summary>
    [RelayCommand]
    private void ContinueWorkflow() => CurrentPage = HasRepo ? AppPage.Staging : AppPage.Welcome;

    /// <summary>"Exit Application": warn about ticked-but-unsaved files, then close.</summary>
    [RelayCommand]
    private async Task ExitApplicationAsync()
    {
        if (StagedCount > 0 && !await AskAsync(
                "Leave without saving?",
                $"{StagedCount} ticked file(s) have not been saved as a version yet. " +
                "They stay on your disk, but the version won't be created.",
                "Exit anyway", "Stay", ""))
            return;

        System.Windows.Application.Current?.Shutdown();
    }
}
