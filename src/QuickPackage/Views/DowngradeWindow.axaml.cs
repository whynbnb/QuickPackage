using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;

namespace QuickPackage.Views;

public partial class DowngradeWindow : Window
{
    public DowngradeWindow()
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
