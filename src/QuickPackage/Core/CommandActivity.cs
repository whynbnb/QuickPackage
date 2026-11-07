namespace QuickPackage.Core;

/// <summary>An external command that has started.</summary>
public sealed record CommandStarted(long Id, string CommandLine, bool Operation);

/// <summary>A batch of output lines (separated by '\n') from a running command.</summary>
public sealed record CommandOutput(long Id, string Text);

public enum CommandEndReason
{
    Completed,
    TimedOut,
    Cancelled,
}

/// <summary>An external command that has finished.</summary>
public sealed record CommandFinished(
    long Id, string CommandLine, bool Operation, bool Ok, int ExitCode,
    CommandEndReason Reason, string? ExpectedNoteKey = null);

/// <summary>
/// Broadcasts external command lifecycle events so the activity panel can show
/// what QuickPackage is running and what the commands print.
/// </summary>
public static class CommandActivity
{
    private static long _lastId;

    public static event Action<CommandStarted>? Started;
    public static event Action<CommandOutput>? Output;
    public static event Action<CommandFinished>? Finished;

    internal static long NextId() => Interlocked.Increment(ref _lastId);

    internal static void RaiseStarted(CommandStarted activity) => Started?.Invoke(activity);

    internal static void RaiseOutput(CommandOutput activity) => Output?.Invoke(activity);

    internal static void RaiseFinished(CommandFinished activity) => Finished?.Invoke(activity);
}
