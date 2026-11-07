using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using QuickPackage.Core;

namespace QuickPackage;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // AUR helpers run sudo with -A and call us back through SUDO_ASKPASS
        // so the password prompt never blocks on the launching terminal.
        if (args.Length >= 2 && args[0] == "--askpass")
        {
            RunAskpass(args[1]);
            return;
        }

        // The language is resolved before any UI string is produced.
        var config = Config.Load();
        var language = config.GetString("General", "Language", "system");
        Loc.SetLanguage(language == "system" ? Loc.DetectSystemLanguage() : language);

        // Give text shaping an explicit language so CJK glyph variants are
        // selected deterministically (Simplified Chinese uses the SC forms).
        var culture = CultureInfo.GetCultureInfo(Loc.IsChinese ? "zh-CN" : "en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Prints the password supplied by the running app for sudo -A.</summary>
    private static void RunAskpass(string socketPath)
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var stream = new NetworkStream(socket, ownsSocket: false);

            using var writer = new StreamWriter(
                stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 256, leaveOpen: true)
            {
                AutoFlush = true,
            };
            writer.WriteLine("password");

            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                bufferSize: 256, leaveOpen: true);
            var password = reader.ReadLine();
            if (!string.IsNullOrEmpty(password))
            {
                Console.Out.Write(password);
                Console.Out.Write('\n');
                Console.Out.Flush();
            }
        }
        catch
        {
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            // Keep the Wayland app id in sync with the installed desktop entry
            // so compositors can associate the window with its icon/metadata.
            .With(new WaylandPlatformOptions { AppId = "org.quickpackage.QuickPackage" })
            .UsePlatformDetect()
            // Prefer the native Wayland backend when a compositor is reachable
            // and fall back to the X11 backend otherwise (including XWayland).
            .UseWaylandWithFallback()
            .With(FontStrategy.CreateOptions())
            .LogToTrace();
}
