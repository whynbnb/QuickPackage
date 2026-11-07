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

    // Local queries must not hang forever on a broken mirror or cache.
    private int _queryTimeoutMilliseconds = 30_000;

    // Network checks, downloads, builds and installs need a much longer
    // allowance than local queries.
    private int _downloadTimeoutMilliseconds = 3_600_000;

    // Extra attempts for network checks and operations on weak connections.
    private int _networkRetries = 2;

    // Only paru and yay are supported; empty means auto-detect.
    private string _preferredAurHelper = string.Empty;

    // Supplies the sudo password when no cached credentials are available.
    // The callback shows the prompt on the UI thread; the validator it is
    // given re-checks the password without closing the dialog.
    private Func<Func<string, Task<bool>>?, Task<string?>>? _passwordProvider;

    public void SetPasswordProvider(
        Func<Func<string, Task<bool>>?, Task<string?>>? provider) => _passwordProvider = provider;

    public void SetAurHelper(string? helper)
        => _preferredAurHelper = helper is "paru" or "yay" ? helper : string.Empty;

    public void SetQueryTimeout(int seconds)
        => _queryTimeoutMilliseconds = Math.Clamp(seconds, 5, 600) * 1000;

    public void SetDownloadTimeout(int seconds)
        => _downloadTimeoutMilliseconds = Math.Clamp(seconds, 60, 86_400) * 1000;

    public void SetNetworkRetries(int retries)
        => _networkRetries = Math.Clamp(retries, 0, 10);

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
        if (!(await EnsureSudoAsync(cancellationToken).ConfigureAwait(false)).Ok)
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
            executable, arguments, _downloadTimeoutMilliseconds, null, cancellationToken, operation: true).ConfigureAwait(false);
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

        var sudo = await EnsureSudoAsync(cancellationToken).ConfigureAwait(false);
        if (!sudo.Ok)
            return new CommandRunner.Result(false, Loc.T("Authentication failed"));

        await using var askpass = SudoAskpass.TryCreate(
            _passwordProvider, sudo.Password,
            candidate => ValidateSudoPasswordAsync(candidate, cancellationToken));
        Report(Loc.T("Cleaning the AUR helper cache…"));
        var arguments = new List<string> { all ? "-Scc" : "-Sc", "--noconfirm" };
        AddSudoOverride(arguments, askpass?.SudoPath);
        var run = await CommandRunner.RunAsync(
            helper, arguments, _downloadTimeoutMilliseconds,
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
            if (!package.Installed)
                continue;
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
        var expected = query == "-Qm"
            ? new[] { new CommandRunner.ExpectedExit(1, "No AUR packages", RequireEmptyOutput: true) }
            : null;
        var run = await CommandRunner.RunAsync(
            "pacman", [query], _queryTimeoutMilliseconds,
            ProxyEnvironment(backend), cancellationToken,
            expectedExits: expected).ConfigureAwait(false);
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
            if (package.Backend != backend || !package.Installed)
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
        // LANG/LC_ALL=C keep the "key : value" layout stable for parsing.
        var environment = EnvironmentWith(Backend.Pacman, ("LANG", "C"), ("LC_ALL", "C"));
        var query = await CommandRunner.RunAsync("pacman", ["-Qi"], _queryTimeoutMilliseconds, environment, cancellationToken)
            .ConfigureAwait(false);
        var installed = ParsePackageInfo(query.Output, installed: true);

        // "pacman -Si" carries the same detail block for every sync package,
        // including the ones that are not installed. One pass also supplies
        // the repository for installed packages, since "pacman -Qi" does not
        // include one on every system.
        var syncQuery = await CommandRunner.RunAsync(
            "pacman", ["-Si"], _queryTimeoutMilliseconds, environment, cancellationToken)
            .ConfigureAwait(false);
        var sync = new Dictionary<string, Package>(StringComparer.Ordinal);
        var syncOrder = new List<string>();
        foreach (var available in ParsePackageInfo(syncQuery.Output, installed: false))
        {
            if (sync.TryAdd(available.Name, available))
                syncOrder.Add(available.Name);
        }

        var orphanRun = await CommandRunner.RunAsync(
            "pacman", ["-Qdtq"], _queryTimeoutMilliseconds, environment, cancellationToken,
            expectedExits: [new CommandRunner.ExpectedExit(1, "No orphans", RequireEmptyOutput: true)]).ConfigureAwait(false);
        var orphans = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in orphanRun.Output.Split('\n'))
        {
            var name = rawLine.Trim();
            if (name.Length > 0)
                orphans.Add(name);
        }

        var enabled = new List<Package>(installed.Count + sync.Count);
        var installedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in installed)
        {
            item.Orphan = orphans.Contains(item.Name);
            installedNames.Add(item.Name);
            if (sync.TryGetValue(item.Name, out var entry))
            {
                item.Origin = entry.Origin;
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

        // Repo packages that are not installed are kept available for the
        // "not installed" / "all" list filters and for direct installation.
        if (_enabled[Backend.Pacman])
        {
            foreach (var name in syncOrder)
            {
                if (!installedNames.Contains(name))
                    enabled.Add(sync[name]);
            }
        }
        return enabled;
    }

    /// <summary>
    /// Parses one or more "key : value" package blocks as printed by
    /// "pacman -Qi" and "pacman -Si". Continuation lines belong to the
    /// previous key.
    /// </summary>
    private static List<Package> ParsePackageInfo(string output, bool installed)
    {
        var result = new List<Package>();
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
                case "downloadsize": package.DownloadSize += " " + value; break;
                case "date": package.InstallDate += " " + value; break;
                case "builddate": package.BuildDate += " " + value; break;
                case "reason": package.InstallReason += " " + value; break;
                case "packager": package.Packager += " " + value; break;
                case "url": package.Url += " " + value; break;
                case "licenses": package.Licenses += " " + value; break;
                case "architecture": package.Architecture += " " + value; break;
                case "groups": package.Groups += " " + value; break;
                case "provides": package.Provides += " " + value; break;
                case "optionaldeps": package.OptionalDeps += " " + value; break;
                case "conflicts": package.Conflicts += " " + value; break;
                case "depends": package.Depends += " " + value; break;
                case "requiredby": package.RequiredBy += " " + value; break;
            }
        }

        void Flush()
        {
            if (!string.IsNullOrEmpty(package.Name))
            {
                package.PackageId = package.Name;
                package.Backend = Backend.Pacman;
                package.Installed = installed;
                // pacman prints the literal "None" for empty list fields.
                package.Groups = CleanList(package.Groups);
                package.Provides = CleanList(package.Provides);
                package.OptionalDeps = CleanList(package.OptionalDeps);
                package.Conflicts = CleanList(package.Conflicts);
                package.Depends = CleanList(package.Depends);
                package.RequiredBy = CleanList(package.RequiredBy);
                result.Add(package);
            }
            package = new Package();
        }

        foreach (var rawLine in output.Split('\n'))
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
                case "Download Size": package.DownloadSize = value; currentKey = "downloadsize"; break;
                case "Install Date": package.InstallDate = value; currentKey = "date"; break;
                case "Build Date": package.BuildDate = value; currentKey = "builddate"; break;
                case "Install Reason":
                    package.InstallReason = value;
                    package.Explicit = value.StartsWith("Explicitly", StringComparison.OrdinalIgnoreCase);
                    currentKey = "reason";
                    break;
                case "Packager": package.Packager = value; currentKey = "packager"; break;
                case "URL": package.Url = value; currentKey = "url"; break;
                case "Licenses": package.Licenses = value; currentKey = "licenses"; break;
                case "Architecture": package.Architecture = value; currentKey = "architecture"; break;
                case "Groups": package.Groups = value; currentKey = "groups"; break;
                case "Provides": package.Provides = value; currentKey = "provides"; break;
                case "Optional Deps": package.OptionalDeps = value; currentKey = "optionaldeps"; break;
                case "Conflicts With": package.Conflicts = value; currentKey = "conflicts"; break;
                case "Depends On": package.Depends = value; currentKey = "depends"; break;
                case "Required By": package.RequiredBy = value; currentKey = "requiredby"; break;
                default: currentKey = null; break;
            }
        }
        Flush();
        return result;
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
        var run = await RunNetworkAsync("checkupdates", [], _downloadTimeoutMilliseconds, ProxyEnvironment(Backend.Pacman), cancellationToken,
            expectedExits: [new CommandRunner.ExpectedExit(2, "No updates", RequireEmptyOutput: true)])
            .ConfigureAwait(false);
        return run.Ok || (run.ExitCode == 2 && run.Output.Length == 0)
            ? ParseUpdateLines(run.Output)
            : [];
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
        var run = await RunNetworkAsync(helper, ["-Qua"], _downloadTimeoutMilliseconds, ProxyEnvironment(Backend.Aur), cancellationToken,
            expectedExits: [new CommandRunner.ExpectedExit(1, "No updates", RequireEmptyOutput: true)])
            .ConfigureAwait(false);
        return run.Ok || (run.ExitCode == 1 && run.Output.Length == 0)
            ? ParseUpdateLines(run.Output)
            : [];
    }

    private async Task<Dictionary<string, string>> MiseUpdateNamesAsync(CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await RunNetworkAsync("mise", ["outdated"], _downloadTimeoutMilliseconds, ProxyEnvironment(Backend.Mise), cancellationToken)
            .ConfigureAwait(false);
        if (!run.Ok)
            return updates;
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
        var run = await RunNetworkAsync("rustup", ["check"], _downloadTimeoutMilliseconds, ProxyEnvironment(Backend.Rustup), cancellationToken,
            expectedExits: [new CommandRunner.ExpectedExit(100, "Updates available")])
            .ConfigureAwait(false);
        if (!run.Ok && run.ExitCode != 100)
            return updates;
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
        var run = await RunNetworkAsync(
            "flatpak", ["remote-ls", "--updates", "--app", "--columns=application,version"],
            _downloadTimeoutMilliseconds, ProxyEnvironment(Backend.Flatpak), cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
            return updates;
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
        var run = await RunNetworkAsync(
            "cargo", ["install-update", "--list"], _downloadTimeoutMilliseconds,
            ProxyEnvironment(Backend.Cargo), cancellationToken).ConfigureAwait(false);
        if (!run.Ok)
            return updates;
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
    /// <c>sudo -v</c> refreshes the ticket. The validated password is returned
    /// so AUR helpers can reuse it through their own sudo calls.
    /// </summary>
    private async Task<(bool Ok, string? Password)> EnsureSudoAsync(
        CancellationToken cancellationToken)
    {
        var cached = await CommandRunner.RunAsync(
            "sudo", ["-n", "true"], 30_000, null, cancellationToken).ConfigureAwait(false);
        if (cached.Ok)
            return (true, null);

        if (_passwordProvider is null)
            return (false, null);

        // The dialog validates in place and keeps asking until the password
        // is accepted or the user cancels.
        var password = await _passwordProvider(
            candidate => ValidateSudoPasswordAsync(candidate, cancellationToken)).ConfigureAwait(false);
        if (string.IsNullOrEmpty(password))
            return (false, null);

        var run = await CommandRunner.RunAsync(
            "sudo", ["-S", "-p", "", "-v"], 60_000, null, cancellationToken,
            standardInput: password + "\n").ConfigureAwait(false);
        if (run.Ok)
            return (true, password);

        Report(Loc.T("Authentication failed"));
        return (false, null);
    }

    private async Task<bool> ValidateSudoPasswordAsync(
        string password, CancellationToken cancellationToken)
    {
        // -k forces sudo to verify the supplied password instead of
        // succeeding from an existing ticket (which may belong to a
        // different tty/parent process).
        var run = await CommandRunner.RunAsync(
            "sudo", ["-k", "-S", "-p", "", "-v"], 60_000, null, cancellationToken,
            standardInput: password + "\n").ConfigureAwait(false);
        return run.Ok;
    }

    /// <summary>
    /// Runs a network-facing command and retries it when the failure looks
    /// like a broken or slow connection instead of a real error.
    /// </summary>
    private async Task<CommandRunner.Result> RunNetworkAsync(
        string executable,
        IEnumerable<string> arguments,
        int timeoutMilliseconds,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken,
        bool operation = false,
        IReadOnlyCollection<CommandRunner.ExpectedExit>? expectedExits = null)
    {
        var argumentList = arguments as IReadOnlyList<string> ?? arguments.ToList();
        var maxRetries = Math.Clamp(_networkRetries, 0, 10);
        var run = new CommandRunner.Result(false, string.Empty, -1);

        for (var attempt = 0; attempt <= maxRetries; ++attempt)
        {
            run = await CommandRunner.RunAsync(
                executable, argumentList, timeoutMilliseconds, environment, cancellationToken,
                operation: operation, expectedExits: expectedExits).ConfigureAwait(false);

            var expected = expectedExits?.Any(exit => exit.ExitCode == run.ExitCode
                && (!exit.RequireEmptyOutput || run.Output.Length == 0)) == true;
            if (run.Ok || expected || attempt == maxRetries || cancellationToken.IsCancellationRequested)
                return run;
            if (!LooksLikeNetworkFailure(run.Output))
                return run;

            var retry = attempt + 1;
            Report(Loc.T("Network error, retrying (%1/%2)…", retry, maxRetries));
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * retry, 10)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return run;
            }
        }

        return run;
    }

    private static bool LooksLikeNetworkFailure(string output)
    {
        if (output.Length == 0)
            return false;

        foreach (var marker in NetworkFailureMarkers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly string[] NetworkFailureMarkers =
    [
        "curl: (", "curl error", "could not resolve", "couldn't resolve",
        "failed to connect", "connection timed out", "connection reset",
        "connection refused", "network is unreachable", "no route to host",
        "temporary failure in name resolution", "resolving timed out",
        "operation too slow", "transfer closed", "end of response",
        "bytes missing", "partial file", "ssl connect", "tls handshake",
        "failed to retrieve", "failed to download", "error downloading",
        "the requested url returned error: 5", "http/2 stream",
        "下载失败", "无法下载", "未能下载", "下载中断", "网络错误",
        "网络连接", "网络不可达", "连接超时", "连接失败", "超时",
        "响应结束", "传输中断",
    ];

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
        string? sudoPassword = null;
        if (packages.Any(package => package.Backend is Backend.Pacman or Backend.Aur))
        {
            var sudo = await EnsureSudoAsync(cancellationToken).ConfigureAwait(false);
            if (!sudo.Ok)
            {
                state.Ok = false;
                state.LastError = Loc.T("Authentication failed");
                Report(state.LastError);
                OperationFinished?.Invoke(false, state.LastError);
                return;
            }
            sudoPassword = sudo.Password;
        }

        // paru/yay run sudo themselves for the privileged steps. Give them a
        // wrapper that uses the in-app askpass helper so a password prompt
        // cannot block on the terminal that launched QuickPackage.
        await using var askpass = packages.Any(package => package.Backend == Backend.Aur)
            ? SudoAskpass.TryCreate(
                _passwordProvider, sudoPassword,
                candidate => ValidateSudoPasswordAsync(candidate, cancellationToken))
            : null;
        var sudoPath = askpass?.SudoPath;

        // AUR builds download sources through makepkg/curl. Weak links drop
        // large transfers with "end of response ... bytes missing", which
        // curl does not retry by default; give makepkg a temporary config
        // that retries every transfer error.
        using var makepkgConf = packages.Any(package => package.Backend == Backend.Aur)
            ? MakepkgRetryConfig.Create(_networkRetries)
            : null;
        var aurEnvironment = makepkgConf is null
            ? ProxyEnvironment(Backend.Aur)
            : EnvironmentWith(Backend.Aur, ("MAKEPKG_CONF", makepkgConf.Path));

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
                    var run = await RunNetworkAsync(
                        "sudo", ["pacman", "-Syu", "--noconfirm"], _downloadTimeoutMilliseconds,
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
                    AddSudoOverride(arguments, sudoPath);
                    var run = await RunNetworkAsync(
                        helper, arguments, _downloadTimeoutMilliseconds,
                        aurEnvironment, cancellationToken, operation: true)
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
                var run = await RunNetworkAsync(
                    "mise", ["upgrade"], _downloadTimeoutMilliseconds,
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
                var run = await RunNetworkAsync(
                    "rustup", ["update"], _downloadTimeoutMilliseconds,
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
            var arguments = BuildArguments(operation, package, id, state, sudoPath);
            if (arguments is null)
                continue;

            Report(operation switch
            {
                PackageOperation.Remove => Loc.T("Removing %1…", package.Name),
                PackageOperation.Install => Loc.T("Installing %1…", package.Name),
                _ => Loc.T("Updating %1…", package.Name),
            });
            var environment = package.Backend == Backend.Aur
                ? aurEnvironment
                : ProxyEnvironment(package.Backend);
            var run = operation == PackageOperation.Remove
                ? await CommandRunner.RunAsync(
                    arguments[0], arguments.Skip(1), _downloadTimeoutMilliseconds,
                    environment, cancellationToken, operation: true).ConfigureAwait(false)
                : await RunNetworkAsync(
                    arguments[0], arguments.Skip(1), _downloadTimeoutMilliseconds,
                    environment, cancellationToken, operation: true).ConfigureAwait(false);
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
        if (!(await EnsureSudoAsync(cancellationToken).ConfigureAwait(false)).Ok)
            return new CommandRunner.Result(false, Loc.T("Authentication failed"));

        Report(Loc.T("Marking %1 as explicitly installed…", package.Name));
        var run = await CommandRunner.RunAsync(
            "sudo", ["pacman", "-D", "--asexplicit", package.EffectiveId], 300_000,
            ProxyEnvironment(Backend.Pacman), cancellationToken, operation: true).ConfigureAwait(false);
        Report(run.Ok ? Loc.T("Done: %1", package.Name) : Loc.T("Failed: %1", package.Name));
        return run;
    }

    /// <summary>
    /// Points paru/yay at the askpass wrapper. Both helpers accept
    /// <c>--sudo &lt;file&gt;</c> and use it for every internal sudo call.
    /// </summary>
    private static void AddSudoOverride(List<string> arguments, string? sudoPath)
    {
        if (string.IsNullOrEmpty(sudoPath))
            return;
        arguments.Add("--sudo");
        arguments.Add(sudoPath);
    }

    private List<string>? BuildArguments(
        PackageOperation operation, Package package, string id, OperationState state,
        string? sudoPath = null)
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
                var aur = new List<string> { helper, "-S", "--noconfirm" };
                if (helper == "paru")
                    aur.Add("--skipreview");
                AddSudoOverride(aur, sudoPath);
                aur.Add(id);
                return aur;

            case Backend.Pacman:
                return operation == PackageOperation.Install
                    ? ["sudo", "pacman", "-S", "--noconfirm", id]
                    : ["sudo", "pacman", "-Rns", "--noconfirm", id];

            case Backend.Flatpak:
                return operation switch
                {
                    PackageOperation.Remove => ["flatpak", "uninstall", "-y", id],
                    PackageOperation.Install when package.Origin.Length > 0 =>
                        ["flatpak", "install", "-y", package.Origin, id],
                    PackageOperation.Install => ["flatpak", "install", "-y", id],
                    _ => ["flatpak", "update", "-y", id],
                };

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

    /// <summary>pacman uses the literal "None" for an empty list field.</summary>
    private static string CleanList(string value)
        => value == "None" ? string.Empty : value;

    [GeneratedRegex(@"^\s*""([a-zA-Z0-9_.+-]+) ([^\s""]+) \(([^)]*)\)""\s*=\s*\[([^\]]*)\]")]
    private static partial Regex CargoEntryRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9_.-]+)\s+v(\S+)\s+v(\S+)\s+Yes\b")]
    private static partial Regex CargoUpdateRegex();

    [GeneratedRegex(@"^([a-zA-Z0-9_.+-]+)\s+v([^\s:]+)")]
    private static partial Regex CargoInstalledRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
