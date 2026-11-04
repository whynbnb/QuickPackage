namespace QuickPackage.ViewModels;

/// <summary>Dialog interactions implemented by the main window.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText);

    Task ShowInfoAsync(string title, string message);

    Task<bool> ShowSettingsAsync(SettingsViewModel viewModel);

    /// <summary>Returns the packages checked for update, or null when cancelled.</summary>
    Task<List<Core.Package>?> ShowUpdatesAsync(UpdateViewModel viewModel);
}
