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

    /// <summary>Backends that should receive the proxy environment. Defaults to all.</summary>
    public HashSet<Backend> EnabledBackends { get; } =
    [
        Backend.Pacman, Backend.Aur, Backend.Flatpak, Backend.Cargo,
        Backend.Mise, Backend.Rustup, Backend.Fvm,
    ];

    public bool IsEnabledFor(Backend backend) => EnabledBackends.Contains(backend);

    public void SetEnabled(Backend backend, bool enabled)
    {
        if (enabled)
            EnabledBackends.Add(backend);
        else
            EnabledBackends.Remove(backend);
    }

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
        EnabledBackends.Clear();
        foreach (var backend in Enum.GetValues<Backend>())
        {
            if (config.GetBool("Proxy", "Enable" + backend, true))
                EnabledBackends.Add(backend);
        }
    }

    public void Save(Config config)
    {
        config.Set("Proxy", "Mode", (int)Mode);
        config.Set("Proxy", "Host", Host);
        config.Set("Proxy", "Port", Port);
        foreach (var backend in Enum.GetValues<Backend>())
            config.Set("Proxy", "Enable" + backend, IsEnabledFor(backend));
        config.Save();
    }

    /// <summary>Proxy variables understood by curl, reqwest and flatpak.</summary>
    private static readonly string[] ProxyVariables =
    [
        "ALL_PROXY", "all_proxy",
        "HTTP_PROXY", "http_proxy",
        "HTTPS_PROXY", "https_proxy",
    ];

    public Dictionary<string, string?> BuildEnvironment(Backend backend)
        => IsEnabledFor(backend) ? BuildEnvironment() : ClearedEnvironment();

    /// <summary>Environment overrides handed to child processes.</summary>
    public Dictionary<string, string?> BuildEnvironment()
    {
        var url = Url;
        if (url.Length == 0)
            return ClearedEnvironment();

        // socks5h resolves host names at the proxy. curl/reqwest/flatpak fail
        // with plain socks5 on systems that cannot resolve the target locally.
        var environmentUrl = Mode == ProxyMode.Socks5
            ? url.Replace("socks5://", "socks5h://", StringComparison.Ordinal)
            : url;

        var environment = new Dictionary<string, string?>
        {
            ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            ["no_proxy"] = "localhost,127.0.0.1,::1",
        };
        foreach (var key in ProxyVariables)
            environment[key] = environmentUrl;
        return environment;
    }

    /// <summary>
    /// Removes inherited proxy variables. Empty values are not equivalent for
    /// every tool (reqwest can hang on an empty ALL_PROXY), so the child
    /// process must not receive the variable at all.
    /// </summary>
    private static Dictionary<string, string?> ClearedEnvironment()
    {
        var environment = new Dictionary<string, string?>();
        foreach (var key in ProxyVariables)
            environment[key] = null;
        return environment;
    }
}
