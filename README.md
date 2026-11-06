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
- Per-package install/uninstall in the detail pane, plus a bulk update
  action while the update filter is active
- Native **Wayland** windowing when the compositor is available, with an
  automatic X11/XWayland fallback otherwise
- Stop a running refresh/update/operation with the footer button or `Esc`,
  and a configurable timeout for read-only queries
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
