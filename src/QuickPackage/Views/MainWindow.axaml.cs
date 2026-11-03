using Avalonia.Controls;
using Avalonia.VisualTree;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainWindowViewModel _viewModel;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Core.FontStrategy.ApplyTextOptions(this);
        _viewModel = new MainWindowViewModel(this);
        DataContext = _viewModel;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_viewModel.IsBusy || _closeConfirmed)
            return;
        e.Cancel = true;
        var confirmed = await ConfirmAsync(
            Loc.T("Operation in Progress"),
            Loc.T("An operation is still running. Quit anyway?"),
            Loc.T("OK"), Loc.T("Cancel"));
        if (!confirmed)
            return;
        _closeConfirmed = true;
        _viewModel.CancelCurrentOperation();
        Close();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText, string cancelText)
    {
        var dialog = new MessageWindow(title, message, okText, cancelText);
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new MessageWindow(title, message, Loc.T("OK"), null);
        await dialog.ShowDialog<bool>(this);
    }

    public async Task<bool> ShowSettingsAsync(SettingsViewModel viewModel)
    {
        var dialog = new SettingsWindow { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task<bool> ShowProxyAsync(ProxyViewModel viewModel)
    {
        var dialog = new ProxyWindow { DataContext = viewModel };
        return await dialog.ShowDialog<bool>(this);
    }

    public async Task<List<Package>?> ShowUpdatesAsync(UpdateViewModel viewModel)
    {
        var dialog = new UpdateWindow { DataContext = viewModel };
        return await dialog.ShowDialog<List<Package>?>(this);
    }
}
