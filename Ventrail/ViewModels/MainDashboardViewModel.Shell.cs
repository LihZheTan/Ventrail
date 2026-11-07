using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ventrail.Models;
using Ventrail.Themes;

namespace Ventrail.ViewModels;

/// <summary>
/// Redesign (GitHub Desktop-style shell): menu bar and toolbar commands, the light/dark theme
/// choice, the summary + description commit fields and the diff shown beside the changed files.
/// Still UI-free: App.xaml.cs applies the theme when CurrentTheme changes.
/// </summary>
public partial class MainDashboardViewModel
{
    // ---------- theme ----------

    [ObservableProperty] private AppTheme _currentTheme = AppTheme.System;

    [RelayCommand]
    private void SetTheme(AppTheme theme) => CurrentTheme = theme;

    // ---------- commit box: summary (CommitMessage) + optional description ----------

    [ObservableProperty] private string _commitDescription = string.Empty;

    // "Commit to main" on the commit button.
    public string CommitButtonText => string.IsNullOrEmpty(BranchName) ? "Commit" : $"Commit to {BranchName}";
    partial void OnBranchNameChanged(string value) => OnPropertyChanged(nameof(CommitButtonText));

    // "Last scanned at 14:05" under "Scan for changes" in the toolbar.
    [ObservableProperty] private string _lastScanText = "Not scanned yet";

    // ---------- diff beside the changed files ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInlineDiffLines))]
    private FileDiff? _inlineDiff;

    [ObservableProperty] private bool _isInlineDiffLoading;

    public bool HasInlineDiffLines => InlineDiff?.CanShowLines == true;

    private int _diffRequest;

    private async Task LoadInlineDiffAsync(FileAsset? file)
    {
        int request = ++_diffRequest;   // a newer selection wins over a slower, older one
        if (file is null)
        {
            InlineDiff = null;
            return;
        }
        IsInlineDiffLoading = true;
        try
        {
            string path = file.FilePath;
            var diff = await Background(() => _repo.GetFileDiff(path));
            if (request == _diffRequest) InlineDiff = diff;
        }
        catch (Exception ex)
        {
            if (request == _diffRequest) InlineDiff = new FileDiff { FilePath = file.FilePath, Message = $"Could not compare this file: {ex.Message}" };
        }
        finally
        {
            if (request == _diffRequest) IsInlineDiffLoading = false;
        }
    }

    // ---------- menu commands ----------

    [RelayCommand]
    private void NavigateToWelcome() => CurrentPage = AppPage.Welcome;

    [RelayCommand(CanExecute = nameof(CanUseRepo))]
    private void ShowInExplorer()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CurrentDirectory}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast($"Could not open File Explorer: {ex.Message}", ToastKind.Error); }
    }

    // Help > User guide: the copy shipped next to Ventrail.exe, or docs/ in the source folder.
    [RelayCommand]
    private void OpenUserGuide()
    {
        string? guide = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && guide is null; dir = dir.Parent)
        {
            foreach (string candidate in new[] { Path.Combine(dir.FullName, "USER_GUIDE.md"), Path.Combine(dir.FullName, "docs", "USER_GUIDE.md") })
                if (File.Exists(candidate)) { guide = candidate; break; }
        }
        if (guide is null)
        {
            ShowToast("The user guide (USER_GUIDE.md) was not found next to Ventrail.", ToastKind.Error);
            return;
        }
        try { Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true }); }
        catch (Exception)
        {
            // No app is set to open .md files: show it in Notepad instead.
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{guide}\"") { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private Task ShowAboutAsync() => AskAsync(
        "About Ventrail",
        $"{AppVersion}\n\nVersion control for non-technical users, with an offline AI storage advisor and " +
        "peer-to-peer sharing on your local network.\n\nCapstone Project 2, Sunway University, by Tan Lih Zhe.\n" +
        "Built with .NET 8, WPF, LibGit2Sharp and ML.NET.",
        "OK", string.Empty, "");
}
