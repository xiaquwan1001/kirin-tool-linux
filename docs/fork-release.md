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
Qt/GTK-free (Avalonia/Skia). `postinst` reloads udev rules and runs a non-fatal runtime self-check; add
yourself to `adbusers` for non-desktop sessions.

### Dependency policy (ICU / OpenSSL)

Two runtime dependencies cannot be pinned to a single package name, because their package names change
between distributions and dpkg alternatives have **no wildcard support** — `Depends:` can only list
exact package names joined with `|`:

| Dependency | Written as | Why |
|---|---|---|
| ICU | `libicu78 \| libicu76 \| libicu74 \| libicu72 \| libicu71 \| libicu70 \| libicu66 \| libicu-dev` | the ICU `soname` grows with every release (20.04 = 66, 22.04 = 70, 24.04 = 74, 26.04 = 78) |
| OpenSSL 3 | `libssl3t64 \| libssl3 \| libssl1.1` | 24.04+ renamed `libssl3` to `libssl3t64` for the 64-bit `time_t` transition |

The deb ships a **self-contained** .NET app, but .NET still P/Invokes the *system* ICU (globalization) and
OpenSSL, so the packages must be satisfiable from the target archive. Hard-coding one soname is what
broke installs before: the file listed `libicu76 | libicu74 | ... | libicu66` and Ubuntu 26.04 (only
`libicu78`) therefore rejected the package with `Unable to satisfy dependencies`. A hard-coded list is
always one release behind, so the list is newest-first, keeps `libicu66` for the glibc 2.31 container
builds, and ends with `libicu-dev`, which exists in every archive and pulls the matching runtime.

Both lists live in one place at the top of `packaging/build-deb.sh` (`ICU_DEPENDS` / `SSL_DEPENDS`); a
new distribution means adding one alternative there and nothing else.

Guards against regression:

- `packaging-smoke` in `cli-mcp-ci.yml` runs on a matrix (`ubuntu-latest` + `ubuntu-24.04` +
  `ubuntu-26.04`) and, after building a deb from a synthetic publish tree, runs
  `apt-get -s --no-install-recommends install ./dist/*.deb`. A simulation resolves the real `Depends`
  against that runner's archive without installing anything (no root needed);
  "`but it is not installable`" / "`Unable to satisfy`" fails the job.
- `packaging/check-depends.sh <deb>` does the same check locally against *this* host's archive, so a
  downloaded release asset can be validated before `sudo apt install`.
- `postinst` additionally probes `ldconfig -p` for `libicuuc.so*` at install time and prints actionable
  advice (`apt install libicu-dev`, or `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` as a last resort). The
  check is diagnostic only: it never fails the installation.

## Releasing

```bash
cd ~/src/kirin-tool-linux
git switch main && git pull
git tag -a v2.4.12 -m "Kirin Tool for Linux 2.4.12 (fork build)"
git push origin v2.4.12       # release-linux.yml builds + attaches the assets
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
./packaging/build-deb.sh publish-out 2.4.12 cli-out/KirinTool.Cli dist # deb (needs dpkg-deb)
./packaging/check-depends.sh dist/kirin-tool-linux_2.4.12_amd64.deb      # apt simulation, no root
./packaging/build-appimage.sh publish-appimage 2.4.12                 # needs glibc <= 2.31 + gcc-10
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
