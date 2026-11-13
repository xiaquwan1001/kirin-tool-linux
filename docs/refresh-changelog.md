# Refresh 变更记录 / Refresh line changelog

本文件记录 **refresh 线**（本仓库）相对接手基线的改动与原因。基线与上游关系见
[FORK-NOTICE.md](FORK-NOTICE.md)；面向使用的介绍见 [README](../README.md)。

Baseline: the inherited Avalonia port of upstream Kirin-Tool v2.4.2 (repo
`Redminote11tech/kirin-tool-linux`, released there as v2.4.8). Upstream relicensed to a view-only
license on **2026-09-11**; this line stopped syncing at that commit. Everything below is written here,
not pulled from upstream.

| Version | Theme | One-line result |
| :--- | :--- | :--- |
| v2.4.9 | first self-published release | deb + AppImage + tar.gz + single-file CLI, all from one commit, with install-level smoke checks |
| v2.4.10 | GUI quality of life | remembers last CPU model / files / folders, light + dark theme |
| v2.4.11 | .NET 10 migration, packaging fix, unlock/redline behaviour | packages install on modern Ubuntu again |
| v2.4.12 | redline policy pushed down to services, GUI false-success removal | device refusal can no longer render as success |
| v2.4.13 | audit + fix batch | MCP contract matches reality; pre-op backup; honest gates |
| v2.4.14 | three-surface parity, approval gate, honest verdicts | GUI, CLI and MCP share one registry, one service call and one approval gate; nothing writable is GUI-only |
| v2.4.15 | log merge, per-entry ACK policy, device-taken verdicts | one operation = one session directory = one `session.jsonl` (VCOM frames, approval decisions and fastboot commands on one timeline, `logs` reads new sessions and old files); CLI/MCP fail on silence; every write reports what the device actually said |

---

## v2.4.9 — own release pipeline

- **Why:** the inherited repo's releases did not match Linux runtime reality, and waiting on upstream is
  not an option after the licence change. A tool that people cannot install is a tool nobody runs.
- `.deb`, `.AppImage`, `.tar.gz` and a single-file CLI built from one commit, smoke-tested before upload
  (`--help` contains `flash-ota`, `version` runs, the bundled fastboot reports the Huawei-parity build,
  loaders and the USB Update shared object are present).
- Container build pinned to `ubuntu:20.04` so the AppImage stays within **glibc 2.31**. Two CI details
  worth remembering: container jobs default to `sh` (no `pipefail` — set `shell: bash`), and android-tools
  on 20.04 needs `CC/CXX=gcc-10` because the default `g++-9` lacks C++20 `<span>`.

## v2.4.10 — GUI QoL

- Remember the last selected CPU model, firmware files and folders; light/dark theme with dark-safe
  palettes. Purely ergonomic, but the theme change touched the frozen `MainWindow.axaml.cs` surface, so
  the `StableParity` hash was refreshed with a note that protocol/timing did not change.

## v2.4.11 — .NET 10 and packaging reality

- `net10.0` migration for both projects and the CI smoke paths.
- deb `Depends` fix: ICU sonames grow with every distribution and `libssl3` became `libssl3t64` on 24.04+;
  dpkg alternatives have no wildcard support, so the list is newest-first and validated by
  `apt-get -s` on three Ubuntu generations (see [fork-release.md](fork-release.md)).
- Unlock / redline behaviour corrections: a loader that answers "already fastboot unlocked" is reported
  as *no write* instead of a success.

## v2.4.12 — redline policy in one place, no false success

- Redline partition policy moved down into the service layer, so GUI, CLI and MCP share one decision
  instead of three copies.
- GUI fastboot writes require exit 0 **and** a device `OKAY`; USB Update redline gate covered by an
  offline end-to-end harness driven by a synthetic `UPDATE.APP`.
- Test harnesses stopped writing into the real user config / machine-specific paths (that is what made
  them CI-safe).

## v2.4.13 — audit batch (current)

Full notes: [release-notes/v2.4.13.md](release-notes/v2.4.13.md). Summary of intent:

- **Contracts cannot drift.** `CliWriteKind` (`ReadOnly` / `PlanOnly` / `RealWrite`) generates the MCP
  `initialize.instructions`, every tool's `confirm` description and the CLI wording. The earlier
  "gate-only in this build" text described a command that really wrote — that class of bug is now
  structurally impossible.
- **Machine-readable gate** on every write command and MCP call: `plan-only` / `refused` (with
  `reason`) / `confirmed` / `applied`; `refused` is `isError=true` over MCP; the refusal path emits
  exactly one JSON document (it used to emit two and break clients).
- **No AI self-override:** `allow-redline` over MCP returns `-32602`. GUI dialogs list the exact
  redline partitions; SN (NVE) rewrite and FRP erase show old vs new values first.
