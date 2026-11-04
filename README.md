# QuickPackage

A desktop package manager for Arch Linux and developer toolchains, built with
C# 14 and Avalonia.

## Features

- **System packages**: pacman (including AUR via `paru`/`yay`), Flatpak, cargo
- **Developer tools** (optional, disabled by default): mise, rustup, fvm
- A prominent **System packages / Developer packages** switch keeps the two
  worlds apart: each side has its own filter chips, counters and update flow
- Update detection per backend with available versions
- Install / remove / update selected packages, with a master-detail pane
- A collapsible, resizable **activity log** at the bottom shows the commands
  being run and their live output (copy/clear included)
- Proxy support (HTTP or SOCKS5) for every backend command
- Light / dark / system theme and English / Simplified Chinese UI
- `quickpackagerc` settings in the established format, so existing
  configurations keep working

## Requirements

- .NET SDK 10 (C# 14)
- Linux desktop; the package backends are Arch-specific

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

- `[General]` — language, theme, startup update check, the activity log's
  expanded state/height and one `Enable*` key per backend
- `[Proxy]` — mode, host and port
