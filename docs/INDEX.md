# Docs index

Fork-relevant guides first; inherited-baseline guides follow. Entry point: [README](../README.md).
Every link below is checked with `ls` (no dead entries; `docs/audit/` is a directory of audit records).

## Start here

| Guide | What it covers |
| :--- | :--- |
| [README](../README.md) · [中文 README](../README.zh-CN.md) | Overview, what this line changes vs upstream, design trade-offs, field notes, safety model, install. |
| [refresh-changelog.md](refresh-changelog.md) | Per-release changes of the refresh line (with reasons) and known gaps. |
| **[debt-register.md](debt-register.md)** | **Which audit finding is fixed in 2.4.14, which is still debt, and why** — per-item evidence (file:line / gate name), open items sorted by "can it make a user or an AI believe a write succeeded". |

## Design & contracts (the three-surface model)

| Guide | What it covers |
| :--- | :--- |
| [parity-design.md](parity-design.md) | Why GUI/CLI/MCP share one capability registry, the parity contract, the drift gates, and what "GUI-only" is allowed to mean. |
| [approval-gate.md](approval-gate.md) | The single approval gate in front of every write: GUI modal, CLI `--yes` + typed phrase, MCP `confirm:true` + out-of-band human approval, write tickets, `approvals.jsonl` audit. |
| [verdict-honesty.md](verdict-honesty.md) | When this tool is allowed to say "success" (applied vs verified), VCOM ACK evidence, loader pre-flight, backup gate, NVE read-back, and the test map. |
| [cli-mcp.md](cli-mcp.md) | CLI commands, write kinds, redline gate, gate signals, exit codes, MCP stdio protocol. |

| [log-merge.md](log-merge.md) | Log merge: one operation = one session directory = one `session.jsonl` (+ `vcom-trace.txt`, `summary.json`), the unified field table, the VCOM-frame→phase map, `logs` command, migration/compat and retention. |
| [op-logging.md](op-logging.md) | Unified operation log: JSONL field table, where the logs live and how to read them, VCOM session-log integration, migration order, `Process.Start` inventory. |
| [fork-release.md](fork-release.md) | Fork release pipeline: deb / AppImage / tar.gz / CLI assets, deb layout, ICU/OpenSSL dependency policy, CI map. |
| [warnings-policy.md](warnings-policy.md) | Compiler/CI warning budget: where the CS86xx flood came from, batch A (done) vs B (open), the `WarningsAsErrors` gate, pinned action majors and runners. |
| [verdict-honesty.md](verdict-honesty.md) | Verdict honesty: when this tool is allowed to say "success" (applied vs verified), VCOM ACK evidence, loader pre-flight, backup gate, NVE read-back, and the test map. |
| [downgrade-gate.md](downgrade-gate.md) | Downgrade erase gate (D-13): `downgrade-enable` (plan only) vs `downgrade-erase` (redline erase behind the approval gate), the GUI/CLI/MCP wiring, the op log and the test map. |
| [write-verdict-parity.md](write-verdict-parity.md) | Write/verdict parity fix (D-01/D-02/D-03): the oeminfo write capability split, `applied` taken from real device answers, CLI `write-sn` read-back verdict — and what was still false in v2.4.14. |
| [usb-update-verdict.md](usb-update-verdict.md) | USB Update verdict (D-04): the transfer's three states (`verified`/`partial`/`failed`), every degradation as an explicit `degraded:`/`skipped:`/`missing:` event, the required-partition rule, the GUI/CLI/`WriteReport` mapping and the hardware-free test map. |
| [cli-vcom-unlock-parity.md](cli-vcom-unlock-parity.md) | `vcom-unlock` GUI↔CLI↔MCP alignment (design, item-by-item table, ACK policies, stage split, session log fields, tests). |
| [ack-default.md](ack-default.md) | ACK default per entry point: CLI/MCP `strict`, GUI `lenient` — decision, exit/JSON table, migration and rollback. |
| [op-logging.md](op-logging.md) | Unified operation log: JSONL field table, where the logs live and how to read them, VCOM session-log integration, migration order, `Process.Start` inventory. |
| [features.md](features.md) | Feature overview across GUI, CLI and MCP. |

