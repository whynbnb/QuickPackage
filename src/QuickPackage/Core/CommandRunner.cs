using System.Diagnostics;
using System.Text;

namespace QuickPackage.Core;

/// <summary>Runs external commands with a merged stdout+stderr capture and a timeout.</summary>
public static class CommandRunner
{
    public sealed record Result(bool Ok, string Output);

    public static async Task<Result> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        int timeoutMilliseconds,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        CancellationToken cancellationToken = default)
    {
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
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (extraEnvironment is not null)
        {
            foreach (var (key, value) in extraEnvironment)
                startInfo.Environment[key] = value;
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
                return new Result(false, string.Empty);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMilliseconds);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                var partial = await SafeReadAsync(stdoutTask) + await SafeReadAsync(stderrTask);
                return new Result(false, partial);
            }

            var output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
            return new Result(process.ExitCode == 0, output);
        }
        catch (Exception exception)
        {
            return new Result(false, exception.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
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
