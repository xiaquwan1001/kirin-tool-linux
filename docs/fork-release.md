# Fork release pipeline (deb · AppImage · binaries)

This fork ships its own builds instead of waiting for upstream releases. Everything is produced from
the fork's `main` by `.github/workflows/release-linux.yml` in an `ubuntu:20.04` container, so the
binaries stay compatible with **glibc 2.31** (the AppImage script refuses to build on newer glibc).

## What a release contains

| Asset | What it is | Notes |
|---|---|---|
| `kirin-tool-linux-<ver>-x86_64.AppImage` | portable GUI | no install; bundled runtime, fastboot and loaders |
| `kirin-tool-linux_<ver>_amd64.deb` | GUI + headless CLI | self-contained; installs to `/usr/lib/kirin-tool-linux` |
| `kirin-tool-linux-<ver>-x86_64.tar.gz` | same tree as the deb | for `~/opt` style installs, no root |
| `kirin-tool-cli-<ver>-linux-x86_64` | single-file CLI | ~49 MB compressed bundle; also an MCP stdio server |
| `SHA256SUMS-<ver>.txt` | checksums | verify before flashing anything |

All four come from the same commit and are smoke-tested in CI before upload:
`KirinTool.Cli --help | grep flash-ota`, `KirinTool.Cli version`, `fastboot --version` (must report the
Huawei-parity build), and presence of `loaders/hisi990/fastboot.ktl` and `libkirin_usb_update.so`.

## Debian package layout

```
/usr/lib/kirin-tool-linux/        app tree (Kirin-Tool, KirinTool.Cli, fastboot/, loaders/, libs, docs)
/usr/bin/kirin-tool               → GUI
/usr/bin/kirin-tool-cli           → headless CLI / MCP server
/usr/bin/kirin-usb-update-probe   → USB Update diagnostics
/usr/bin/kirin-vcom-open-probe    → VCOM open diagnostics
/usr/lib/udev/rules.d/51-kirin-tool-fastboot.rules
/usr/share/applications/kirin-tool.desktop
/usr/share/licenses/kirin-tool-linux/   (BSL-1.1 + third-party notices)
```

`Depends` covers glibc, X11/ICE/SM, fontconfig/freetype, libusb, zlib, ICU and OpenSSL. The app tree is
Qt/GTK-free (Avalonia/Skia). `postinst` reloads udev rules; add yourself to `adbusers` for non-desktop
sessions.

## Releasing

```bash
cd ~/src/kirin-tool-linux
git switch main && git pull
git tag -a v2.4.9 -m "Kirin Tool for Linux 2.4.9 (fork build)"
git push origin v2.4.9        # release-linux.yml builds + attaches the assets
gh run watch                  # optional
```

A `workflow_dispatch` run builds the same artifacts **without** publishing a release, which is how the
pipeline is validated before tagging.

## Building the same artifacts locally

```bash
./packaging/publish.sh publish-out                                   # GUI tree + native fastboot
DOTNET_ROLL_FORWARD=Major dotnet publish KirinTool.Cli/KirinTool.Cli.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o cli-out
./packaging/build-deb.sh publish-out 2.4.9 cli-out/KirinTool.Cli dist # deb (needs dpkg-deb)
./packaging/build-appimage.sh publish-appimage 2.4.9                 # needs glibc <= 2.31 + gcc-10
```

On a modern host you can build the deb and the CLI binary; **the AppImage must come from a glibc 2.31
container** (`docs/portable-appimage.md`). `packaging/build-deb.sh` is smoke-tested on every push by the
`packaging-smoke` job in `.github/workflows/cli-mcp-ci.yml`.

## CI map

| Workflow | Trigger | Purpose |
|---|---|---|
| `cli-mcp-ci.yml` | push/PR to `main`, `feat/cli-and-mcp-entry` | build both projects, CLI/MCP smoke tests, deb packaging smoke |
| `release-linux.yml` | tag `v*`, manual dispatch | deb + AppImage + tar.gz + CLI binary, uploads, GitHub release |
| `build.yml` | manual dispatch only (fork) | upstream parity build (app bundle, AppImage, pacman) |
