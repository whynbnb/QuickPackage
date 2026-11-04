using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickPackage.Core;

/// <summary>Runs external commands with a merged stdout+stderr capture and a timeout.</summary>
public static class CommandRunner
{
    public sealed record Result(bool Ok, string Output);

    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public static async Task<Result> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        int timeoutMilliseconds,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default,
        bool logOutput = false)
    {
        var argumentList = arguments as IReadOnlyList<string> ?? arguments.ToList();
        var commandLine = FormatCommand(executable, argumentList);
        var activityId = CommandActivity.NextId();
        CommandActivity.RaiseStarted(new CommandStarted(activityId, commandLine, logOutput));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in argumentList)
            startInfo.ArgumentList.Add(argument);
        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
                startInfo.Environment[key] = value;
        }

        var output = new StringBuilder();
        var gate = new object();
        var exitCode = -1;
        var ok = false;

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return new Result(false, string.Empty);

            var stdoutTask = PumpAsync(process.StandardOutput);
            var stderrTask = PumpAsync(process.StandardError);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                await SafeAwaitAsync(stdoutTask, stderrTask);
                return new Result(false, Snapshot());
            }

            await SafeAwaitAsync(stdoutTask, stderrTask);
            exitCode = process.ExitCode;
            ok = exitCode == 0;
            return new Result(ok, Snapshot());
        }
        catch (Exception exception)
        {
            if (logOutput)
                CommandActivity.RaiseOutput(new CommandOutput(activityId, Sanitize(exception.Message)));
            return new Result(false, exception.Message);
        }
        finally
        {
            process?.Dispose();
            CommandActivity.RaiseFinished(
                new CommandFinished(activityId, commandLine, logOutput, ok, exitCode));
        }

        // Read both streams concurrently: awaiting one stream fully would
        // deadlock as soon as the other pipe buffer fills up.
        async Task PumpAsync(StreamReader reader)
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
                if (line is null)
                    break;

                lock (gate)
                    output.Append(line).Append('\n');

                if (!logOutput)
                    continue;
                var clean = Sanitize(line);
                if (clean.Length > 0)
                    CommandActivity.RaiseOutput(new CommandOutput(activityId, clean));
            }
        }

        string Snapshot()
        {
            lock (gate)
                return output.ToString();
        }
    }

    /// <summary>Formats a command line for display, quoting arguments like a shell would need.</summary>
    public static string FormatCommand(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(executable);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            if (argument.Length == 0)
            {
                builder.Append("''");
                continue;
            }
            if (argument.IndexOfAny([' ', '\t', '\"', '\'', '\\', '$', '`', '&', '|', ';', '<', '>', '(', ')', '*', '?', '#', '~', '!']) < 0)
            {
                builder.Append(argument);
                continue;
            }
            builder.Append('\'').Append(argument.Replace("'", "'\\''")).Append('\'');
        }
        return builder.ToString();
    }

    /// <summary>Removes ANSI escapes and control characters that would corrupt the log view.</summary>
    private static string Sanitize(string line)
    {
        var value = AnsiEscape.Replace(line, string.Empty);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character == '\t' || !char.IsControl(character))
                builder.Append(character);
        }
        return builder.ToString().TrimEnd();
    }

    private static async Task SafeAwaitAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // The process disappeared or the stream was closed; partial output stands.
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have just exited.
        }
    }

    /// <summary>Finds an executable on PATH, like QStandardPaths::findExecutable.</summary>
    public static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static bool HasExecutable(string name) => FindExecutable(name) is not null;
}
