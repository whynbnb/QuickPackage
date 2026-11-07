using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class CacheWindow : Window
{
    public CacheWindow()
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is CacheViewModel viewModel)
            Title = viewModel.Title;
    }

    private CacheViewModel? ViewModel => DataContext as CacheViewModel;

    private async void OnCleanUninstalled(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Clean uninstalled versions"),
                Loc.T("Remove cached packages that are no longer installed?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanPacmanAsync(PackageManager.CacheCleanMode.Uninstalled);
        }
    }

    private async void OnKeepLatest(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Keep only the latest version"),
                Loc.T("Remove older cached versions of installed packages?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanPacmanAsync(PackageManager.CacheCleanMode.KeepLatest);
        }
    }

    private async void OnCleanAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Clean all"),
                Loc.T("This removes every cached package, including versions still installed. Continue?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanPacmanAsync(PackageManager.CacheCleanMode.All);
        }
    }

    private async void OnCleanAur(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Clean AUR cache"),
                Loc.T("Remove cached AUR sources and built packages that are no longer needed?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanAurAsync(all: false);
        }
    }

    private async void OnCleanAurAll(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Clean all"),
                Loc.T("This removes all cached AUR sources and built packages. Continue?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanAurAsync(all: true);
        }
    }

    private async void OnCleanFlatpak(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        if (await ConfirmAsync(
                Loc.T("Remove unused runtimes"),
                Loc.T("Remove Flatpak runtimes that no installed application needs?"),
                Loc.T("OK"), Loc.T("Cancel")))
        {
            await viewModel.CleanFlatpakAsync();
        }
    }

    private async void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } viewModel)
            await viewModel.RefreshAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmAsync(string title, string message, string ok, string cancel)
    {
        var dialog = new MessageWindow(title, message, ok, cancel);
        return await dialog.ShowDialog<bool>(this);
    }
}
