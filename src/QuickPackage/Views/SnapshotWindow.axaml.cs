using Avalonia.Controls;
using Avalonia.Interactivity;
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
}