- **Pre-op backup** (`Services/PreOpBackup.cs`): redline partitions of the plan + `ptable` into a
  timestamped directory with a SHA256 `manifest.json`; `--backup-dir` / `--no-backup` /
  `--require-backup`; read-only `backup` command; per-partition 10 s timeout, stops after two
  consecutive failures, never overwrites an existing directory, and one dump per partition per process.
- **Battery banner** (warning only) from a best-effort `battery-soc` / `battery-capacity` read.
- **`flash-xml` name honesty:** identifiers the loader does not expose are skipped with a warning;
  `--strict-names` refuses (exit 3).
- **Robustness:** truncated sparse headers rejected instead of dereferencing a short buffer;
  `ProcessRunner` kills the whole process tree on timeout; per-instance temp files in `OemInfoService`;
  no `SerialPort` leak on VCOM retry; CLI staging directory cleaned even on exception/cancel.
- **Tests / CI:** four new harnesses (`WriteGates`, `McpProtocol`, `OtaPlan`, `PreOpBackup`),
  `tests/suites.txt` as the single source of truth for both pipelines, `FlashPolicy` fails hard when the
  CLI binary is missing, USTC GitLab pipeline added next to GitHub Actions.
- **Docs:** screenshots captured from the real v2.4.13 controls (headless), README restructured,
  Chinese README added.

## v2.4.15 — log merge, per-entry ACK policy, device-taken verdicts

- **Why:** v2.4.14 made the three surfaces agree, but left three holes the debt register named: two
  logs that had to be stitched by hand, an ACK default that let a silent device exit 0, and write
  paths that could still speak success without device evidence (USB Update, `applied: true`,
  CLI `write-sn`, GUI OEMINFO/downgrade writes bypassing the approval gate).
- **Log merge:** one operation owns one session directory (`session.jsonl`, optional
  `vcom-trace.txt`, `summary.json`), with `sessionId`/`seq`/`parentId`/`capabilityId`/`approvalId`
  correlating frames, approvals and commands. `logs list|show|path` reads it back; every remaining
  write path logs `ok:`/`failed:`/`degraded:`/`skipped:` steps.
- **ACK policy:** CLI and MCP default to `strict` (silent device → exit 3, `verified=false`,
  `failureKind=device-rejected`); the GUI keeps `lenient` with an amber unverified verdict.
- **Verdicts:** `WriteReport` replaces the unconditional `applied: true`; `write-sn` reads the value
  back; `oeminfo-flash` and `downgrade-erase` split the redline writes out of their plan-only
  siblings and pass the shared approval gate; USB Update returns verified/partial/failed with
  explicit events instead of silent `continue`s.
- **Numbers at release time:** 24 hardware-free suites, 413 surface-parity checks, 25 capabilities
  (24 CLI, 24 MCP, 35 GUI handlers, 0 GUI-only), mutation-checked in three directions.
- **Debt:** D-01/02/03/04/13 fixed; see [debt-register.md](debt-register.md) for what stays open.

## v2.4.14 — three-surface parity, approval gate, honest verdicts

Full notes: [release-notes/v2.4.14.md](release-notes/v2.4.14.md). One paragraph of intent:
GUI, CLI and MCP now share one capability registry (22 capabilities, 21 CLI commands, 21 MCP tools)
and one approval gate; the six former GUI-only writes gained CLI/MCP faces; `vcom-unlock` really
uploads loaders instead of printing a plan; every operation writes a JSONL log; and the verdicts that
used to say "success" off a bare exit code (loader upload, SN write, `--require-backup`, unknown CLI
options) now say what was actually verified.

**债务登记见 [debt-register.md](debt-register.md)。** That file is the status side of the two audit
reports: which finding this release closed (with `file:line` / gate evidence) and which is still open,
sorted by "can it make a user or an AI believe a write succeeded".

---

## Known gaps (as of v2.4.14)

Status of every audit item, including the ones below: [debt-register.md](debt-register.md).

- GUI-side gzip SUPER handling is missing (the CLI detects the `0x1f8b` magic in
  `Cli/Commands/OtaFlashRunner.cs`); BD packages can therefore end in a 45/46 half-flash from the GUI.
- The battery level cannot be read reliably on temporary loaders, so the 50 % rule stays a human
  convention rather than a gate.
- `VcomOpenProbe`, `UsbUpdateProbe` and `VisualParity` are excluded from CI (real USB hardware /
  font-dependent pixel diff).
- Only the ELS-AN00 has a full-OTA run; Kirin 990 4G remains unsupported by policy (do not substitute
  5G loaders).

## Repository-level lessons worth keeping

- **`.gitignore` can silently eat new directories.** The Visual Studio `Backup*/` rule swallowed
  `tests/BackupPlan/` and the `[Rr]eleases/` rule swallowed `docs/releases/`; both only surfaced as
  "path does not exist" in CI. Renamed to `tests/PreOpBackup/` and `docs/release-notes/`, and any new
  path containing *Backup*/*Release* should be checked with `git check-ignore -v <path>` first.
- **A test that one pipeline runs is a test that will rot.** Hence `tests/suites.txt`.
