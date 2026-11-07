using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class PackageFilesWindow : Window
{
    public PackageFilesWindow()
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is PackageFilesViewModel viewModel)
            Title = viewModel.Title;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
