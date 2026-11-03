namespace QuickPackage.Core;

public enum ProxyMode
{
    None = 0,
    Http = 1,
    Socks5 = 2,
}

/// <summary>Proxy settings shared by the package tools and the HTTP client.</summary>
public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.None;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1080;

    public string Scheme => Mode == ProxyMode.Socks5 ? "socks5" : "http";

    public string Url => MakeUrl(Mode, Host, Port);

    public static string MakeUrl(ProxyMode mode, string host, int port)
        => mode == ProxyMode.None || string.IsNullOrWhiteSpace(host) || port <= 0
            ? string.Empty
            : $"{(mode == ProxyMode.Socks5 ? "socks5" : "http")}://{host}:{port}";

    public void Load(Config config)
    {
        Mode = (ProxyMode)config.GetInt("Proxy", "Mode", 0);
        Host = config.GetString("Proxy", "Host");
        Port = config.GetInt("Proxy", "Port", 1080);
    }

    public void Save(Config config)
    {
        config.Set("Proxy", "Mode", (int)Mode);
        config.Set("Proxy", "Host", Host);
        config.Set("Proxy", "Port", Port);
        config.Save();
    }

    /// <summary>
    /// Environment overrides handed to child processes. libcurl based tools
    /// (pacman, flatpak, cargo) also accept socks5:// in http_proxy.
    /// </summary>
    public Dictionary<string, string> BuildEnvironment()
    {
        var environment = new Dictionary<string, string>
        {
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            ["no_proxy"] = "localhost,127.0.0.1,::1",
        };
        var url = Url;
        if (url.Length == 0)
            return environment;

        environment["ALL_PROXY"] = url;
        environment["all_proxy"] = url;
        if (Mode == ProxyMode.Http)
        {
            foreach (var key in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY" })
                environment[key] = url;
        }
        else
        {
            // curl/libcurl-based tools honour the socks5:// scheme here.
            environment["http_proxy"] = url;
            environment["https_proxy"] = url;
        }
        return environment;
    }
}
