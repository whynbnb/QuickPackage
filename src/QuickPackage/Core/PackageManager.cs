using System.Text.RegularExpressions;

namespace QuickPackage.Core;

/// <summary>
/// Query and operation engine for every backend. All work runs off the UI
/// thread; callers pass a CancellationToken for cancellation.
/// </summary>
public sealed partial class PackageManager
{
    private readonly Dictionary<Backend, bool> _enabled = new()
    {
        [Backend.Pacman] = true,
        [Backend.Aur] = true,
        [Backend.Flatpak] = true,
        [Backend.Cargo] = true,
        // Developer tool backends are optional and disabled by default.
        [Backend.Mise] = false,
        [Backend.Rustup] = false,
        [Backend.Fvm] = false,
    };

    private IReadOnlyDictionary<Backend, IReadOnlyDictionary<string, string?>> _proxyEnvironments =
        new Dictionary<Backend, IReadOnlyDictionary<string, string?>>();

    // Read-only queries must not hang forever on a broken mirror/network.
    private int _queryTimeoutMilliseconds = 30_000;

    // Only paru and yay are supported; empty means auto-detect.
    private string _preferredAurHelper = string.Empty;

    // Supplies the sudo password when no cached credentials are available.
    // The callback is responsible for showing the prompt on the UI thread.
    private Func<Task<string?>>? _passwordProvider;

    public void SetPasswordProvider(Func<Task<string?>>? provider) => _passwordProvider = provider;

    public void SetAurHelper(string? helper)
        => _preferredAurHelper = helper is "paru" or "yay" ? helper : string.Empty;

    public void SetQueryTimeout(int seconds)
        => _queryTimeoutMilliseconds = Math.Clamp(seconds, 5, 600) * 1000;

    /// <summary>Status / progress messages for the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>Raised when a user operation (install/remove/update) ends.</summary>
    public event Action<bool, string>? OperationFinished;

    public void SetBackendEnabled(Backend backend, bool enabled) => _enabled[backend] = enabled;

    public bool IsBackendEnabled(Backend backend) => _enabled.GetValueOrDefault(backend);

    public void SetProxy(ProxySettings proxy)
    {
        var environments = new Dictionary<Backend, IReadOnlyDictionary<string, string?>>();
        foreach (var backend in Enum.GetValues<Backend>())
            environments[backend] = proxy.BuildEnvironment(backend);
        _proxyEnvironments = environments;
    }

    private IReadOnlyDictionary<string, string?>? ProxyEnvironment(Backend backend)
        => _proxyEnvironments.TryGetValue(backend, out var environment) ? environment : null;

    private IReadOnlyDictionary<string, string?>? EnvironmentWith(
        Backend backend, params (string Key, string Value)[] overrides)
    {
        _proxyEnvironments.TryGetValue(backend, out var proxy);
        if ((proxy is null || proxy.Count == 0) && overrides.Length == 0)
            return null;

        var environment = proxy is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?>(proxy);
        foreach (var (key, value) in overrides)
            environment[key] = value;
        return environment;
    }

    private void Report(string message) => Message?.Invoke(message);

    // ---------------------------------------------------------------- queries

    public enum CacheCleanMode
    {
        Uninstalled,
        KeepLatest,
        All,
    }

    public sealed record CacheUsage(
        string PacmanPath,
        string PacmanSize,
        string AurHelper,
        string AurPath,
        string AurSize,
        bool HasPaccache);

    /// <summary>Sizes of the pacman package cache and the AUR helper cache.</summary>
    public async Task<CacheUsage> GetCacheUsageAsync(CancellationToken cancellationToken)
    {
        const string pacmanCache = "/var/cache/pacman/pkg";
        var helper = AurHelper() ?? string.Empty;
        var aurPath = helper switch
        {
            "paru" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/paru"),
            "yay" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/yay"),
            _ => string.Empty,
        };

        var pacmanSize = await DirectorySizeAsync(pacmanCache, cancellationToken).ConfigureAwait(false);
        var aurSize = aurPath.Length == 0
            ? string.Empty
            : await DirectorySizeAsync(aurPath, cancellationToken).ConfigureAwait(false);

        return new CacheUsage(
            pacmanCache,
            pacmanSize,
            helper,
            aurPath,
            aurSize,
            CommandRunner.HasExecutable("paccache"));
    }

