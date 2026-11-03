using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        Core.FontStrategy.ApplyTextOptions(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is UpdateViewModel viewModel)
            Title = viewModel.Title;
    }

    private void OnUpdate(object? sender, RoutedEventArgs e)
    {
        Close((DataContext as UpdateViewModel)?.CheckedPackages);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
