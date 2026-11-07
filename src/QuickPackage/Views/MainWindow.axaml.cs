using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainWindowViewModel _viewModel;
    private bool _closeConfirmed;
    private bool _logScrollQueued;

    public MainWindow()
    {
        InitializeComponent();
        Core.FontStrategy.ApplyTextOptions(this);
        _viewModel = new MainWindowViewModel(this);
        DataContext = _viewModel;
        ApplyContentSplitRatio();
        _viewModel.LogUpdated += ScrollLogToEnd;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.SelectedPackage))
        {
            // Dependency/back-forward navigation selects a row that may be
            // outside the current scroll position.
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.SelectedPackage is { } item)
                {
                    PackageList.ScrollIntoView(item);
                    DetailScroll.Offset = new Vector(0, 0);
                }
            }, DispatcherPriority.Background);
            return;
        }

        if (e.PropertyName is nameof(MainWindowViewModel.IsLogExpanded)
            or nameof(MainWindowViewModel.IsLogDetailed))
            ScrollLogToEnd();
    }

    private void ScrollLogToEnd()
    {
        if (!_viewModel.IsLogExpanded || _logScrollQueued)
            return;
        _logScrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _logScrollQueued = false;
            LogScroll.ScrollToEnd();
            UpdateLogContext();
        }, DispatcherPriority.Loaded);
    }

    private void OnLogDetailsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
            return;
        _viewModel.IsLogDetailed = checkBox.IsChecked == true;
        _viewModel.SaveLogPanelState();
    }

    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
        => UpdateLogContext();

    /// <summary>
    /// Keeps the sticky header on the command whose block occupies the top of
    /// the viewport, so long output never loses its command context.
    /// </summary>
    private void UpdateLogContext()
    {
        if (!_viewModel.IsLogExpanded || !_viewModel.IsLogDetailed)
        {
            _viewModel.LogContext = null;
            return;
        }

        var items = _viewModel.LogItems;
        if (items.Count == 0)
        {
            _viewModel.LogContext = null;
            return;
        }

        LogItemViewModel? candidate = null;
        var offset = LogScroll.Offset.Y + 1;
        for (var i = 0; i < items.Count; i++)
        {
            if (LogList.ContainerFromIndex(i) is not Control container)
                continue;
            var position = container.TranslatePoint(new Point(0, 0), LogList);
            if (position is null)
                continue;
            if (position.Value.Y <= offset)
                candidate = items[i];
            else
                break;
        }

        _viewModel.LogContext = candidate ?? items[0];
    }

    private void OnLogResizeDrag(object? sender, VectorEventArgs e)
        => _viewModel.ResizeLogPanel(-e.Vector.Y, Bounds.Height);

    private void OnLogResizeCompleted(object? sender, VectorEventArgs e)
        => _viewModel.SaveLogPanelState();

    /// <summary>Restores the saved 3:2-style star weights between list and detail.</summary>
    private void ApplyContentSplitRatio()
    {
        var ratio = _viewModel.ContentSplitRatio;
        ContentSplit.ColumnDefinitions[0].Width = new GridLength(ratio, GridUnitType.Star);
        ContentSplit.ColumnDefinitions[2].Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    private void OnContentSplitDragCompleted(object? sender, VectorEventArgs e)
    {
        SaveContentSplitRatio();
        // Convert the dragged pixel widths back to star weights so resizing
        // the window keeps the chosen proportion.
        ApplyContentSplitRatio();
    }

    private void SaveContentSplitRatio()
    {
        var left = ContentSplit.ColumnDefinitions[0].ActualWidth;
        var right = ContentSplit.ColumnDefinitions[2].ActualWidth;
        if (left + right > 0)
            _viewModel.SaveContentSplitRatio(left / (left + right));
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            Dispatcher.UIThread.Post(UpdateLogContext, DispatcherPriority.Loaded);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Persist the splitter positions even if a drag ended unexpectedly.
        _viewModel.SaveLogPanelState();
        SaveContentSplitRatio();

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

    public async Task ShowSnapshotsAsync(SnapshotsViewModel viewModel)
    {
        var dialog = new SnapshotWindow { DataContext = viewModel };
        await dialog.ShowDialog(this);
    }

    public async Task ShowCacheCleanupAsync(CacheViewModel viewModel)
    {
        var dialog = new CacheWindow { DataContext = viewModel };
        await dialog.ShowDialog(this);
    }

    public async Task<string?> AskPasswordAsync(
        string title, string message, Func<string, Task<bool>>? validate = null)
    {
        // The prompt may be requested from a modal cache/snapshot window, so
        // parent it to whichever window is currently active.
        Window owner = this;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            owner = desktop.Windows.LastOrDefault(window => window.IsActive) ?? this;

        var dialog = new PasswordWindow(title, message, validate);
        return await dialog.ShowDialog<string?>(owner);
    }
}
