# Kirin Tool for Linux · refresh

[English](README.md) · **[简体中文](README.zh-CN.md)**

**Huawei / HiSilicon Kirin devices from Linux: UPDATE.APP extraction, a Huawei-parity fastboot engine and
real Full-OTA writes — through a GUI, a headless CLI and an MCP server that share a single implementation,
with every destructive step gated, confirmed and backed up first.**

[![Latest release](https://img.shields.io/github/v/release/xiaquwan1001/kirin-tool-linux-refresh?style=flat-square&color=5865f2)](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest)
[![Release build](https://img.shields.io/github/actions/workflow/status/xiaquwan1001/kirin-tool-linux-refresh/release-linux.yml?style=flat-square&label=release%20build)](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/actions/workflows/release-linux.yml)
[![Platform](https://img.shields.io/badge/Linux-x86__64-1793d1?style=flat-square&logo=linux&logoColor=white)](#install)
[![License](https://img.shields.io/badge/source_available-BSL_1.1-8b5cf6?style=flat-square)](LICENSE)

[**Download**](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest) ·
[Why "-refresh"](#why-this-repo-is-called--refresh) ·
[Changes vs upstream](#what-this-line-changes-vs-upstream) ·
[Design trade-offs](#design-decisions-worth-arguing-about) ·
[Field notes](#field-notes--what-flashing-a-kirin-device-actually-looks-like) ·
[Safety model](#safety-model) · [Install](#install) · [CLI & MCP](#cli--mcp-quick-start) · [Docs](#documentation)

> [!WARNING]
> Flashing, unlocking and rebranding can erase data or leave a device unbootable. A successfully built
> host tool proves nothing about your device. Back up, match firmware to model, read the
> [safety model](#safety-model) before writing anything. You own the device operations.

This repository is a **technical record first**: what this line changes relative to the upstream Linux
port, why each of those changes exists, and what we actually observed while flashing a Kirin 990 5G
device on Linux. Usage-oriented, less formal write-ups live on the community side (酷安), not here.

---

## Why this repo is called `-refresh`

The code lineage is three steps deep, and two of them share the same repository name:

| | Project | What it is |
| :--- | :--- | :--- |
| Upstream of everything | [kethily-daniel/Kirin-Tool](https://github.com/kethily-daniel/Kirin-Tool) | original Windows/WPF tool, BSL 1.1 at v2.4.2 |
| The repo this is forked from | [Redminote11tech/kirin-tool-linux](https://github.com/Redminote11tech/kirin-tool-linux) | independent Avalonia port of v2.4.2 to Linux |
| **This repository** | `xiaquwan1001/kirin-tool-linux-refresh` | the maintained **refresh line** of that port |

Three reasons the suffix is part of the name, not just decoration:

1. **A plain fork is indistinguishable.** The inherited repo is literally named `kirin-tool-linux`;
   a fork with the same name makes "which one do I clone / which release do I install" ambiguous in
   search results and in every link people paste.
2. **This is a divergence, not a mirror.** Upstream replaced BSL 1.1 with a view-only *Kirin-Tool
   Read-Only Reference License* on 2026-09-11; this line **stopped syncing** at that point. Every build
   after that is our own work on top of the BSL-era v2.4.2 base — calling it an upstream mirror would
   be factually wrong. Provenance and the license position are recorded in [docs/FORK-NOTICE.md](docs/FORK-NOTICE.md).
3. **"refresh" is what actually happened to it.** The line was re-audited end to end (write paths,
   MCP contract, GUI success criteria, packaging), re-verified on real hardware, and given its own
   release pipeline. The name should say so.

Channel split, for the record: **this repository is the technical showcase (diffs, rationale,
hardware findings, honest limits)**, while the community posts (酷安) are the practical showcase (what it is
nice for, how to use it). If the two ever disagree, this repository is the source of truth.

## What this line changes vs upstream

Baseline = the inherited Avalonia port of upstream v2.4.2/v2.4.8, as taken over. Everything below is
added or changed afterwards; the exhaustive per-release list is in
[docs/refresh-changelog.md](docs/refresh-changelog.md).

| Change | Why it exists |
| :--- | :--- |
| **CLI + MCP server as first-class entries** (`kirin-tool-cli`, `Kirin-Tool --mcp`) sharing one `CliRunner` with the GUI | flashing is scriptable and agent-driven now; three code paths would drift apart within a week. One implementation, three faces. |
| **`CliWriteKind` generates every contract string** (MCP `initialize.instructions`, each tool's `confirm` description, CLI help) | an earlier build described `flash_ota` as "gate-only in this build" while it really wrote to the device. Generated text cannot drift from the implementation. |
| **Machine-readable `gate`: `plan-only` / `refused` / `confirmed` / `applied`**, and a refusal emits exactly **one** JSON document | clients branched on exit codes only — and 4 means both "not confirmed" and "refused". Two JSON documents on the refusal path also broke parsers. |
| **Redline override is CLI-only**: MCP rejects `allow-redline` with JSON-RPC `-32602` | identity/security partitions (`oeminfo`, `nvme`, `persist`, `modem_secure`, `modemnvm*`, `certification`, `hisee_encos`, `veritykey`, …) are effectively irreversible. A human types the override; an AI client cannot grant it to itself. |
| **Success = exit 0 *and* device `OKAY`** | a rejected write used to render as "flash completed" because the process exited 0. Now a temporary loader's "already fastboot unlocked" is reported as *no write*. |
| **SN (NVE) rewrite and FRP erase require an explicit dialog showing old vs new values**; USB Update re-confirms redline partitions discovered only after unpacking | these were one click away from silent execution, and unpacking can surface partitions the pre-flight check never saw. |
| **Automatic pre-op backup** before every real write (redline partitions of the plan + `ptable`, timestamped dir, `manifest.json` with size/SHA256/status) | the write we can most easily regret is the one we cannot undo. Details and flags below. |
| **Low-battery banner (warning only)** | see [why it is not a gate](#why-the-battery-is-a-warning-and-not-a-gate). |
| **`flash-xml` skips identifiers the loader does not expose** (warning), `--strict-names` refuses with exit 3 | a human-authored XML may legitimately mention aliases; failing hard by default blocked nothing useful but confused everybody. |
| **Plan-only commands really never write** (`vcom-unlock`, `oeminfo`, `downgrade-enable` → exit 4 always) | see the oeminfo field note: those writes are not offline-computable, so the honest product is a plan. |
| **Own release pipeline** (deb / AppImage / tar.gz / single-file CLI from one commit) with a deb `Depends` policy and `apt-get -s` simulation on three Ubuntu generations | upstream releases never matched Linux runtime reality: ICU sonames move every release, `libssl3` became `libssl3t64` on 24.04+, and dpkg alternatives have no wildcards. [docs/fork-release.md](docs/fork-release.md). |
| **Dual CI, single source of truth for tests** (`tests/suites.txt`, 17 hardware-free harnesses, GitHub Actions + GitLab CI) | a test that only one pipeline runs is a test that will rot. `FlashPolicy` now fails hard when the CLI binary is missing instead of skipping. |
| **`StableParity` hash boundary** around `loaders/`, the fastboot payload, `SuperMerger`, `VcomFlasher`, USB Update | hardware blobs and serial protocol must not move by accident; only behaviour-preserving edits are allowed there. |
| **Crash handlers and robustness fixes** (process-tree kill on timeout, `SerialPort` leak, per-instance temp files, truncated sparse header rejection) | a GUI that dies silently mid-flash is worse than one that reports a crash. |

## Design decisions worth arguing about

**Backups are best effort, not a gate.** A temporary VCOM loader typically answers `invalid command` to
`oem dump-emmc` / `oem dump-storage` — exactly the loader we use for the one flashing path that works
on ELS-AN00. Making an incomplete backup block the write would therefore block the working path and
push people to `--no-backup` permanently. So: it degrades, records `unsupported`/`failed` per partition
with a reason, warns on stderr/UI, and offers `--require-backup` for those who want the strict version.
Cost: silence is possible if you ignore warnings — which is why the manifest states per-partition status
instead of just "backup done".

**The MCP path has no dry-run, but still cannot redline-override.** Faking a dry-run would be the same
lie the old "gate-only" wording told. Instead the contract is explicit ("`confirm: true` performs the
real write; there is no dry-run in the MCP path"), the redline refusal is structural rather than
policy-based, and every call returns a `gate` value a client can branch on without parsing prose.

**`flash-ota` and `flash-xml` default differently on unknown identifiers** — skip (ota) vs skip-with-warning
(xml, `--strict-names` to refuse). The OTA plan comes from a firmware package: an identifier the loader
does not expose means a firmware/loader mismatch you should notice. An XML plan is authored by a human
and may mention aliases that only resolve on some loaders; refusing by default there would reject
legitimate configurations.

**The battery level is a warning and not a gate.** The read is best effort (temporary loaders usually
answer `invalid command`), and a gate built on an unreliable read either blocks everything or protects
nothing. So the 50 % rule stays a human convention, printed as a non-modal banner, with an honest
"could not read" line instead of a fake percentage.

**The native fastboot engine is not optional.** Google platform-tools 37 fails wholesale against Huawei
loaders (`getvar partition-size` and friends), so the bundled Huawei-parity client is the only thing
that talks to these devices; see [docs/native-fastboot-parity-v2.4.5.md](docs/native-fastboot-parity-v2.4.5.md)
for what is reconstructed, what is tested and what is honestly unproven.

## Field notes — what flashing a Kirin device actually looks like

Everything here was learned on a Huawei P40 Pro (ELS-AN00, Kirin 990 5G) from Linux. It is the reason
the safety model looks the way it does.

| Symptom / belief | What is really going on |
| :--- | :--- |
| "fastboot sees the device, so the tool is broken" | The device in `18d1:d00d` may be a *temporary* fastboot that exists only in RAM after a BootROM injection — `reboot` erases it. It is not a phone you can boot from. |
| `oem unlock` says "already fastboot unlocked" | Meaningless on the temporary loader. Unlocking can only happen on the board (BD) bootloader; a retail bootloader answers `invalid command`. And `oem get-bootinfo` reporting `unlocked` is a lie — only a write probe tells the truth. |
| "the loader printed a partition table, let's script it" | The temporary loader publishes 90 entries including factory ones and without `kpatch`; the real GPT has 74. Never treat the loader's ptable as a partition map. |
| "I flashed every partition manually, that is equivalent to Full-OTA" | It is not: Full-OTA also carries `ptable`, the official `userdata`/`cache` skeleton and version metadata. Use `flash-ota`. |
| "the package extracted fine, so the plan is fine" | Base `UPDATE.APP` SUPER comes as **two sparse segments** that must be merged; taking only the large one writes ~2.1 GiB of zeros and guarantees a black screen. |
| "the GUI works, the BD package should too" | BD packages can ship a **gzip-compressed** SUPER. The CLI detects the `0x1f8b` magic and decompresses; the GUI merger path does not yet, which shows up as a 45/46 half-flash. Known gap, listed below. |
| "cross-version is fine, the GPT has 74 entries either way" | Both do — but `boot` 25→30 MB, `super` 6728→6992 MB, `kpatch` 66→61 MB, `preas` 296→32 MB. Write `ptable` first. |
| "mix the newest xloader with the loader I already had" | Boot-chain suites are same-generation or nothing: mixing produces a black device with zero enumeration or `load hhee failed`. Intermittent `12d1:3609` appearances mean the chain is failing; recovery is a manual pin short — there is no software EDL exit. |
| "just edit `Display_Version` in oeminfo to skip a version check" | `oeminfo` is 96 MiB sparse with 32 B-aligned sections and an HMAC keyed per device, which cannot be recomputed offline. That is why `oeminfo` is plan-only in the CLI/MCP. |
| "serial open failed, must be a permissions problem" | On `12d1:3609` with the `option` driver, pyserial fails with `errno 71` (DTR stall). Raw `os.open` + termios 8N1, never touching DTR/RTS, is what works. A half-open BootROM session (HEAD sent, payload not) answers all zeros — unplug, hold power 15 s, re-short. Do not "fix" the frame format. |
| "flashing died at 60 %, the tool is at fault" | Check the battery first: mid-write power loss is unrecoverable on these devices. Keep ≥ 50 %; the tool warns, it does not block. |

The one end-to-end result worth quoting: `flash-ota` through a temporary VCOM loader on ELS-AN00 →
**46/46 partitions OKAY, device boots ~22 s after reboot**.

## Safety model

**Writes:** `flash-ota`, `flash-xml` (CLI/MCP) and the GUI flashing / USB Update / rescue workflows. Real
writes — there is no dry-run after confirmation. `flash-xml` skips identifiers the loader does not
expose; `--strict-names` refuses instead (exit 3).

**Never writes:** `vcom-unlock`, `oeminfo`, `downgrade-enable` — plan-only, exit 4, even with `--yes`.

**Redline partitions** are refused unless a human overrides: `--allow-redline` in the CLI, a dialog
listing the exact partitions in the GUI, and nothing at all over MCP.

**Gate signals.** Every write command with `--json` and every MCP write tool reports `gate`:
`plan-only`, `refused` (with `reason`), `confirmed` (plan acknowledged, nothing written) or `applied`.
Branch on `gate`, not on the exit code. Exit codes: 0 ok / 1 usage / 2 no device / 3 device refused
(includes `--strict-names`) / 4 gate refusal / 5 file or verification failure.

**Pre-op backup.** Before the first real write, the redline partitions of the plan plus `ptable` are
dumped into a timestamped directory (`--backup-dir`, else `$KIRIN_BACKUP_DIR`, else
`~/.local/share/kirin-tool/backups/`) with a `manifest.json` (tool version, UTC time, fastboot path,
device model/SN when readable, trigger command, write plan, and per-partition file/size/SHA256/status).
Existing backup directories are never overwritten. `--require-backup` turns an incomplete backup into a
refusal; `--no-backup` skips it. A read-only `backup` command does the same on demand.

Unverified behaviour stays unverified — see [Verified / not verified](#verified-on-hardware--not-verified).

## Install

Prebuilt files are on the [releases page](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest);
no separate .NET runtime is needed. Asset names, deb layout, dependency policy:
[docs/fork-release.md](docs/fork-release.md).

| Format | Install |
| :--- | :--- |
| **.deb** | `sudo apt install ./kirin-tool-linux_<ver>_amd64.deb` — self-contained; installs `kirin-tool` (GUI) + `kirin-tool-cli` (CLI/MCP) + udev rules |
| **AppImage** | `chmod +x kirin-tool-linux-<ver>-x86_64.AppImage && ./kirin-tool-linux-<ver>-x86_64.AppImage` (no FUSE2? add `--appimage-extract-and-run`) |
| **tar.gz** | `tar xf kirin-tool-linux-<ver>-x86_64.tar.gz -C ~/opt/kirin-tool-linux && ~/opt/kirin-tool-linux/Kirin-Tool` |
| **CLI binary** | download `kirin-tool-cli-<ver>-linux-x86_64`, `chmod +x`, run — doubles as an MCP stdio server |

Asset and package names intentionally keep the historical `kirin-tool-linux` string: renaming the
repository must not silently change the deb package name (that would break upgrades).

If USB access is denied, add yourself to the right groups and reconnect:

```bash
sudo usermod -aG plugdev,uucp "$USER"   # Debian/Ubuntu (adbusers on Arch/CachyOS)
sudo udevadm control --reload-rules && sudo udevadm trigger
```

## CLI & MCP quick start

```bash
kirin-tool-cli doctor                                   # what's connected, which fastboot/adb was picked
kirin-tool-cli info                                     # device info, capabilities, rescue plan
kirin-tool-cli extract UPDATE.APP out/                  # extract all partitions
kirin-tool-cli flash-ota base.app --json                # prints the write plan, gate=plan-only, exit 4
kirin-tool-cli flash-ota base.app --yes                 # REAL WRITE of all non-redline partitions
kirin-tool-cli backup --json                            # read-only dump of identity partitions + manifest
```

MCP server (newline-delimited JSON-RPC 2.0 over stdio):

```json
{
  "mcpServers": {
    "kirin-tool": {
      "command": "/path/to/kirin-tool-cli",
      "args": ["mcp"]
    }
  }
}
```

Tools: `info`, `doctor`, `getvar`, `extract`, `dump`, `backup`, `flash_ota`, `flash_xml`,
`vcom_unlock`, `oeminfo`, `downgrade_enable`. Write tools require `confirm: true` and never accept
`allow-redline`. Full command table and gates: [docs/cli-mcp.md](docs/cli-mcp.md).

## Screenshots

Headless renders of the v2.4.13 UI, captured from the real Avalonia controls (commit `47fb3da`) — no
device was connected, so device fields show the UI's native "Not Read" state. Provenance per file:
[docs/screenshots.md](docs/screenshots.md).

| Main window (light) | Main window (dark) |
| :-- | :-- |
| ![Main window, light theme](docs/images/screenshot-main.png) | ![Main window, dark theme](docs/images/screenshot-dark.png) |

| UPDATE.APP extractor | Flash progress (pre-op backup line at the bottom) |
| :-- | :-- |
| ![Extractor with a loaded UPDATE.APP](docs/images/screenshot-extractor.png) | ![Flash progress dialog](docs/images/screenshot-flash-progress.png) |

| Redline confirmation | CLI / MCP output |
| :-- | :-- |
| ![Redline confirmation dialog](docs/images/screenshot-redline-confirm.png) | ![kirin-tool-cli doctor and flash-ota plan](docs/images/screenshot-cli-doctor.png) |

## Verified on hardware / not verified

| Verified on hardware | Not verified |
| :--- | :--- |
| **Huawei P40 Pro (ELS-AN00)**: `flash-ota` through a temporary VCOM loader — 46/46 partitions written, boots ~22 s after reboot | Other devices and chipsets (Kirin 980 / 9000 evidence exists from earlier work, but no full OTA run) |
| Device information, fastboot reads and extraction on the same device | BD-package gzip SUPER images (the GUI merger path is not validated on hardware) |
| Kirin 990 5G loader families used by the ELS-AN00 rescue flow | Kirin 990 4G (**unsupported** — do not substitute 5G loaders), Kirin 9000 rescue writes (device-rejected), hardware USB Update equivalence |

## Known gaps

- GUI-side gzip SUPER handling is missing (CLI has it) → BD packages can end in a 45/46 half-flash.
- Battery level cannot be read reliably on temporary loaders; the 50 % rule remains a human convention.
- `VcomOpenProbe` / `UsbUpdateProbe` are excluded from CI (real USB hardware); `VisualParity` is excluded
  (font-dependent pixel diff).
- Only one device family has a full-OTA run.

## Documentation

| Guide | Details |
| :--- | :--- |
| [Docs index](docs/INDEX.md) | All guides in one table. |
| [Refresh changelog](docs/refresh-changelog.md) | Per-release changes of this line, with reasons. |
| [CLI & MCP](docs/cli-mcp.md) | Commands, write kinds, gates, exit codes, MCP protocol. |
| [Fork release pipeline](docs/fork-release.md) | Assets, deb layout, ICU/OpenSSL dependency policy, CI map. |
| [Fork provenance](docs/FORK-NOTICE.md) | License position and upstream relationship. |
| [UPDATE.APP extraction](docs/partition-extractor.md) · [AppImage & udev](docs/portable-appimage.md) · [Native fastboot parity](docs/native-fastboot-parity-v2.4.5.md) | Inherited-baseline guides. |
| [v2.4.13 release notes](docs/release-notes/v2.4.13.md) · [screenshots](docs/screenshots.md) | This release, and how the screenshots were captured. |
| [Community post (酷安)](https://www.coolapk.com/feed/75025716) | Usage-oriented introduction of the refresh line, archived as [docs/coolapk/refresh-post.md](docs/coolapk/refresh-post.md). Technical record stays here. |

### Build from source

Install the **.NET 10 SDK** and the [native build prerequisites](fastboot-modern/README.md), then:

```bash
git clone https://github.com/xiaquwan1001/kirin-tool-linux-refresh.git
cd kirin-tool-linux-refresh
./packaging/publish.sh publish-out
./publish-out/Kirin-Tool
```

### Issues

[Open an issue](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/issues) with the app version,
distribution, device model/chipset, firmware, connection mode, attempted operation and the relevant
error lines. Redact serial numbers, PSIDs and IMEIs before sharing logs.

## Credits and license

This Linux line derives from upstream **v2.4.2** (BSL 1.1 at fork time), ported to Avalonia by
**[Redminote11tech](https://github.com/Redminote11tech)**, and is maintained independently by
**[xiaquwan1001](https://github.com/xiaquwan1001)**. It does not sync upstream after 2026-09-11.
Original copyright notices are retained.

- **Kethily Daniel & NDXCode** — original Kirin-Tool authors.
- **[TASZK Security Labs SL](https://taszk.io)** — head resend bootrom exploit.
- **[kasnria001](https://github.com/kasnria001)** — decrypted xloaders and community help.
- **Redminote11tech** — Linux port and maintenance (upstream of this line).

Distributed under **BSL 1.1** (see [LICENSE](LICENSE)) — non-production, personal, non-commercial use.
Not affiliated with, sponsored by or endorsed by the upstream authors or Huawei.
