using Avalonia.Media;

namespace QuickPackage.ViewModels;

public enum LogEntryKind
{
    Command,
    Output,
    Info,
    Success,
    Error,
}

/// <summary>One line in the activity log panel.</summary>
public sealed class LogEntryViewModel
{
    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#8B949E"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E5534B"));

    public LogEntryViewModel(LogEntryKind kind, string text)
    {
        Kind = kind;
        Text = text;
        Foreground = kind switch
        {
            LogEntryKind.Command => CommandBrush,
            LogEntryKind.Info => InfoBrush,
            LogEntryKind.Success => SuccessBrush,
            LogEntryKind.Error => ErrorBrush,
            _ => null,
        };
    }

    public LogEntryKind Kind { get; }

    public string Text { get; }

    /// <summary>Null means "inherit the theme foreground" (plain command output).</summary>
    public IBrush? Foreground { get; }

    /// <summary>Output lines keep the theme's normal foreground instead of a custom brush.</summary>
    public bool IsPlain => Kind == LogEntryKind.Output;

    public FontWeight FontWeight => Kind == LogEntryKind.Command ? FontWeight.SemiBold : FontWeight.Normal;
}
