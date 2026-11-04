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
        [Backend.Flatpak] = true,
        [Backend.Cargo] = true,
        // Developer tool backends are optional and disabled by default.
        [Backend.Mise] = false,
        [Backend.Rustup] = false,
        [Backend.Fvm] = false,
    };

    private IReadOnlyDictionary<string, string> _proxyEnvironment = new Dictionary<string, string>();

    /// <summary>Status / progress messages for the status bar.</summary>
    public event Action<string>? Message;

    /// <summary>Raised when a user operation (install/remove/update) ends.</summary>
    public event Action<bool, string>? OperationFinished;

    public void SetBackendEnabled(Backend backend, bool enabled) => _enabled[backend] = enabled;

    public bool IsBackendEnabled(Backend backend) => _enabled.GetValueOrDefault(backend);

    public void SetProxy(ProxySettings proxy) => _proxyEnvironment = proxy.BuildEnvironment();

    private Dictionary<string, string> EnvironmentWith(params (string Key, string Value)[] overrides)
    {
        var environment = new Dictionary<string, string>(_proxyEnvironment);
        foreach (var (key, value) in overrides)
            environment[key] = value;
        return environment;
    }

    private void Report(string message) => Message?.Invoke(message);

    // ---------------------------------------------------------------- queries

    public async Task<List<Package>> LoadPackagesAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<List<Package>>>();
        if (_enabled[Backend.Pacman])
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
        var environment = EnvironmentWith(("LANG", "C"), ("LC_ALL", "C"));
        var query = await CommandRunner.RunAsync("pacman", ["-Qi"], 60_000, environment, cancellationToken)
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
                case "Install Reason": package.InstallReason = value; currentKey = "reason"; break;
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
        var syncList = await CommandRunner.RunAsync("pacman", ["-Sl"], 60_000, environment, cancellationToken)
            .ConfigureAwait(false);
        var repoByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in syncList.Output.Split('\n'))
        {
            var match = SyncRepoRegex().Match(rawLine.Trim());
            if (match.Success)
                repoByName.TryAdd(match.Groups[2].Value, match.Groups[1].Value);
        }
        foreach (var item in result)
        {
            if (repoByName.TryGetValue(item.Name, out var repo))
                item.Origin = repo;
            else if (string.IsNullOrEmpty(item.Origin) || item.Origin == "None")
            {
                item.Origin = Loc.T("AUR / local");
                item.Foreign = true;
            }
        }
        return result;
    }

    private static async Task<List<Package>> QueryFlatpakAsync(CancellationToken cancellationToken)
    {
        var result = new List<Package>();
        var run = await CommandRunner.RunAsync(
            "flatpak",
            ["list", "--app", "--columns=application,name,version,origin,description"],
            60_000,
            null,
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
        var run = await CommandRunner.RunAsync("mise", ["ls"], 120_000, null, cancellationToken)
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
            "rustup", ["toolchain", "list"], 120_000, null, cancellationToken).ConfigureAwait(false);
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
        var aurEnabled = _enabled[Backend.Pacman] && helper is not null;
        var tasks = new List<Task<(Backend Key, Dictionary<string, string> Updates)>>();

        if (_enabled[Backend.Pacman])
            tasks.Add(Wrap(Backend.Pacman, QueryPacmanUpdatesAsync(cancellationToken)));
        if (aurEnabled)
            tasks.Add(Wrap(Backend.Pacman, AurUpdateNamesAsync(helper!, cancellationToken)));
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
        // pacman and AUR both report under Backend.Pacman; merge their entries.
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
        var run = await CommandRunner.RunAsync("checkupdates", [], 60_000, null, cancellationToken)
            .ConfigureAwait(false);
        return ParseUpdateLines(run.Output);
    }

    /// <summary>AUR helper: paru if available, otherwise yay; null when neither exists.</summary>
    public static string? AurHelper()
    {
        if (CommandRunner.HasExecutable("paru"))
            return "paru";
        if (CommandRunner.HasExecutable("yay"))
            return "yay";
        return null;
    }

    private async Task<Dictionary<string, string>> AurUpdateNamesAsync(
        string helper, CancellationToken cancellationToken)
    {
        var run = await CommandRunner.RunAsync(helper, ["-Qua"], 120_000, null, cancellationToken)
            .ConfigureAwait(false);
        return ParseUpdateLines(run.Output);
    }

    private async Task<Dictionary<string, string>> MiseUpdateNamesAsync(CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync("mise", ["outdated"], 120_000, null, cancellationToken)
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
        var run = await CommandRunner.RunAsync("rustup", ["check"], 120_000, null, cancellationToken)
            .ConfigureAwait(false);
        // "<toolchain> - up to date: …" / "<toolchain> - Update available: 1.90.0 (x) -> 1.91.0 (y)"
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var line = rawLine.Trim();
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0 || !line.Contains("Update available", StringComparison.Ordinal))
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

    private static async Task<Dictionary<string, string>> FlatpakUpdateNamesAsync(
        CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync(
            "flatpak", ["remote-ls", "--updates", "--app", "--columns=application,version"],
            60_000, null, cancellationToken).ConfigureAwait(false);
        foreach (var rawLine in run.Output.Split('\n'))
        {
            var columns = rawLine.TrimEnd('\r').Split('\t');
            if (string.IsNullOrWhiteSpace(columns[0]))
                continue;
            updates[columns[0].Trim()] = columns.Length > 1 && columns[1] != "-" ? columns[1].Trim() : string.Empty;
        }
        return updates;
    }

    private static async Task<Dictionary<string, string>> CargoUpdateNamesAsync(
        CancellationToken cancellationToken)
    {
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        var run = await CommandRunner.RunAsync(
            "cargo", ["install-update", "--list"], 120_000, null, cancellationToken).ConfigureAwait(false);
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

        // Pacman must never be partially updated: one full "-Syu" upgrades the
        // whole system. AUR packages get one helper pass; mise/rustup one
        // tool upgrade pass each.
        if (operation == PackageOperation.Update)
        {
            if (packages.Any(p => p.Backend == Backend.Pacman && !p.Foreign))
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Report(Loc.T("Upgrading system (pacman -Syu)…"));
                    var run = await CommandRunner.RunAsync(
                        "sudo", ["pacman", "-Syu", "--noconfirm"], 3_600_000, null,
                        cancellationToken, operation: true)
                        .ConfigureAwait(false);
                    if (!run.Ok)
                    {
                        state.Ok = false;
                        state.LastError = Loc.T("pacman -Syu: %1", Tail(run.Output));
                        Report(Loc.T("Failed: pacman -Syu"));
                    }
                }
            }

            if (packages.Any(p => p.Backend == Backend.Pacman && p.Foreign))
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
                    var run = await CommandRunner.RunAsync(helper, arguments, 3_600_000, null, cancellationToken,
                        operation: true)
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
                    "mise", ["upgrade"], 3_600_000, null, cancellationToken, operation: true).ConfigureAwait(false);
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
                    "rustup", ["update"], 3_600_000, null, cancellationToken, operation: true).ConfigureAwait(false);
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
            var run = await CommandRunner.RunAsync(arguments[0], arguments.Skip(1), 1_800_000, null,
                cancellationToken, operation: true).ConfigureAwait(false);
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

    private static List<string>? BuildArguments(
        PackageOperation operation, Package package, string id, OperationState state)
    {
        switch (package.Backend)
        {
            case Backend.Pacman when package.Foreign:
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