    /// <summary>
    /// Removes cached pacman packages. Prefers paccache (pacman-contrib) and
    /// falls back to pacman -Sc when it is not installed.
    /// </summary>
    public async Task<CommandRunner.Result> CleanPacmanCacheAsync(
        CacheCleanMode mode, CancellationToken cancellationToken)
    {
        if (!await EnsureSudoAsync(cancellationToken).ConfigureAwait(false))
            return new CommandRunner.Result(false, Loc.T("Authentication failed"));

        string executable;
        List<string> arguments;
        if (mode == CacheCleanMode.KeepLatest && CommandRunner.HasExecutable("paccache"))
        {
            executable = "sudo";
            arguments = ["paccache", "-rk1"];
        }
        else if (mode == CacheCleanMode.All)
        {
            executable = "sudo";
            arguments = ["pacman", "-Scc", "--noconfirm"];
        }
        else if (CommandRunner.HasExecutable("paccache"))
        {
            executable = "sudo";
            arguments = ["paccache", "-ruk0"];
        }
        else
        {
            executable = "sudo";
            arguments = ["pacman", "-Sc", "--noconfirm"];
        }

        Report(Loc.T("Cleaning pacman cache…"));
        var run = await CommandRunner.RunAsync(
            executable, arguments, 3_600_000, null, cancellationToken, operation: true).ConfigureAwait(false);
        Report(run.Ok ? Loc.T("Cache cleaned") : Loc.T("Failed: %1", "pacman cache"));
        return run;
    }

    /// <summary>Cleans the AUR helper cache using its native -Sc/-Scc flags.</summary>
    public async Task<CommandRunner.Result> CleanAurCacheAsync(
        bool all, CancellationToken cancellationToken)
    {
        var helper = AurHelper();
        if (helper is null)
            return new CommandRunner.Result(false, Loc.T("No AUR helper (paru or yay) found"));

        if (!await EnsureSudoAsync(cancellationToken).ConfigureAwait(false))
            return new CommandRunner.Result(false, Loc.T("Authentication failed"));

        Report(Loc.T("Cleaning the AUR helper cache…"));
        var run = await CommandRunner.RunAsync(
            helper, [all ? "-Scc" : "-Sc", "--noconfirm"], 3_600_000,
            ProxyEnvironment(Backend.Aur), cancellationToken, operation: true).ConfigureAwait(false);
        Report(run.Ok ? Loc.T("Cache cleaned") : Loc.T("Failed: %1", "AUR cache"));
        return run;
    }

