using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using QuickPackage.Core;

namespace QuickPackage.Views;

public partial class PasswordWindow : Window
{
    private readonly Func<string, Task<bool>>? _validate;
    private bool _busy;

    public PasswordWindow()
        : this(string.Empty, string.Empty, null)
    {
    }

    public PasswordWindow(
        string title, string message, Func<string, Task<bool>>? validate = null)
    {
        InitializeComponent();
        FontStrategy.ApplyTextOptions(this);
        _validate = validate;
        Title = title;
        MessageText.Text = message;
        OkButton.Content = Loc.T("OK");
        CancelButton.Content = Loc.T("Cancel");
        RevealCheck.Content = Loc.T("Show password");
        RevealCheck.IsCheckedChanged += (_, _) =>
            PasswordInput.RevealPassword = RevealCheck.IsChecked == true;
        // Tell the input method this is a password so composing states are
        // not used for the field.
        TextInputOptions.SetContentType(PasswordInput, TextInputContentType.Password);
        // Focus as soon as the field enters the visual tree so the first
        // keystroke is not swallowed while the window is being mapped.
        PasswordInput.AttachedToVisualTree += (_, _) => PasswordInput.Focus();
        Opened += (_, _) =>
        {
            Activate();
            PasswordInput.Focus();
        };
    }

    private async void OnOk(object? sender, RoutedEventArgs e) => await SubmitAsync();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SubmitAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(null);
        }
    }

    private async Task SubmitAsync()
    {
        if (_busy)
            return;

        var password = PasswordInput.Text ?? string.Empty;
        if (_validate is null)
        {
            Close(password);
            return;
        }

        if (password.Length == 0)
        {
            ShowError(Loc.T("Please enter your password."));
            return;
        }

        _busy = true;
        OkButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PasswordInput.IsEnabled = false;
        ErrorText.IsVisible = false;
        var previousText = OkButton.Content;
        OkButton.Content = Loc.T("Verifying…");

        bool ok;
        try
        {
            ok = await _validate(password);
        }
        catch
        {
            ok = false;
        }
        finally
        {
            _busy = false;
            OkButton.Content = previousText;
            OkButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            PasswordInput.IsEnabled = true;
        }

        if (!ok)
        {
            // Keep the text so a dropped first keystroke (typed before the
            // window gained focus) can be fixed instead of starting over.
            ShowError(Loc.T("Incorrect password. Please try again."));
            PasswordInput.SelectAll();
            PasswordInput.Focus();
            return;
        }

        Close(password);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
