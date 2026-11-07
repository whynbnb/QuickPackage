using Avalonia.Controls;
using Avalonia.Interactivity;
using QuickPackage.Core;
using QuickPackage.ViewModels;

namespace QuickPackage.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is HistoryViewModel viewModel)
            Title = viewModel.Title;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
