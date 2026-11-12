# fix-F9：GitHub 介绍页收尾（v2.4.13 真实状态）

日期：2026-11-12 · lane：F9 · 范围：README.md、docs/{INDEX,features,screenshots}.md、docs/releases/v2.4.13.md

## 事实核对结论（逐项，对照 v2.4.13 代码）

| 事实 | 代码证据 | 结论 |
| :--- | :--- | :--- |
| 命令清单 | `Cli/CliRunner.cs` `Commands`：info, doctor, getvar, extract, dump, **backup**, flash-ota, flash-xml, vcom-unlock, oeminfo, downgrade-enable（11 个） | README MCP 清单缺 `backup` → 已补；`backup` 是只读命令（`IsWrite=false`），dump 默认身份分区集 + ptable，写 manifest.json |
| gate 四态 | `WriteCommands.cs` / `McpServer.cs`：plan-only / refused (reason) / confirmed / applied | README 一致，不改 |
| `--require-backup/--no-backup/--backup-dir` | `WriteCommands.cs:100-102,332-334`；默认目录 `$KIRIN_BACKUP_DIR` → `~/.local/share/kirin-tool/backups` | README 一致，不改 |
| `--strict-names` | "refuse instead of skipping identifiers the loader does not expose"（exit 3，需连设备解析） | README 原先完全没提 → 已补一句 |
| MCP 不接受 allow-redline | `McpServer.cs:261-262` `-32602` "redline override is CLI-only" | 一致，不改 |
| 电量横幅 warning-only | banner 只读刷新，never a gate | 一致，不改 |
| 自动备份 best-effort | dump 失败降级 `unsupported` + 警告，不阻塞；`--require-backup` 才拒绝 | 一致，不改 |
| 安装四格式资产名 | fork-release.md：`kirin-tool-linux_<ver>_amd64.deb` / `-x86_64.AppImage` / `-x86_64.tar.gz` / `kirin-tool-cli-<ver>-linux-x86_64` | README 一致，不改 |
| 版本号 | README 无 2.4.12 残留；fork-release.md:14 `(v2.4.12: 52.3 MB)` 是历史体积注记，保留 | 一致 |
| 测试数 | `tests/suites.txt` = 17 套 | features.md 写 16 → 已改 17 |

## 改动清单（原 → 新）

### README.md

1. **MCP 工具清单**：`info, doctor, getvar, extract, dump, flash_ota, ...` → 在 `dump` 后插入 `backup`（代码 `BackupCommand.McpName="backup"`，cli-mcp.md:88 已列）。
2. **新增 60 秒速览表**（WARNING 块之后）：能做什么 / 不能做什么 / 安全边界 / 谁适合用，标明 CLI+MCP 面向自动化、红线分区不能由 AI 自决、备份 best effort。
3. **CLI 快速上手**：加一行只读备份示例 `kirin-tool-cli backup --json`。
4. **Safety model**：flash-xml 一句补充 `--strict-names` 语义。
5. **新增 "v2.4.13 highlights"** 一节（6 条，链接 docs/releases/v2.4.13.md）。
6. **Screenshots 节**：改指 docs/images/ 下的成品图说明（等 F10 产出，README 暂不嵌图）。

### docs/features.md

7. "16 hardware-free test suites" → 17。
8. Safety 节补一条：只读 `backup` 命令 + 写入前自动备份（best effort）。

### docs/screenshots.md

9. 整节重写：6 张图的文件名/内容/拍摄要点，1600x1000、字体建议、必须真实 v2.4.13 UI；图由 docs/images/ 提供，README 引用相对路径；暂不嵌图。

### docs/INDEX.md

10. screenshots 行描述同步为成品图说明（docs/images/）。

### docs/releases/v2.4.13.md（校对）

11. 补一条漏记的新功能：只读 `backup` CLI/MCP 命令（时间戳目录 + manifest.json）。

## 代码与文档冲突、文档更可信（交编排者）

无。本轮所有代码可验证事实均与文档一致（除上表已修两处文档自身滞后）。

## 仓库元数据建议

- **description**（≤350 字符，实测 ~340）：`Flash, extract and rescue Huawei Kirin devices from Linux — Avalonia GUI, headless CLI and MCP server behind shared write-safety gates: redline partitions CLI-only override, machine-readable gate signals, OKAY-verified writes and best-effort pre-op backups. Unofficial fork; BSL 1.1.`
- **homepage**：`https://github.com/xiaquwan1001/kirin-tool-linux/releases/latest`（或 docs 站点若有）
- **topics**（GitHub 上限 20）：`huawei`, `kirin`, `hisilicon`, `fastboot`, `flash-tool`, `android`, `linux`, `avalonia`, `dotnet`, `mcp`, `mcp-server`, `cli`, `firmware`, `bootloader`, `update-app`, `device-rescue`
