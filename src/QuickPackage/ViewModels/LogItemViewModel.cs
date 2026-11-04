using System.Text;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickPackage.Core;

namespace QuickPackage.ViewModels;

public enum LogItemState
{
    Running,
    QueryDone,
    Success,
    Failure,
    Info,
}

/// <summary>
/// One command (or informational message) in the activity log. Output lines are
/// kept per command, so concurrently running commands never interleave.
/// </summary>
public sealed partial class LogItemViewModel : ObservableObject
{
    private const int MaxLines = 300;

    private static readonly IBrush CommandBrush = new SolidColorBrush(Color.Parse("#4C8DFF"));
    private static readonly IBrush SubtleBrush = new SolidColorBrush(Color.Parse("#8B949E"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E5534B"));

    private readonly StringBuilder _output = new();
    private int _exitCode;
    private CommandEndReason _endReason = CommandEndReason.Completed;
    private bool _truncated;
    private bool _showDetails = true;

    private LogItemViewModel(long id, string header, bool operation, bool info)
    {
        Id = id;
        Header = header;
        Operation = operation;
        IsInfo = info;
        _state = info ? LogItemState.Info : LogItemState.Running;
    }

    public static LogItemViewModel CreateCommand(long id, string header, bool operation)
        => new(id, header, operation, info: false);

    public static LogItemViewModel CreateInfo(string message)
        => new(0, message, operation: false, info: true);

    public long Id { get; }

    public string Header { get; }

    public bool Operation { get; }

    public bool IsCommand => !IsInfo;

    public bool IsInfo { get; }

    public string OutputText => _truncated
        ? Loc.T("… earlier output omitted …") + "\n" + _output
        : _output.ToString();

    public bool HasOutput => _output.Length > 0;

    /// <summary>Global "show details" switch, mirrored onto every block.</summary>
    public bool ShowDetails
    {
        get => _showDetails;
        set
        {
            if (_showDetails == value)
                return;
            _showDetails = value;
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(OutputVisible));
        }
    }

    public bool OutputVisible => HasOutput && ShowDetails;

    [ObservableProperty]
    private LogItemState _state;

    public bool IsRunning => State == LogItemState.Running;

    private bool HasFailedExit => IsCommand && State switch
    {
        LogItemState.Failure => true,
        LogItemState.QueryDone => _endReason != CommandEndReason.Completed || _exitCode != 0,
        _ => false,
    };

    public string StatusText
    {
        get
        {
            if (State == LogItemState.Running)
                return Loc.T("Running…");
            if (State == LogItemState.Success)
                return "\u2713";
            if (_endReason is CommandEndReason.TimedOut or CommandEndReason.Cancelled)
            {
                var reason = Loc.T(_endReason == CommandEndReason.TimedOut ? "Timed out" : "Cancelled");
                return (HasFailedExit ? "\u2717 " : "\u00B7 ") + reason;
            }
            return State switch
            {
                LogItemState.Failure => "\u2717 " + Loc.T("exit %1", _exitCode),
                LogItemState.QueryDone => (HasFailedExit ? "\u2717 " : "\u00B7 ") + Loc.T("exit %1", _exitCode),
                _ => string.Empty,
            };
        }
    }

    public IBrush? HeaderBrush => State switch
    {
        LogItemState.Info => SubtleBrush,
        LogItemState.QueryDone => HasFailedExit ? ErrorBrush : SubtleBrush,
        LogItemState.Failure => ErrorBrush,
        _ => CommandBrush,
    };

    public IBrush? StatusBrush => State switch
    {
        LogItemState.Success => SuccessBrush,
        LogItemState.Failure => ErrorBrush,
        LogItemState.Running => SubtleBrush,
        LogItemState.QueryDone => HasFailedExit ? ErrorBrush : SubtleBrush,
        _ => null,
    };

    /// <summary>Appends one or more lines (separated by '\n') in a single UI refresh.</summary>
    public void Append(string text)
    {
        if (text.Length == 0)
            return;

        var hadOutput = _output.Length > 0;
        if (hadOutput)
            _output.Append('\n');
        _output.Append(text);
        TrimToMaxLines();

        OnPropertyChanged(nameof(OutputText));
        if (!hadOutput)
        {
            OnPropertyChanged(nameof(HasOutput));
            OnPropertyChanged(nameof(OutputVisible));
        }
    }

    public void Finish(bool ok, int exitCode, CommandEndReason reason)
    {
        _exitCode = exitCode;
        _endReason = reason;
        State = Operation
            ? (ok ? LogItemState.Success : LogItemState.Failure)
            : LogItemState.QueryDone;
    }

    /// <summary>Re-evaluates localized status text after a language switch.</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(OutputText));
    }

    public string ToText()
    {
        var header = Header;
        var status = StatusText;
        if (status.Length > 0)
            header += "  " + status;
        return _output.Length == 0 ? header : header + Environment.NewLine + OutputText;
    }

    partial void OnStateChanged(LogItemState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HeaderBrush));
        OnPropertyChanged(nameof(StatusBrush));
    }

    private void TrimToMaxLines()
    {
        var text = _output.ToString();
        var newlines = 0;
        foreach (var character in text)
        {
            if (character == '\n')
                ++newlines;
        }
        if (newlines < MaxLines)
            return;

        _truncated = true;
        // Drop whole leading lines until only MaxLines remain.
        var skip = newlines - MaxLines + 1;
        var index = -1;
        while (skip-- > 0)
            index = text.IndexOf('\n', index + 1);
        _output.Clear().Append(text[(index + 1)..]);
    }
}
