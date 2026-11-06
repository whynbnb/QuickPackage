using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using QuickPackage.Core;

namespace QuickPackage.Views;

public partial class PasswordWindow : Window
{
    public PasswordWindow()
        : this(string.Empty, string.Empty)
    {
    }

    public PasswordWindow(string title, string message)
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
        Title = title;
        MessageText.Text = message;
        OkButton.Content = Loc.T("OK");
        CancelButton.Content = Loc.T("Cancel");
        Opened += (_, _) => PasswordInput.Focus();
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(PasswordInput.Text);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Close(PasswordInput.Text);
        else if (e.Key == Key.Escape)
            Close(null);
    }
}
