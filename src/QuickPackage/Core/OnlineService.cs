using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace QuickPackage.Core;

/// <summary>Web lookups that honour the app's proxy settings.</summary>
public sealed class OnlineService
{
    private ProxySettings _proxy;

    public OnlineService(ProxySettings proxy) => _proxy = proxy;

    public void SetProxy(ProxySettings proxy) => _proxy = proxy;

    private HttpClient CreateClient(Backend backend, TimeSpan timeout)
    {
        var handler = new HttpClientHandler();
        var url = _proxy.Url;
        if (url.Length > 0 && _proxy.IsEnabledFor(backend))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(url);
        }
        else
        {
            handler.UseProxy = false;
        }
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuickPackage/1.0 (package manager)");
        return client;
    }

    /// <summary>Searches the AUR RPC index by name and description.</summary>
    public async Task<List<Package>> SearchAurAsync(
        string query, CancellationToken cancellationToken)
    {
        var results = new List<Package>();
        var text = query.Trim();
        if (text.Length == 0)
            return results;

        using var client = CreateClient(Backend.Aur, TimeSpan.FromSeconds(20));
        var url = "https://aur.archlinux.org/rpc/v5/search/"
            + Uri.EscapeDataString(text) + "?by=name-desc";
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return results;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var entry in array.EnumerateArray())
        {
            var name = GetString(entry, "Name");
            if (name.Length == 0)
                continue;
            var outOfDate = entry.TryGetProperty("OutOfDate", out var outOfDateValue)
                && outOfDateValue.ValueKind == JsonValueKind.Number
                ? outOfDateValue.GetInt64()
                : 0;
            results.Add(new Package
            {
                Name = name,
                PackageId = name,
                Version = GetString(entry, "Version"),
                Description = GetString(entry, "Description"),
                Origin = "AUR",
                Url = GetString(entry, "URL"),
                Backend = Backend.Aur,
                Installed = false,
                RemoteSearch = true,
                Maintainer = GetString(entry, "Maintainer"),
                Votes = entry.TryGetProperty("NumVotes", out var votes) ? votes.GetInt32() : 0,
                Popularity = entry.TryGetProperty("Popularity", out var popularity)
                    ? popularity.GetDouble()
                    : 0,
                OutOfDate = outOfDate > 0,
            });
        }
        return results;
    }

    /// <summary>Searches crates.io by name and description.</summary>
    public async Task<List<Package>> SearchCratesAsync(
        string query, CancellationToken cancellationToken)
    {
        var results = new List<Package>();
        var text = query.Trim();
        if (text.Length == 0)
            return results;

        using var client = CreateClient(Backend.Cargo, TimeSpan.FromSeconds(20));
        var url = "https://crates.io/api/v1/crates?per_page=25&q=" + Uri.EscapeDataString(text);
        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return results;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("crates", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var entry in array.EnumerateArray())
        {
            var name = GetString(entry, "name");
            if (name.Length == 0)
                continue;
            results.Add(new Package
            {
                Name = name,
                PackageId = name,
                Version = GetString(entry, "max_version"),
                Description = GetString(entry, "description"),
                Origin = "crates.io",
                Backend = Backend.Cargo,
                Installed = false,
                RemoteSearch = true,
                Url = "https://crates.io/crates/" + Uri.EscapeDataString(name),
            });
        }
        return results;
    }

    public sealed record NewsItem(string Title, DateTimeOffset? Date, string Summary, string Url);

    public async Task<List<NewsItem>> GetArchNewsAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient(Backend.Pacman, TimeSpan.FromSeconds(20));
        var xml = await client.GetStringAsync(
            "https://archlinux.org/feeds/news/", cancellationToken).ConfigureAwait(false);
        var document = XDocument.Parse(xml);
        var items = new List<NewsItem>();
        foreach (var item in document.Descendants("item").Take(20))
        {
            var title = item.Element("title")?.Value.Trim() ?? string.Empty;
            var link = item.Element("link")?.Value.Trim() ?? string.Empty;
            var description = item.Element("description")?.Value ?? string.Empty;
            var dateText = item.Element("pubDate")?.Value;
            DateTimeOffset.TryParse(dateText, out var date);
            items.Add(new NewsItem(
                title,
                string.IsNullOrEmpty(dateText) ? null : date,
                StripHtml(description),
                link));
        }
        return items;
    }

    public sealed record MirrorInfo(
        string Url,
        string Country,
        string LastSync,
        double Completion,
        double Delay,
        double Score);

    public async Task<List<MirrorInfo>> GetMirrorStatusAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient(Backend.Pacman, TimeSpan.FromSeconds(30));
        var json = await client.GetStringAsync(
            "https://archlinux.org/mirrors/status/json/", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var mirrors = new List<MirrorInfo>();
        if (!document.RootElement.TryGetProperty("urls", out var array)
            || array.ValueKind != JsonValueKind.Array)
            return mirrors;

        var configured = ReadMirrorlist();
        foreach (var entry in array.EnumerateArray())
        {
            if (!entry.TryGetProperty("url", out var urlValue))
                continue;
            var url = urlValue.GetString() ?? string.Empty;
            if (!configured.Any(mirror => url.StartsWith(mirror, StringComparison.OrdinalIgnoreCase)))
                continue;
            mirrors.Add(new MirrorInfo(
                url,
                GetString(entry, "country"),
                GetString(entry, "last_sync"),
                entry.TryGetProperty("completion_pct", out var completion) ? completion.GetDouble() : 0,
                entry.TryGetProperty("delay", out var delay) ? delay.GetDouble() : 0,
                entry.TryGetProperty("score", out var score) ? score.GetDouble() : 0));
        }
        return mirrors;
    }

    private static List<string> ReadMirrorlist()
    {
        var mirrors = new List<string>();
        try
        {
            const string path = "/etc/pacman.d/mirrorlist";
            if (!File.Exists(path))
                return mirrors;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("Server", StringComparison.OrdinalIgnoreCase))
                    continue;
                var separator = line.IndexOf('=');
                if (separator < 0)
                    continue;
                var url = line[(separator + 1)..].Trim();
                // The configured URL ends in /$repo/os/$arch; match its base.
                var placeholder = url.IndexOf("$repo", StringComparison.Ordinal);
                if (placeholder > 0)
                    url = url[..placeholder];
                if (url.Length > 0)
                    mirrors.Add(url);
            }
        }
        catch
        {
            // A missing or unreadable mirrorlist simply yields no matches.
        }
        return mirrors;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string StripHtml(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(
            html, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim());
    }
}