    /// <summary>Currently installed pacman package versions (repo + foreign).</summary>
    public async Task<Dictionary<string, string>> GetInstalledVersionsAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var environment = EnvironmentWith(Backend.Pacman, ("LANG", "C"), ("LC_ALL", "C"));
        var run = await CommandRunner.RunAsync(
            "pacman", ["-Q"], _queryTimeoutMilliseconds, environment, cancellationToken).ConfigureAwait(false);
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var space = line.IndexOf(' ');
            if (space <= 0)
                continue;
            result[line[..space]] = line[(space + 1)..].Trim();
        }
        return result;
    }

    private static async Task<string> DirectorySizeAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
            return Loc.T("Not present");

        // "du" is far faster than walking a 20+ GiB cache from managed code.
        var run = await CommandRunner.RunAsync(
            "sh", ["-c", $"du -sh -- {path} 2>/dev/null"], 120_000, null, cancellationToken)
            .ConfigureAwait(false);
        var line = run.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? string.Empty;
        var separator = line.IndexOf('\t');
        return separator > 0 ? line[..separator] : (line.Length == 0 ? "—" : line);
    }

    /// <summary>
    /// Captures the installed packages using each backend's native export
    /// command, falling back to the already-loaded package list when a
    /// backend has no usable export command. Entries are enriched with the
    /// install reason and origin from the normal query.
    /// </summary>
    public async Task<PackageSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken)
    {
        var loaded = await LoadPackagesAsync(cancellationToken).ConfigureAwait(false);
        var lookup = BuildSnapshotLookup(loaded);
        var entries = new List<SnapshotEntry>();

        if (_enabled[Backend.Pacman])
            await AddPacmanExportAsync(entries, Backend.Pacman, "-Qn", lookup, loaded, cancellationToken).ConfigureAwait(false);
        if (_enabled[Backend.Aur])
            await AddPacmanExportAsync(entries, Backend.Aur, "-Qm", lookup, loaded, cancellationToken).ConfigureAwait(false);
        if (_enabled[Backend.Flatpak])
            await AddFlatpakExportAsync(entries, lookup, loaded, cancellationToken).ConfigureAwait(false);
        if (_enabled[Backend.Cargo])
            await AddCargoExportAsync(entries, lookup, loaded, cancellationToken).ConfigureAwait(false);

        // These backends only expose their installed set through the same
        // listing commands the normal query already uses.
        foreach (var backend in new[] { Backend.Mise, Backend.Rustup, Backend.Fvm })
        {
            if (_enabled[backend])
                AddSnapshotFallback(entries, loaded, backend);
        }

        var packages = entries
            .GroupBy(entry => (entry.Backend, entry.Name))
            .Select(group => group.First())
            .OrderBy(entry => entry.Backend)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PackageSnapshot { Packages = packages };
    }

    private static Dictionary<(Backend, string), Package> BuildSnapshotLookup(List<Package> loaded)
    {
        var lookup = new Dictionary<(Backend, string), Package>();
        foreach (var package in loaded)
        {
            lookup.TryAdd((package.Backend, package.Name), package);
            if (package.PackageId.Length > 0)
                lookup.TryAdd((package.Backend, package.PackageId), package);
        }
        return lookup;
    }

    private static void AddSnapshotEntry(
        List<SnapshotEntry> entries, Backend backend, string name, string version,
        Dictionary<(Backend, string), Package> lookup)
    {
        lookup.TryGetValue((backend, name), out var package);
        entries.Add(new SnapshotEntry
        {
            Backend = backend,
            Name = name,
            Version = version,
            Origin = package?.Origin ?? string.Empty,
            Explicit = backend is Backend.Pacman or Backend.Aur
                ? package?.Explicit ?? false
                : null,
            Orphan = package?.Orphan ?? false,
        });
    }

    private async Task AddPacmanExportAsync(
        List<SnapshotEntry> entries, Backend backend, string query,
        Dictionary<(Backend, string), Package> lookup, List<Package> loaded,
        CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync(
            "pacman", [query], _queryTimeoutMilliseconds,
            ProxyEnvironment(backend), cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
        {
            AddSnapshotFallback(entries, loaded, backend);
            return;
        }

        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var space = line.IndexOf(' ');
            AddSnapshotEntry(
                entries, backend,
                space < 0 ? line : line[..space],
                space < 0 ? string.Empty : line[(space + 1)..].Trim(),
                lookup);
        }
    }

    private async Task AddFlatpakExportAsync(
        List<SnapshotEntry> entries, Dictionary<(Backend, string), Package> lookup,
        List<Package> loaded, CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync(
            "flatpak", ["list", "--app", "--columns=application,version"],
            _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Flatpak), cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
        {
            AddSnapshotFallback(entries, loaded, Backend.Flatpak);
            return;
        }

        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var columns = line.Split('\t');
            if (columns.Length == 0 || columns[0].Length == 0)
                continue;
            AddSnapshotEntry(
                entries, Backend.Flatpak, columns[0],
                columns.Length > 1 ? columns[1] : string.Empty, lookup);
        }
    }

    private async Task AddCargoExportAsync(
        List<SnapshotEntry> entries, Dictionary<(Backend, string), Package> lookup,
        List<Package> loaded, CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync(
            "cargo", ["install", "--list"], _queryTimeoutMilliseconds,
            ProxyEnvironment(Backend.Cargo), cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
        {
            AddSnapshotFallback(entries, loaded, Backend.Cargo);
            return;
        }

        foreach (var rawLine in run.Output.Split('\n'))
        {
            var match = CargoInstalledRegex().Match(rawLine.TrimEnd('\r'));
            if (match.Success)
                AddSnapshotEntry(
                    entries, Backend.Cargo, match.Groups[1].Value,
                    match.Groups[2].Value, lookup);
        }
    }

    private static void AddSnapshotFallback(
        List<SnapshotEntry> entries, List<Package> loaded, Backend backend)
    {
        foreach (var package in loaded)
        {
            if (package.Backend != backend)
                continue;
            entries.Add(new SnapshotEntry
            {
                Backend = backend,
                Name = package.Name,
                Version = package.Version,
                Origin = package.Origin,
                Explicit = backend is Backend.Pacman or Backend.Aur ? package.Explicit : null,
                Orphan = package.Orphan,
            });
        }
    }

    public async Task<List<Package>> LoadPackagesAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<List<Package>>>();
        if (_enabled[Backend.Pacman] || _enabled[Backend.Aur])
            tasks.Add(QueryPacmanAsync(cancellationToken));
        if (_enabled[Backend.Flatpak])
            tasks.Add(QueryFlatpakAsync(cancellationToken));
        if (_enabled[Backend.Cargo])
            tasks.Add(QueryCargoAsync());
        if (_enabled[Backend.Mise])
            tasks.Add(QueryMiseAsync(cancellationToken));
        if (_enabled[Backend.Rustup])
            tasks.Add(QueryRustupAsync(cancellationToken));
        if (_enabled[Backend.Fvm])
            tasks.Add(QueryFvmAsync());

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.SelectMany(packages => packages).ToList();
    }

    private async Task<List<Package>> QueryPacmanAsync(CancellationToken cancellationToken)
    {
        var result = new List<Package>();
        // LANG/LC_ALL=C keep the "key : value" layout stable for parsing.
        var environment = EnvironmentWith(Backend.Pacman, ("LANG", "C"), ("LC_ALL", "C"));
        var query = await CommandRunner.RunAsync("pacman", ["-Qi"], _queryTimeoutMilliseconds, environment, cancellationToken)
            .ConfigureAwait(false);

        var package = new Package();
        string? currentKey = null;

        void Append(string key, string value)
        {
            switch (key)
            {
                case "name": package.Name += " " + value; break;
                case "version": package.Version += " " + value; break;
                case "description": package.Description += " " + value; break;
                case "origin": package.Origin += " " + value; break;
                case "size": package.Size += " " + value; break;
                case "date": package.InstallDate += " " + value; break;
                case "reason": package.InstallReason += " " + value; break;
                case "packager": package.Packager += " " + value; break;
                case "url": package.Url += " " + value; break;
                case "licenses": package.Licenses += " " + value; break;
                case "depends": package.Depends += " " + value; break;
            }
        }

        void Flush()
        {
            if (!string.IsNullOrEmpty(package.Name))
            {
                package.PackageId = package.Name;
                package.Backend = Backend.Pacman;
                package.Installed = true;
                result.Add(package);
            }
            package = new Package();
        }

        foreach (var rawLine in query.Output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                currentKey = null;
                continue;
            }
            var separator = line.IndexOf(" : ", StringComparison.Ordinal);
            if (separator < 0)
            {
                // Continuation of the previous (wrapped) value.
                if (currentKey is not null)
                    Append(currentKey, line.Trim());
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 3)..].Trim();
            switch (key)
            {
                case "Name": package.Name = value; currentKey = "name"; break;
                case "Version": package.Version = value; currentKey = "version"; break;
                case "Description": package.Description = value; currentKey = "description"; break;
                case "Repository": package.Origin = value; currentKey = "origin"; break;
                case "Installed Size": package.Size = value; currentKey = "size"; break;
                case "Install Date": package.InstallDate = value; currentKey = "date"; break;
                case "Install Reason":
                    package.InstallReason = value;
                    package.Explicit = value.StartsWith("Explicitly", StringComparison.OrdinalIgnoreCase);
                    currentKey = "reason";
                    break;
                case "Packager": package.Packager = value; currentKey = "packager"; break;
                case "URL": package.Url = value; currentKey = "url"; break;
                case "Licenses": package.Licenses = value; currentKey = "licenses"; break;
                case "Depends On": package.Depends = value; currentKey = "depends"; break;
                default: currentKey = null; break;
            }
        }
        Flush();

        // pacman -Qi carries no repository on every system; map installed
        // packages to their repo via "pacman -Sl"; the rest is foreign (AUR).
        var syncList = await CommandRunner.RunAsync("pacman", ["-Sl"], _queryTimeoutMilliseconds, environment, cancellationToken)
            .ConfigureAwait(false);
        var repoByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in syncList.Output.Split('\n'))
        {
            var match = SyncRepoRegex().Match(rawLine.Trim());
            if (match.Success)
                repoByName.TryAdd(match.Groups[2].Value, match.Groups[1].Value);
        }
        var enabled = new List<Package>(result.Count);
        var orphanRun = await CommandRunner.RunAsync(
            "pacman", ["-Qdtq"], _queryTimeoutMilliseconds, environment, cancellationToken).ConfigureAwait(false);
        var orphans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in orphanRun.Output.Split('\n'))
        {
            var name = rawLine.Trim();
            if (name.Length > 0)
                orphans.Add(name);
        }
        foreach (var item in result)
        {
            item.Orphan = orphans.Contains(item.Name);
            if (repoByName.TryGetValue(item.Name, out var repo))
            {
                item.Origin = repo;
                item.Backend = Backend.Pacman;
                item.Foreign = false;
                if (_enabled[Backend.Pacman])
                    enabled.Add(item);
            }
            else
            {
                item.Origin = Loc.T("AUR / local");
                item.Backend = Backend.Aur;
                item.Foreign = true;
                if (_enabled[Backend.Aur])
                    enabled.Add(item);
            }
        }
        return enabled;
    }

    private async Task<List<Package>> QueryFlatpakAsync(CancellationToken cancellationToken)
    {
        var result = new List<Package>();
        var run = await CommandRunner.RunAsync(
            "flatpak",
            ["list", "--app", "--columns=application,name,version,origin,description"],
            _queryTimeoutMilliseconds,
            ProxyEnvironment(Backend.Flatpak),
            cancellationToken).ConfigureAwait(false);
        if (!run.Ok && run.Output.Length == 0)
            return result;
        foreach (var line in run.Output.Split('\n'))
        {
            var columns = line.TrimEnd('\r').Split('\t');
            if (columns.Length == 0 || string.IsNullOrEmpty(columns[0]))
                continue;
            var package = new Package
            {
                PackageId = columns[0],
                Name = columns.Length > 1 && !string.IsNullOrEmpty(columns[1]) ? columns[1] : columns[0],
                Version = columns.Length > 2 ? columns[2] : string.Empty,
                Origin = columns.Length > 3 ? columns[3] : string.Empty,
                Description = columns.Length > 4 ? columns[4] : string.Empty,
                Backend = Backend.Flatpak,
                Installed = true,
            };
            result.Add(package);
        }
        return result;
    }

    private static Task<List<Package>> QueryCargoAsync()
    {
        var result = new List<Package>();
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo");
        var listFile = Path.Combine(cargoHome, ".crates.toml");
        if (!File.Exists(listFile))
            return Task.FromResult(result);
        try
        {
            foreach (var line in File.ReadLines(listFile))
            {
                var match = CargoEntryRegex().Match(line);
                if (!match.Success)
                    continue;
                var source = match.Groups[3].Value;
                var origin = source.StartsWith("registry+", StringComparison.Ordinal) ? "crates.io"
                    : source.StartsWith("git+", StringComparison.Ordinal) ? "git"
                    : source.StartsWith("path+", StringComparison.Ordinal) ? Loc.T("local path")
                    : source;
                var binaries = match.Groups[4].Value
                    .Split(',')
                    .Select(bin => bin.Trim().Replace("\"", string.Empty))
                    .Where(bin => bin.Length > 0);
                result.Add(new Package
                {
                    Name = match.Groups[1].Value,
                    PackageId = match.Groups[1].Value,
                    Version = match.Groups[2].Value,
                    Origin = origin,
                    Binaries = string.Join(", ", binaries),
                    Backend = Backend.Cargo,
                    Installed = true,
                });
            }
        }
        catch
        {
            // An unreadable crates file simply yields no cargo packages.
        }
        return Task.FromResult(result);
    }

    private async Task<List<Package>> QueryMiseAsync(CancellationToken cancellationToken)
    {
        var result = new List<Package>();
        var run = await CommandRunner.RunAsync(
                "mise", ["ls"], _queryTimeoutMilliseconds,
                ProxyEnvironment(Backend.Mise), cancellationToken)
            .ConfigureAwait(false);
        // Rows: "tool version config-path requested". A tool with several
        // installed versions gets one row per version.
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var tokens = WhitespaceRegex().Split(rawLine.Trim());
            if (tokens.Length < 2 || tokens[0].Length == 0 || tokens[0] == "Tool")
                continue;
            var name = tokens[0];
            var version = tokens[1];
            result.Add(new Package
            {
                Name = name,
                Version = version,
                PackageId = name + "@" + version,
                Origin = tokens.Length > 2 ? tokens[2] : string.Empty,
                Backend = Backend.Mise,
                Installed = true,
            });
        }
        return result;
    }

    private async Task<List<Package>> QueryRustupAsync(CancellationToken cancellationToken)
    {
        var result = new List<Package>();
        var run = await CommandRunner.RunAsync(
            "rustup", ["toolchain", "list"], _queryTimeoutMilliseconds,
            ProxyEnvironment(Backend.Rustup), cancellationToken).ConfigureAwait(false);
        // Rows: "stable-x86_64-unknown-linux-gnu" or
        // "nightly-x86_64-unknown-linux-gnu (active, default)".
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            var parenthesis = line.IndexOf('(');
            var name = parenthesis < 0 ? line : line[..parenthesis].Trim();
            result.Add(new Package
            {
                Name = name,
                PackageId = name,
                Origin = parenthesis < 0 ? string.Empty : line[(parenthesis + 1)..].TrimEnd(')').Trim(),
                Backend = Backend.Rustup,
                Installed = true,
            });
        }
        return result;
    }

    private static Task<List<Package>> QueryFvmAsync()
    {
        var result = new List<Package>();
        // "fvm list" is slow and its output changed across releases; the
        // versions directory is the authoritative source. fvm honours
        // FVM_CACHE, with ~/.fvm/versions and ~/fvm/versions as defaults.
        var versionsDir = Environment.GetEnvironmentVariable("FVM_CACHE") ?? string.Empty;
        if (versionsDir.Length == 0 || !Directory.Exists(versionsDir))
        {
            versionsDir = string.Empty;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var candidate in new[] { ".fvm/versions", "fvm/versions" })
            {
                var path = Path.Combine(home, candidate);
                if (Directory.Exists(path))
                {
                    versionsDir = path;
                    break;
                }
            }
        }
        if (versionsDir.Length == 0)
            return Task.FromResult(result);

        // The global version is a "default" symlink next to the versions dir.
        var defaultLink = Path.Combine(versionsDir, "..", "default");
        var defaultTarget = CanonicalPath(defaultLink);
        foreach (var versionDir in Directory.EnumerateDirectories(versionsDir).OrderBy(Path.GetFileName))
        {
            var version = Path.GetFileName(versionDir);
            result.Add(new Package
            {
                Name = version,
                PackageId = version,
                Origin = defaultTarget is not null && CanonicalPath(versionDir) == defaultTarget
                    ? Loc.T("global")
                    : string.Empty,
                Backend = Backend.Fvm,
                Installed = true,
            });
        }
        return Task.FromResult(result);
    }

    /// <summary>Full path with symlinks resolved (the path itself may be a link or a directory).</summary>
    private static string? CanonicalPath(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var target = info.LinkTarget;
            if (target is not null)
            {
                if (!Path.IsPathRooted(target))
                    target = Path.Combine(Path.GetDirectoryName(path)!, target);
                return Path.GetFullPath(target);
            }
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    // --------------------------------------------------------------- updates

    public async Task<Dictionary<Backend, Dictionary<string, string>>> CheckUpdatesAsync(
        CancellationToken cancellationToken)
    {
        var helper = AurHelper();
        var tasks = new List<Task<(Backend Key, Dictionary<string, string> Updates)>>();

        if (_enabled[Backend.Pacman])
            tasks.Add(Wrap(Backend.Pacman, QueryPacmanUpdatesAsync(cancellationToken)));
        if (_enabled[Backend.Aur] && helper is not null)
            tasks.Add(Wrap(Backend.Aur, AurUpdateNamesAsync(helper!, cancellationToken)));
        if (_enabled[Backend.Flatpak])
            tasks.Add(Wrap(Backend.Flatpak, FlatpakUpdateNamesAsync(cancellationToken)));
        if (_enabled[Backend.Cargo])
            tasks.Add(Wrap(Backend.Cargo, CargoUpdateNamesAsync(cancellationToken)));
        if (_enabled[Backend.Mise])
            tasks.Add(Wrap(Backend.Mise, MiseUpdateNamesAsync(cancellationToken)));
        if (_enabled[Backend.Rustup])
            tasks.Add(Wrap(Backend.Rustup, RustupUpdateNamesAsync(cancellationToken)));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var updates = new Dictionary<Backend, Dictionary<string, string>>();
        foreach (var (key, map) in results)
        {
            if (!updates.TryGetValue(key, out var target))
                updates[key] = target = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, version) in map)
                target[name] = version;
        }
        return updates;

        static async Task<(Backend, Dictionary<string, string>)> Wrap(
            Backend key, Task<Dictionary<string, string>> task)
            => (key, await task.ConfigureAwait(false));
    }

    /// <summary>
    /// Parses "name oldver -> newver" (checkupdates / paru -Qua) or
    /// "name newver" rows into a name -> new version map.
    /// </summary>
    private static Dictionary<string, string> ParseUpdateLines(string output)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var tokens = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;
            var name = tokens[0];
            var version = string.Empty;
            var arrow = Array.IndexOf(tokens, "->");
            if (arrow >= 0 && arrow + 1 < tokens.Length)
                version = tokens[arrow + 1];
            else if (tokens.Length >= 2)
                version = tokens[^1];
            updates[name] = version;
        }
        return updates;
    }

    private async Task<Dictionary<string, string>> QueryPacmanUpdatesAsync(CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync("checkupdates", [], _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Pacman), cancellationToken)
            .ConfigureAwait(false);
        return ParseUpdateLines(run.Output);
    }

    /// <summary>Configured AUR helper, falling back to whichever one is installed.</summary>
    private string? AurHelper()
    {
        if (_preferredAurHelper.Length > 0 && CommandRunner.HasExecutable(_preferredAurHelper))
            return _preferredAurHelper;
        if (CommandRunner.HasExecutable("paru"))
            return "paru";
        if (CommandRunner.HasExecutable("yay"))
            return "yay";
        return null;
    }

    private async Task<Dictionary<string, string>> AurUpdateNamesAsync(
        string helper, CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync(helper, ["-Qua"], _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Aur), cancellationToken)
            .ConfigureAwait(false);
        return ParseUpdateLines(run.Output);
    }

    private async Task<Dictionary<string, string>> MiseUpdateNamesAsync(CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync("mise", ["outdated"], _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Mise), cancellationToken)
            .ConfigureAwait(false);
        // Rows: "tool requested current latest path" — latest is the 4th token.
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var tokens = WhitespaceRegex().Split(rawLine.Trim());
            if (tokens.Length < 2 || tokens[0].Length == 0 || tokens[0] == "Tool")
                continue;
            updates[tokens[0]] = tokens.Length >= 4 ? tokens[3] : string.Empty;
        }
        return updates;
    }

    private async Task<Dictionary<string, string>> RustupUpdateNamesAsync(CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync("rustup", ["check"], _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Rustup), cancellationToken)
            .ConfigureAwait(false);
        // "<toolchain> - up to date: …" / "<toolchain> - Update available: 1.90.0 (x) -> 1.91.0 (y)"
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0 || !line.Contains("update available", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = line[..separator].Trim();
            if (name.Length == 0)
                continue;
            var rest = line[(separator + 3)..];
            var arrow = rest.IndexOf("->", StringComparison.Ordinal);
            var version = string.Empty;
            if (arrow >= 0)
            {
                version = rest[(arrow + 2)..].Trim();
                var space = version.IndexOf(' ');
                if (space > 0)
                    version = version[..space];
            }
            updates[name] = version;
        }
        return updates;
    }

    private async Task<Dictionary<string, string>> FlatpakUpdateNamesAsync(
        CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync(
            "flatpak", ["remote-ls", "--updates", "--app", "--columns=application,version"],
            _queryTimeoutMilliseconds, ProxyEnvironment(Backend.Flatpak), cancellationToken).ConfigureAwait(false);
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var columns = rawLine.TrimEnd('\r').Split('\t');
            if (string.IsNullOrWhiteSpace(columns[0]))
                continue;
            updates[columns[0].Trim()] = columns.Length > 1 && columns[1] != "-" ? columns[1].Trim() : string.Empty;
        }
        return updates;
    }

    private async Task<Dictionary<string, string>> CargoUpdateNamesAsync(
        CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync(
            "cargo", ["install-update", "--list"], _queryTimeoutMilliseconds,
            ProxyEnvironment(Backend.Cargo), cancellationToken).ConfigureAwait(false);
        // Rows: "name vInstalled vLatest Yes|No" — only Yes needs an update.
        foreach (var line in run.Output.Split('\n'))
        {
            var match = CargoUpdateRegex().Match(line.Trim());
            if (match.Success)
                updates[match.Groups[1].Value] = match.Groups[3].Value;
        }
        return updates;
    }

    // ------------------------------------------------------------- operations

    /// <summary>
    /// Makes sure sudo can run without a terminal. Cached credentials are
    /// reused; otherwise the UI is asked for the password and
    /// <c>sudo -v</c> refreshes the ticket. Returns false when the user
    /// cancels or every attempt fails.
    /// </summary>
    private async Task<bool> EnsureSudoAsync(CancellationToken cancellationToken)
    {
        var cached = await CommandRunner.RunAsync(
            "sudo", ["-n", "true"], 30_000, null, cancellationToken).ConfigureAwait(false);
        if (cached.Ok)
            return true;

        if (_passwordProvider is null)
            return false;

        for (var attempt = 0; attempt < 3; ++attempt)
        {
            var password = await _passwordProvider().ConfigureAwait(false);
            if (string.IsNullOrEmpty(password))
                return false;

            var run = await CommandRunner.RunAsync(
                "sudo", ["-S", "-p", "", "-v"], 60_000, null, cancellationToken,
                standardInput: password + "\n").ConfigureAwait(false);
            if (run.Ok)
                return true;

            Report(Loc.T("Authentication failed"));
        }

        return false;
    }

    private sealed class OperationState
    {
        public bool Ok { get; set; } = true;
        public string LastError { get; set; } = string.Empty;
    }

    public async Task RunOperationAsync(
        PackageOperation operation,
        IReadOnlyList<Package> packages,
        CancellationToken cancellationToken)
    {
        var state = new OperationState();

        // pacman and AUR helper operations need root. Ask for the password
        // up front instead of letting sudo fail with "no tty present".
        if (packages.Any(package => package.Backend is Backend.Pacman or Backend.Aur)
            && !await EnsureSudoAsync(cancellationToken))
        {
            state.Ok = false;
            state.LastError = Loc.T("Authentication failed");
            Report(state.LastError);
            OperationFinished?.Invoke(false, state.LastError);
            return;
        }

        // Pacman must never be partially updated: one full "-Syu" upgrades the
        // whole system. AUR packages get one helper pass; mise/rustup one
        // tool upgrade pass each.
        if (operation == PackageOperation.Update)
        {
            if (packages.Any(p => p.Backend == Backend.Pacman))
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Report(Loc.T("Upgrading system (pacman -Syu)…"));
                    var run = await CommandRunner.RunAsync(
                        "sudo", ["pacman", "-Syu", "--noconfirm"], 3_600_000,
                        ProxyEnvironment(Backend.Pacman), cancellationToken, operation: true)
                        .ConfigureAwait(false);
                    if (!run.Ok)
                    {
                        state.Ok = false;
                        state.LastError = Loc.T("pacman -Syu: %1", Tail(run.Output));
                        Report(Loc.T("Failed: pacman -Syu"));
                    }
                }
            }

            if (packages.Any(p => p.Backend == Backend.Aur))
            {
                var helper = AurHelper();
                if (helper is null)
                {
                    state.Ok = false;
                    state.LastError = Loc.T("No AUR helper (paru or yay) found");
                    Report(Loc.T("Failed: %1", "AUR"));
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    Report(Loc.T("Upgrading AUR packages (%1 -Sua)…", helper));
                    var arguments = new List<string> { "-Sua", "--noconfirm" };
                    if (helper == "paru")
                        arguments.Add("--skipreview");
                    var run = await CommandRunner.RunAsync(helper, arguments, 3_600_000,
                        ProxyEnvironment(Backend.Aur), cancellationToken, operation: true)
                        .ConfigureAwait(false);
                    if (!run.Ok)
                    {
                        state.Ok = false;
                        state.LastError = Loc.T("%1 -Sua: %2", helper, Tail(run.Output));
                        Report(Loc.T("Failed: %1", "AUR"));
                    }
                }
            }

            if (packages.Any(p => p.Backend == Backend.Mise) && !cancellationToken.IsCancellationRequested)
            {
                Report(Loc.T("Upgrading mise tools (mise upgrade)…"));
                var run = await CommandRunner.RunAsync(
                    "mise", ["upgrade"], 3_600_000,
                    ProxyEnvironment(Backend.Mise), cancellationToken, operation: true).ConfigureAwait(false);
                if (!run.Ok)
                {
                    state.Ok = false;
                    state.LastError = Loc.T("mise upgrade: %1", Tail(run.Output));
                    Report(Loc.T("Failed: %1", "mise"));
                }
            }

            if (packages.Any(p => p.Backend == Backend.Rustup) && !cancellationToken.IsCancellationRequested)
            {
                Report(Loc.T("Upgrading rustup toolchains (rustup update)…"));
                var run = await CommandRunner.RunAsync(
                    "rustup", ["update"], 3_600_000,
                    ProxyEnvironment(Backend.Rustup), cancellationToken, operation: true).ConfigureAwait(false);
                if (!run.Ok)
                {
                    state.Ok = false;
                    state.LastError = Loc.T("rustup update: %1", Tail(run.Output));
                    Report(Loc.T("Failed: %1", "rustup"));
                }
            }
        }

        foreach (var package in packages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                state.Ok = false;
                state.LastError = Loc.T("Cancelled");
                break;
            }
            // Whole-pass upgrades above covered pacman/AUR/mise/rustup; fvm has
            // no update flow (install a new version instead).
            if (operation == PackageOperation.Update
                && package.Backend is not (Backend.Flatpak or Backend.Cargo))
                continue;

            var id = package.EffectiveId;
            var arguments = BuildArguments(operation, package, id, state);
            if (arguments is null)
                continue;

            Report(operation switch
            {
                PackageOperation.Remove => Loc.T("Removing %1…", package.Name),
                PackageOperation.Install => Loc.T("Installing %1…", package.Name),
                _ => Loc.T("Updating %1…", package.Name),
            });
            var run = await CommandRunner.RunAsync(arguments[0], arguments.Skip(1), 1_800_000,
                ProxyEnvironment(package.Backend), cancellationToken, operation: true).ConfigureAwait(false);
            if (!run.Ok)
            {
                state.Ok = false;
                state.LastError = Loc.T("%1: %2", package.Name, Tail(run.Output));
                Report(Loc.T("Failed: %1", package.Name));
                continue;
            }
            Report(Loc.T("Done: %1", package.Name));
        }

        OperationFinished?.Invoke(state.Ok, state.LastError);
    }

    /// <summary>Promotes a dependency-installed package to explicitly installed.</summary>
    public async Task<CommandRunner.Result> MarkExplicitAsync(
        Package package, CancellationToken cancellationToken)
    {
        if (!await EnsureSudoAsync(cancellationToken).ConfigureAwait(false))
            return new CommandRunner.Result(false, Loc.T("Authentication failed"));

        Report(Loc.T("Marking %1 as explicitly installed…", package.Name));
        var run = await CommandRunner.RunAsync(
            "sudo", ["pacman", "-D", "--asexplicit", package.EffectiveId], 300_000,
            ProxyEnvironment(Backend.Pacman), cancellationToken, operation: true).ConfigureAwait(false);
        Report(run.Ok ? Loc.T("Done: %1", package.Name) : Loc.T("Failed: %1", package.Name));
        return run;
    }

    private List<string>? BuildArguments(
        PackageOperation operation, Package package, string id, OperationState state)
    {
        switch (package.Backend)
        {
            case Backend.Aur:
                if (operation == PackageOperation.Remove)
                    return ["sudo", "pacman", "-Rns", "--noconfirm", id];
                var helper = AurHelper();
                if (helper is null)
                {
                    state.Ok = false;
                    state.LastError = Loc.T("No AUR helper (paru or yay) found");
                    return null;
                }
                var aur = new List<string> { helper, "-S", "--noconfirm", id };
                if (helper == "paru")
                    aur.Add("--skipreview");
                return aur;

            case Backend.Pacman:
                return operation == PackageOperation.Install
                    ? ["sudo", "pacman", "-S", "--noconfirm", id]
                    : ["sudo", "pacman", "-Rns", "--noconfirm", id];

            case Backend.Flatpak:
                return operation == PackageOperation.Remove
                    ? ["flatpak", "uninstall", "-y", id]
                    : ["flatpak", "update", "-y", id];

            case Backend.Mise:
                return operation == PackageOperation.Remove
                    ? ["mise", "uninstall", id]
                    : ["mise", "install", id];

            case Backend.Rustup:
                return operation == PackageOperation.Remove
                    ? ["rustup", "toolchain", "uninstall", id]
                    : ["rustup", "toolchain", "install", id];

            case Backend.Fvm:
                return operation == PackageOperation.Remove
                    ? ["fvm", "remove", id]
                    : ["fvm", "install", id];

            default:
                return operation == PackageOperation.Remove
                    ? ["cargo", "uninstall", id]
                    : ["cargo", "install", id];
        }
    }

    private static string Tail(string output)
        => output.Length <= 400 ? output : output[^400..];

    [GeneratedRegex(@"^(\S+) (\S+) \S+ \[installed\]$")]
    private static partial Regex SyncRepoRegex();

    [GeneratedRegex(@"^\s*""([a-zA-Z0-9_.+-]+) ([^\s""]+) \(([^)]*)\)""\s*=\s*\[([^\]]*)\]")]
    private static partial Regex CargoEntryRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9_.-]+)\s+v(\S+)\s+v(\S+)\s+Yes\b")]
    private static partial Regex CargoUpdateRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9_.+-]+)\s+v([^\s:]+)")]
    private static partial Regex CargoInstalledRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
