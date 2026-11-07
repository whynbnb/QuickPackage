# QuickPackage

A desktop package manager for Arch Linux and developer toolchains, built with
C# 14 and Avalonia.

## Features

- **System packages**: pacman and AUR (via `paru`/`yay`) as separate
  backends, plus Flatpak and cargo; the AUR helper is selectable in
  Preferences and only installed helpers can be chosen
- **Developer tools** (optional, disabled by default): mise, rustup, fvm
- A prominent **System packages / Developer packages** switch keeps the two
  worlds apart: each side has its own filter chips, counters and update flow
- **On-demand online search** for AUR, Flatpak remotes and crates.io, so
  packages that are not installed yet can be found and installed without
  leaving the app; AUR results include votes, popularity, maintainer and
  out-of-date state
- **Transaction preview** before install/remove/update: pacman plans the
  transaction with `--print`, so dependencies, recursive removals, download
  size and disk change are visible before confirming
- **Explicit database sync** for pacman and Flatpak appstream metadata, with
  the partial-upgrade warning, followed by a list reload and update check
- **Backend diagnostics** that reports every missing tool (`pacman`, `sudo`,
  `checkupdates`, `paccache`, `paru`/`yay`, `flatpak`,
  `cargo-install-update`, mise/rustup/fvm) and warns in the toolbar
- Update detection per backend with available versions
- **Installed / not installed / all** list filters (installed by default),
  backed by the full pacman sync catalogue so uninstalled repository
  packages can be searched, inspected and installed directly
- **Package origin filter** with every repository present in the current
  backend and install-state scope, so e.g. selecting Pacman only offers the
  pacman repositories rather than AUR, Flatpak or cargo origins
- **Multi-select mode** for batch install/uninstall: the checkboxes appear
  on list rows and the selected count, select-all, clear, install and
  uninstall actions sit beside the search field, so they never take a row
  away from the list; a **Clear filters** action resets the search, install
  state, origin, group, update/orphan filters and exits multi-select
- **Sorting** by name, installed/download size, install date, build date,
  origin or update status, with a remembered direction
- Orphan filter for pacman/AUR with bulk removal, plus explicit/dependency
  install-reason tags and one-click promotion to explicitly installed
- **Ignore updates** per package (stored in the config and passed to pacman
  and the AUR helper as `--ignore`), with an Ignored badge
- Rich pacman metadata in the detail pane — architecture, build date,
  download and installed size, package groups, provides, optional
  dependencies and conflicts — with package groups also shown as pill tags
  on each list row; clicking a group chip filters the list to that group and
  shows a removable filter chip
- A **resizable master/detail split** that starts at a 3:2 ratio and keeps
  the dragged position between launches, alongside the remembered activity
  log height
- **Cache cleanup** for pacman and the AUR helper: shows both cache sizes
  and contents (versions, sizes, installed/old status, cached AUR sources),
  and uses paccache (falling back to `pacman -Sc`), plus the helper's native
  `-Sc` / `-Scc` flags; unused Flatpak runtimes can be removed too
- **Package snapshots** using each backend's native export where available
  (pacman/AUR installed lists, Flatpak refs, cargo's install list) with a
  fallback to the normal listing; every entry keeps its backend, repository,
  orphan state and — for pacman/AUR — the explicit/dependency reason, and any
  two or more snapshots can be compared — or filtered by backend and type —
  with added / removed / version-changed packages highlighted; snapshots can
  be exported/imported as JSON and the newest one can restore missing packages
- **Package history** parsed from `/var/log/pacman.log` with
  installed/upgraded/removed/reinstalled/downgraded filters
- **Package files** view (`pacman -Ql`) with modified backup files and
  `.pacnew`/`.pacsave` remnants
- Per-package install/uninstall/update in the detail pane, plus a bulk
  update action while the update filter is active and an **Update all**
  entry that covers every enabled backend; both follow the selected chip
- **Download only**, **reinstall**, **downgrade from the package cache** and
  **install a local package** (`pacman -U`) from the More menu and the list
  context menu
- Clickable forward and reverse dependency chips for pacman/AUR packages,
  with back/forward history navigation between viewed packages, and a
  recursive **dependency tree** window
- **Right-click context menu** on every list row for the common package
  actions, copy-name and opening the AUR page
- Search improvements: `Ctrl+F` focus, a clear button, remembered recent
  searches, and matching against provides and groups as well as names,
  ids and descriptions

- Native **Wayland** windowing when the compositor is available, with an
  automatic X11/XWayland fallback otherwise
- Stop a running refresh/update/operation with the footer button or `Esc`,
  with separate timeouts for local queries and for update
  checks/downloads/builds
- Automatically retries update checks and failed downloads/operations on
  weak connections, and makes makepkg's curl retry truncated transfers
- Asks for the sudo password inside the app when no cached credentials are
  available, including for the sudo calls that paru/yay make while building
  and installing; a rejected password keeps the prompt open for a retry
  (with a show-password toggle), and cache cleanups are confirmed first
- A collapsible, resizable **activity log** at the bottom shows the commands
  being run and their live output, grouped per command so parallel commands
  never interleave, with a sticky header that keeps the current command in
  view; expected non-zero results such as "no orphans" or "no updates" are
  shown as neutral notes instead of failures, while detail visibility, panel
  state and height are remembered (copy/clear included)
- The footer progress bar follows the percentage reported by a running
  command instead of staying indeterminate
- **Arch news and mirror health** from the official feeds, fetched through
  the configured proxy, with the current mirrorlist entries matched against
  the mirror status JSON
- Proxy support (HTTP or SOCKS5) with per-backend enable switches; online
  search and news/mirror lookups honour the same proxy
- Light / dark / system theme and English / Simplified Chinese UI
- `quickpackagerc` settings in the established format, so existing
  configurations keep working

## Requirements

- .NET SDK 10 (C# 14)
- Linux desktop with Wayland or X11; the package backends are Arch-specific

## Project layout

```
QuickPackage.slnx              solution (root)
src/QuickPackage/              the Avalonia application
data/                          desktop entry and AppStream metadata
```

## Build and run

```bash
dotnet build                       # builds QuickPackage.slnx
dotnet run --project src/QuickPackage/QuickPackage.csproj
```

## Publish

```bash
dotnet publish src/QuickPackage/QuickPackage.csproj \
  -c Release -r linux-x64 --self-contained false
# binary in src/QuickPackage/bin/Release/net10.0/linux-x64/publish/QuickPackage
```

Install the desktop entry from `data/` next to the binary (or into
`~/.local/share/applications`) so it shows up in the application menu.

## Configuration

Settings live in `$XDG_CONFIG_HOME/quickpackagerc` (default
`~/.config/quickpackagerc`):

- `[General]` — language, theme, startup update check, the local query
  timeout (`CommandTimeout`), the update/download timeout
  (`DownloadTimeout`), the network retry count (`NetworkRetries`), the AUR
  helper (`paru`/`yay`), the master/detail split ratio
  (`SplitRatioPercent`, default 60 for a 3:2 layout), the list sort key and
  direction (`SortKey`, `SortDescending`), ignored packages
  (`IgnoredPackages`), recent searches (`SearchHistory`), the activity log's
  expanded state, height and detail visibility, and one `Enable*` key per
  backend
- `[Proxy]` — mode, host, port and one `Enable*` key per backend

Snapshots are stored next to it in
`$XDG_CONFIG_HOME/quickpackage-snapshots.json`.
