# Docs index

Fork-relevant guides first; inherited-baseline guides follow. Entry point: [README](../README.md).

| Guide | What it covers |
| :--- | :--- |
| [README](../README.md) · [中文 README](../README.zh-CN.md) | Overview, what this line changes vs upstream, design trade-offs, field notes, safety model, install. |
| [refresh-changelog.md](refresh-changelog.md) | Per-release changes of the refresh line (with reasons) and known gaps. |
| [cli-mcp.md](cli-mcp.md) | CLI commands, write kinds, redline gate, gate signals, exit codes, MCP stdio protocol. |
| [op-logging.md](op-logging.md) | Unified operation log: JSONL field table, where the logs live and how to read them, VCOM session-log integration, migration order, `Process.Start` inventory. |
| [fork-release.md](fork-release.md) | Fork release pipeline: deb / AppImage / tar.gz / CLI assets, deb layout, ICU/OpenSSL dependency policy, CI map. |
| [cli-vcom-unlock-parity.md](cli-vcom-unlock-parity.md) | `vcom-unlock` GUI↔CLI↔MCP alignment (design, item-by-item table, ACK policies, stage split, session log fields, tests). |
| [features.md](features.md) | Feature overview across GUI, CLI and MCP. |
| [FORK-NOTICE.md](FORK-NOTICE.md) | Fork provenance, BSL 1.1 license position, compliance checklist. |
| [screenshots.md](screenshots.md) | Screenshot set: file names, content and capture guidance (images under docs/images/). |
| [stable-release.md](stable-release.md) | Inherited upstream 2.4.8 baseline description (old file names there are historical, not current assets). |
| [partition-extractor.md](partition-extractor.md) | UPDATE.APP extraction: selection, naming, cancellation, validation. |
| [portable-appimage.md](portable-appimage.md) | AppImage build baseline, glibc 2.31, host dependencies, udev onboarding. |
| [native-fastboot-parity-v2.4.5.md](native-fastboot-parity-v2.4.5.md) | Modern native fastboot engine: coverage, validation, limits. |
| [usb-update-userspace.md](usb-update-userspace.md) | Userspace libusb USB Update transport and diagnostics. |
| [windows-fastboot-audit-v2.4.3.md](windows-fastboot-audit-v2.4.3.md) | Static audit of the frozen Windows fastboot clients. |
| [mate40-bootloader-analysis.md](mate40-bootloader-analysis.md) | Mate 40 Pro FASTBOOT/BL2 containers and unresolved rescue behavior. |
| [windows-ui-parity.md](windows-ui-parity.md) | Avalonia recreation of the frozen WPF UI and its limits. |
| [usb-update-linux-detection.md](usb-update-linux-detection.md) | Linux-side USB Update device detection. |
| [usb-port-manufacture-mode.md](usb-port-manufacture-mode.md) | USB port / manufacture mode notes. |
| [usb-transfer-diagnostics.md](usb-transfer-diagnostics.md) | USB transfer diagnostics. |
| [beta-potential-features.md](beta-potential-features.md) | Beta-branch-only potential features. |
| [testing-checklist.md](testing-checklist.md) | Hardware validation checklist. |
| [fastboot-windows-linux-diff.md](fastboot-windows-linux-diff.md) | Windows vs Linux fastboot differences. |
| [native-windows-port.md](native-windows-port.md) | Native Windows port notes. |
| [soc-support-fast-flash-loader.md](soc-support-fast-flash-loader.md) | SoC support and fast flash loader matrix. |

Audit records (2026-11-12): [audit/](audit/).
Audit reports (lane D input): [audit-gui-blackbox.md](audit-gui-blackbox.md) (36 GUI black-box items) · [audit-hardcoded-and-hidden-steps.md](audit-hardcoded-and-hidden-steps.md) (H01–H95 / S / F / P).
