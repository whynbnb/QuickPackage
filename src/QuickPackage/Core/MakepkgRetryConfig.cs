using System.Runtime.Versioning;
using System.Text;

namespace QuickPackage.Core;

/// <summary>
/// Builds a temporary makepkg configuration that makes curl retry partial
/// transfers. makepkg's stock http/https agents use <c>--retry</c>, but curl
/// treats errors such as (18) "end of response ... bytes missing" as
/// non-transient unless <c>--retry-all-errors</c> is set, so weak connections
/// give up after a single truncated response.
/// </summary>
public sealed class MakepkgRetryConfig : IDisposable
{
    private readonly string _directory;

    private MakepkgRetryConfig(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    public string Path { get; }

    public static MakepkgRetryConfig? Create(int retries)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        string? directory = null;
        try
        {
            var baseDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
                baseDirectory = System.IO.Path.GetTempPath();

            directory = System.IO.Path.Combine(
                baseDirectory, $"quickpackage-makepkg-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory, UnixFileMode.UserRead
                | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var source = Environment.GetEnvironmentVariable("MAKEPKG_CONF");
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                source = "/etc/makepkg.conf";
            if (!File.Exists(source))
            {
                TryDelete(directory);
                return null;
            }

            var content = File.ReadAllText(source);
            content += "\n" + DlAgents(retries);

            var path = System.IO.Path.Combine(directory, "makepkg.conf");
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new MakepkgRetryConfig(directory, path);
        }
        catch
        {
            TryDelete(directory);
            return null;
        }
    }

    public void Dispose() => TryDelete(_directory);

    [SupportedOSPlatform("linux")]
    private static string DlAgents(int retries)
    {
        var curl = CommandRunner.FindExecutable("curl") ?? "/usr/bin/curl";
        // Never go below the stock retry count; more retries for weak links.
        var count = Math.Clamp(retries + 3, 3, 20);
        var retry = $"--retry {count} --retry-all-errors --retry-delay 3 --connect-timeout 30";

        return $"""
            DLAGENTS=('file::{curl} -qgC - -o %o %u'
                      'ftp::{curl} -qgfC - --ftp-pasv {retry} -o %o %u'
                      'http::{curl} -qgb "" -fLC - {retry} -o %o %u'
                      'https::{curl} -qgb "" -fLC - {retry} -o %o %u'
                      'rsync::/usr/bin/rsync --no-motd -z %u %o'
                      'scp::/usr/bin/scp -C %u %o')
            """;
    }

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
