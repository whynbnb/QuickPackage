using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class ProxyWindow : Window
{
    public ProxyWindow()
    {
        InitializeComponent();
        Title = Loc.T("Proxy Settings");
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProxyViewModel viewModel)
            viewModel.Apply();
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
