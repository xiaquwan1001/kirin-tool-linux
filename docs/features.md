# Features

One implementation, three entry points: the **Avalonia GUI**, the **headless CLI** (`KirinTool.Cli`), and the **MCP server** (`kirin-tool-cli mcp` / `Kirin-Tool --mcp`). Command/gate semantics: [cli-mcp.md](cli-mcp.md).

## Firmware tools (offline, no device)

- **UPDATE.APP extraction** — Base / Cust / Preload archives, per-partition selection, numbered duplicate names, cancellation-safe; raw bytes only (no signature check, no SUPER merge). GUI and `extract` CLI command.
- **SUPER handling** — positional SUPER merger inherited from the baseline; the native fastboot engine adds liblp / wipe-super / image-plan workflows.

## Device operations

- **Device info** — model, vendor/country, lock state, firmware versions; NVE SN and OEM SN / PSID as separate queries; `info`, `doctor`, `getvar` CLI/MCP commands.
- **Huawei fastboot** — bundled native AOSP 35.0.2-based client with reconstructed Huawei extensions: dump/upload (DDR + eMMC geometry), UFS-eye, UltraFlash negotiation, staging/fetch, slots, fastbootd, logical partitions, boot-header/vbmeta support, TCP/UDP transports, bundled filesystem helpers. Coverage and limits: [native-fastboot-parity-v2.4.5.md](native-fastboot-parity-v2.4.5.md).
- **Real OTA flashing** — `flash-ota` writes a Full-OTA package partition-by-partition through a temporary VCOM loader, with name resolution against the loader's exposed partition table, per-partition OKAY verification and staging cleanup. Verified on a P40 Pro (ELS-AN00), 46/46 partitions. `flash-xml` writes a flashing.xml plan.
- **USB Update** — userspace libusb transport (vendor 12d1, interface ff/13/23), no DKMS module; VCOM loader path remains serial-based. See [usb-update-userspace.md](usb-update-userspace.md).
- **Rescue / unlock workflows** — loader-based rescue flashing, real `vcom-unlock` (VCOM loader upload, same services as the GUI, `--stage-only` split around the manual replug), plan-only `oeminfo` / `downgrade-enable` CLI commands.

## Safety (see README §Safety model)

- Redline partition gate shared by GUI, CLI and MCP; `--allow-redline` exists only in the CLI.
- Machine-readable `gate` signals (`plan-only` / `refused` / `confirmed` / `applied`) and exit codes 0–5.
- Write success requires a device `OKAY` reply, not just exit 0.
- Explicit GUI confirmations for SN rewrite, FRP erase, and USB Update redline partitions discovered after unpacking.
- Global unhandled-exception logging; plan-only commands never write; every VCOM session (GUI and CLI) leaves a
  frame-level audit log under `~/.local/state/kirin-tool/` (`vcom-<UTC>-<cpu>.log`).
- Pre-op backup before every real write: redline partitions of the plan + `ptable` into a timestamped directory with a SHA256 `manifest.json` (best effort; `--require-backup` / `--no-backup` / `--backup-dir`), plus a read-only `backup` command.

## Packaging & CI

- Self-published `.deb` (glibc 2.31, self-contained, udev rules + desktop entry), AppImage, tar.gz and single-file CLI — all from one commit, smoke-tested in CI. [fork-release.md](fork-release.md)
- Dual CI (GitHub Actions + GitLab CI) running 17 hardware-free test suites (`tests/suites.txt` is the single source of truth).
