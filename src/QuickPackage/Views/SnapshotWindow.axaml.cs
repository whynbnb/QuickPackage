using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class SnapshotWindow : Window
{
    public SnapshotWindow()
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is SnapshotsViewModel viewModel)
            Title = viewModel.Title;
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel viewModel)
            return;
        var count = viewModel.Items.Count(item => item.IsSelected);
        if (count == 0)
            return;
        var dialog = new MessageWindow(
            Loc.T("Delete snapshots"),
            Loc.Tn("Delete the selected snapshot?", "Delete the selected %1 snapshots?", count, count),
            Loc.T("OK"),
            Loc.T("Cancel"));
        if (await dialog.ShowDialog<bool>(this))
            viewModel.DeleteSelected();
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel viewModel)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Export snapshots"),
            SuggestedFileName = $"quickpackage-snapshots-{DateTime.Now:yyyyMMdd}.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
            ],
        });
        var path = file?.TryGetLocalPath();
        if (path is null)
            return;
        try
        {
            await viewModel.ExportSelectedAsync(path);
        }
        catch (Exception exception)
        {
            await ShowInfoAsync(Loc.T("Export failed"), exception.Message);
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel viewModel)
            return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Import snapshots"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON") { Patterns = ["*.json"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null)
            return;
        try
        {
            await viewModel.ImportAsync(path);
        }
        catch (Exception exception)
        {
            await ShowInfoAsync(Loc.T("Import failed"), exception.Message);
        }
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SnapshotsViewModel viewModel)
            return;
        var confirm = new MessageWindow(
            Loc.T("Restore missing packages"),
            Loc.T("Install packages from the newest selected snapshot that are missing now?"),
            Loc.T("OK"),
            Loc.T("Cancel"));
        if (!await confirm.ShowDialog<bool>(this))
            return;
        var count = await viewModel.RestoreMissingAsync();
        if (count > 0)
        {
            await ShowInfoAsync(
                Loc.T("Restore requested"),
                Loc.T("Installation of %1 packages was requested. Check the activity log for progress.", count));
        }
    }

    private async Task ShowInfoAsync(string title, string message)
        => await new MessageWindow(title, message, Loc.T("OK"), null).ShowDialog(this);
}
