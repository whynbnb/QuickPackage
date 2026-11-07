using Avalonia.Controls;
using Avalonia.Input;
using QuickPackage.Core;

namespace QuickPackage.Views;

public partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
        Core.FontStrategy.ApplyTextOptions(this);
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
                Close(false);
        };
    }

    public MessageWindow(string title, string message, string okText, string? cancelText)
        : this(title, message, null, okText, cancelText)
    {
    }

    public MessageWindow(
        string title, string message, string? details, string okText, string? cancelText)
        : this()
    {
        Title = title;
        MessageText.Text = message;
        if (!string.IsNullOrEmpty(details))
        {
            DetailsBox.Text = details;
            DetailsBox.IsVisible = true;
        }
        OkButton.Content = okText;
        OkButton.Click += (_, _) => Close(true);
        if (cancelText is null)
        {
            CancelButton.IsVisible = false;
        }
        else
        {
            CancelButton.Content = cancelText;
            CancelButton.Click += (_, _) => Close(false);
        }
    }
}
