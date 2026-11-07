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
- Update detection per backend with available versions
- Orphan filter for pacman/AUR with bulk removal, plus explicit/dependency
  install-reason tags and one-click promotion to explicitly installed
- **Cache cleanup** for pacman and the AUR helper: shows both cache sizes
  and contents (versions, sizes, installed/old status, cached AUR sources),
  and uses paccache (falling back to `pacman -Sc`), plus the helper's native
  `-Sc` / `-Scc` flags
- **Package snapshots** using each backend's native export where available
  (pacman/AUR installed lists, Flatpak refs, cargo's install list) with a
  fallback to the normal listing; every entry keeps its backend, repository,
  orphan state and — for pacman/AUR — the explicit/dependency reason, and any
  two or more snapshots can be compared — or filtered by backend and type —
  with added / removed / version-changed packages highlighted
- Per-package install/uninstall in the detail pane, plus a bulk update
  action while the update filter is active; both the update count and the
  bulk action follow the selected backend chip
- Native **Wayland** windowing when the compositor is available, with an
  automatic X11/XWayland fallback otherwise
- Stop a running refresh/update/operation with the footer button or `Esc`,
  and a configurable timeout for read-only queries
- Asks for the sudo password inside the app when no cached credentials are
  available, including for the sudo calls that paru/yay make while building
  and installing; a rejected password keeps the prompt open for a retry
  (with a show-password toggle), and cache cleanups are confirmed first
- A collapsible, resizable **activity log** at the bottom shows the commands
  being run and their live output, grouped per command so parallel commands
  never interleave, with a sticky header that keeps the current command in
  view; detail visibility, panel state and height are remembered
  (copy/clear included)
- Proxy support (HTTP or SOCKS5) with per-backend enable switches
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

- `[General]` — language, theme, startup update check, query timeout, the
  AUR helper (`paru`/`yay`), the activity log's expanded state, height and
  detail visibility, and one `Enable*` key per backend
- `[Proxy]` — mode, host, port and one `Enable*` key per backend

Snapshots are stored next to it in
`$XDG_CONFIG_HOME/quickpackage-snapshots.json`.
