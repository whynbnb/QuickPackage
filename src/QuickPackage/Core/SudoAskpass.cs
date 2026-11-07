using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace QuickPackage.Core;

/// <summary>
/// Serves sudo passwords to AUR helpers (paru/yay) through a private Unix
/// socket. The helpers run sudo with a small wrapper that adds <c>-A</c> and
/// points <c>SUDO_ASKPASS</c> back at QuickPackage, so a password prompt is
/// shown by the app instead of blocking on the launching terminal.
/// </summary>
public sealed class SudoAskpass : IAsyncDisposable
{
    private readonly string _directory;
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptLoop;
    private readonly Func<string, Task<bool>>? _validatePassword;
    private string? _password;

    private SudoAskpass(
        string directory,
        Socket listener,
        Func<Func<string, Task<bool>>?, Task<string?>> passwordProvider,
        string? initialPassword,
        Func<string, Task<bool>>? validatePassword)
    {
        _directory = directory;
        _listener = listener;
        _validatePassword = validatePassword;
        _password = initialPassword;
        _acceptLoop = AcceptLoopAsync(passwordProvider, _cancellation.Token);
    }

    /// <summary>The executable to pass to the helper's <c>--sudo</c> option.</summary>
    public string SudoPath => Path.Combine(_directory, "sudo");

    /// <summary>
    /// Creates the helper files and listener. Returns null when the app has no
    /// way to ask for a password or the runtime directory is unavailable.
    /// </summary>
    public static SudoAskpass? TryCreate(
        Func<Func<string, Task<bool>>?, Task<string?>>? passwordProvider,
        string? initialPassword = null,
        Func<string, Task<bool>>? validatePassword = null)
    {
        if (passwordProvider is null)
            return null;
        // sudo, paru and yay are Linux-only, and so are the socket helper.
        if (!OperatingSystem.IsLinux())
            return null;

        string? directory = null;
        Socket? listener = null;
        try
        {
            var baseDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
                baseDirectory = Path.GetTempPath();

            directory = Path.Combine(
                baseDirectory, $"quickpackage-askpass-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead
                | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var socketPath = Path.Combine(directory, "askpass.sock");
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(8);

            var askpassScript = Path.Combine(directory, "askpass");
            WriteExecutable(askpassScript,
                "#!/bin/sh\n" +
                $"exec {BuildHelperInvocation(socketPath)} \"$@\"\n");

            var sudoWrapper = Path.Combine(directory, "sudo");
            WriteExecutable(sudoWrapper,
                "#!/bin/sh\n" +
                $"SUDO_ASKPASS={Quote(askpassScript)} " +
                $"exec {Quote(CommandRunner.FindExecutable("sudo") ?? "sudo")} -A \"$@\"\n");

            return new SudoAskpass(
                directory, listener, passwordProvider, initialPassword, validatePassword);
        }
        catch
        {
            listener?.Dispose();
            TryDelete(directory);
            return null;
        }
    }

    private async Task AcceptLoopAsync(
        Func<Func<string, Task<bool>>?, Task<string?>> passwordProvider,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;
            try
            {
                client = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = new NetworkStream(client, ownsSocket: true);
                client = null;

                // The helper announces itself before we show a prompt so a
                // connection that closes early never leaves a dialog behind.
                using var reader = new StreamReader(
                    stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 256, leaveOpen: true);
                var request = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (request is null)
                    continue;

                var password = _password;
                if (string.IsNullOrEmpty(password))
                {
                    password = await passwordProvider(_validatePassword).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(password))
                        _password = password;
                }

                var response = string.IsNullOrEmpty(password) ? "\n" : password + "\n";
                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A helper that disconnects early must not stop the server.
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        try
        {
            _listener.Close();
        }
        catch
        {
            // Already closed.
        }

        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // The loop was cancelled.
        }

        _listener.Dispose();
        _cancellation.Dispose();
        TryDelete(_directory);
    }

    private static string BuildHelperInvocation(string socketPath)
    {
        var processPath = Environment.ProcessPath;
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        var isDotnetHost = processPath is not null
            && Path.GetFileNameWithoutExtension(processPath)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (processPath is not null && !isDotnetHost)
            return $"{Quote(processPath)} --askpass {Quote(socketPath)}";
        if (processPath is not null && !string.IsNullOrEmpty(assemblyPath))
            return $"{Quote(processPath)} {Quote(assemblyPath)} --askpass {Quote(socketPath)}";

        return $"QuickPackage --askpass {Quote(socketPath)}";
    }

    [SupportedOSPlatform("linux")]
    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static void TryDelete(string? directory)
    {
        if (directory is null)
            return;
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
