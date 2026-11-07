namespace QuickPackage.ViewModels;

/// <summary>Dialog interactions implemented by the main window.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText);

    Task ShowInfoAsync(string title, string message);

    Task<bool> ShowSettingsAsync(SettingsViewModel viewModel);

    Task ShowSnapshotsAsync(SnapshotsViewModel viewModel);

    Task ShowCacheCleanupAsync(CacheViewModel viewModel);

    Task ShowDiagnosticsAsync(DiagnosticsViewModel viewModel);

    Task<string?> AskPasswordAsync(
        string title, string message, Func<string, Task<bool>>? validate = null);
}