## Audits (read these as snapshots, not as status)

| Guide | What it covers |
| :--- | :--- |
| [audit-gui-blackbox.md](audit-gui-blackbox.md) | GUI black-box audit: 36 user-visible actions judged by four black-box criteria (B1 no-cli/no-mcp, B2 hidden steps, B3 UI-only results, B4 bypass-service), plus the side-effect inventory and TOP 10. |
| [audit-hardcoded-and-hidden-steps.md](audit-hardcoded-and-hidden-steps.md) | Hardcoded values (H01–H95), silent degradations (S01–S43), false successes (F01–F21), platform assumptions (P01–P27), and the TOP 15 by "can it mislead a user or an AI". |
| [audit/](audit/) | Audit working records of 2026-11-12: lane-A…lane-D (per-lane findings) and `fix-F*.md` (how each fix candidate was approached). |
| [windows-fastboot-audit-v2.4.3.md](windows-fastboot-audit-v2.4.3.md) | Static audit of the frozen Windows fastboot clients. |
| [debt-register.md](debt-register.md) | **Status side of the audits above** — start here when asking "is this still broken?". |

## Releases

| Guide | What it covers |
| :--- | :--- |
| [release-notes/v2.4.14.md](release-notes/v2.4.14.md) | Current release: three-surface parity + approval gate + real VCOM execution + operation log + honest verdicts, with its known gaps. |
| [release-notes/v2.4.13.md](release-notes/v2.4.13.md) | Previous release: audit + fix batch (contracts, machine-readable gate, pre-op backup, honest gates). |
| [fork-release.md](fork-release.md) | Fork release pipeline: deb / AppImage / tar.gz / CLI assets, deb layout, ICU/OpenSSL dependency policy, CI map. |
| [portable-appimage.md](portable-appimage.md) | AppImage build baseline, glibc 2.31, host dependencies, udev onboarding. |
| [stable-release.md](stable-release.md) | Inherited upstream 2.4.8 baseline description (old file names there are historical, not current assets). |
| [FORK-NOTICE.md](FORK-NOTICE.md) | Fork provenance, BSL 1.1 license position, compliance checklist. |
| [screenshots.md](screenshots.md) | Screenshot set: file names, content and capture guidance (images under docs/images/). |

## Inherited-baseline guides

| Guide | What it covers |
| :--- | :--- |
| [partition-extractor.md](partition-extractor.md) | UPDATE.APP extraction: selection, naming, cancellation, validation. |
| [native-fastboot-parity-v2.4.5.md](native-fastboot-parity-v2.4.5.md) | Modern native fastboot engine: coverage, validation, limits. |
| [usb-update-userspace.md](usb-update-userspace.md) | Userspace libusb USB Update transport and diagnostics. |
| [usb-update-linux-detection.md](usb-update-linux-detection.md) | Linux-side USB Update device detection. |
| [usb-port-manufacture-mode.md](usb-port-manufacture-mode.md) | USB port / manufacture mode notes. |
| [usb-transfer-diagnostics.md](usb-transfer-diagnostics.md) | USB transfer diagnostics. |
| [mate40-bootloader-analysis.md](mate40-bootloader-analysis.md) | Mate 40 Pro FASTBOOT/BL2 containers and unresolved rescue behavior. |
| [windows-ui-parity.md](windows-ui-parity.md) | Avalonia recreation of the frozen WPF UI and its limits. |
| [fastboot-windows-linux-diff.md](fastboot-windows-linux-diff.md) | Windows vs Linux fastboot differences. |
| [native-windows-port.md](native-windows-port.md) | Native Windows port notes. |
| [soc-support-fast-flash-loader.md](soc-support-fast-flash-loader.md) | SoC support and fast flash loader matrix. |
| [beta-potential-features.md](beta-potential-features.md) | Beta-branch-only potential features. |
| [testing-checklist.md](testing-checklist.md) | Hardware validation checklist. |
