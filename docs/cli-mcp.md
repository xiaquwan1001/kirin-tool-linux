# CLI 与 MCP 入口（本地 fork 实验）

> 上游自 2026-09-11 起为 Read-Only Reference License。本目录下的 CLI/MCP 仅作本地 fork 改造，
> 不发布、不分发；改用前请阅读 `FORK-NOTICE.md` 与 `LICENSE`。

## 生成物

- `KirinTool.Cli/`：薄 exe 工程，引用主工程，输出 `KirinTool.Cli.dll`
- `Cli/`：CLI 命令、闸门与 MCP server 实现，编译进主 exe，因此 `Kirin-Tool --mcp` 也可用
- 两个入口共用 `CliRunner`，命令实现只有一份

## 命令

只读（第一批，已落地）：

| 命令 | 说明 | 复用 |
|---|---|---|
| `info` | 工具/fastboot/设备信息、能力与 rescue 计划 | `FastbootClient`、`FastbootReadResponses`、`ChipCapabilities`/`RescuePartitionPlan` |
| `doctor` | fastboot/adb 路径与版本、设备枚举、loader 清单、只读能力探测 | `BinaryLocator`、`ProcessRunner`、`FastbootClient`、`FastbootReadResponses`、`FirmwareUnlocker.CpuAddresses` |
| `getvar <name>` | 单个 fastboot 变量 | `FastbootClient.QueryVariableAsync` + `FastbootReadResponses.Variable` |
| `extract <UPDATE.APP> [outdir]` | 列出并导出全部分区 | `Services.PartitionExtractor.Read/ExtractAsync`（`Utils.UpdateApp`） |
| `dump <partition> -o <dir>` | 先 `oem dump-emmc`，失败回退 `oem dump-storage` 导出分区 | `Services.FastbootFlasherService.DumpPartitionAsync`（临时 VCOM loader 两者都回 `invalid command`，属预期） |

写操作分两类（`CliWriteKind`，`Cli/Commands/ICliCommand.cs`）：

| 命令 | 类型 | `--yes` / `confirm:true` 后的行为 |
|---|---|---|
| `flash-ota`、`flash-xml` | **RealWrite** | **真实刷写设备**（MCP 路径没有 dry-run）；红线分区仍需 `--allow-redline`（仅 CLI） |
| `vcom-unlock`、`oeminfo`、`downgrade-enable` | **PlanOnly** | 只确认计划，**永远不写设备**，一律 exit 4 |

无 `--yes` 时打印计划清单，exit 4。`--strict-names` 时若 loader 未暴露计划中的分区/标识符则拒绝执行（exit 3），默认只 warning。

### gate 信号（机器可读）

写命令的 `--json` 输出统一带 `gate` 字符串字段，MCP 侧同步写进 `_meta.gate`：

| 取值 | 含义 |
|---|---|
| `plan-only` | 只生成了计划，未确认 |
| `refused` | 被拒绝，`isError=true`；`reason` 字段说明原因（红线分区未给 `--allow-redline` → exit 4；flash-xml 缺图 → exit 5，缺图先于红线判定） |
| `confirmed` | PlanOnly 命令已确认，但未写任何内容 |
| `applied` | 真实写入已执行（全部成功 exit 0；全失败 exit 5；部分失败 exit 3） |

### 退出码

0 成功 / 1 用法 / 2 无设备 / 3 设备拒绝（含 `--strict-names` 拒绝、部分刷写失败）/ 4 被闸门拒（未确认或 PlanOnly 确认）/ 5 文件或校验失败（含刷写 0/0 全失败）。

flash-ota 默认 staging 目录 `<首个文件目录>/.kirintool-cli-tmp` 在结束时（含异常/取消）自动删除；显式 `--work-dir` 则保留。

## 约定

- `--json` 输出结构化 JSON 到 stdout；进度/诊断一律 stderr，stdout 可安全管道
- fastboot 解析顺序：`KIRIN_FASTBOOT` 环境变量 → 程序目录及 `fastboot/` → PATH → `/usr/bin`、`/usr/local/bin`；adb 同理（`KIRIN_ADB`）
- 设备拒绝判定：输出包含 `FAILED`/`invalid command`/`not allowed`/`locked` 等 → exit 3

## MCP

`KirinTool.Cli mcp` 或 `Kirin-Tool --mcp`：stdio、**换行分隔的 JSON-RPC 2.0**（每行一条报文），
日志只写 stderr。支持 `initialize`、`notifications/initialized`、`ping`、`tools/list`、`tools/call`。

工具名：`info`、`doctor`、`getvar`、`extract`、`dump`、`flash_ota`、`flash_xml`、`vcom_unlock`、`oeminfo`、`downgrade_enable`。

- 写工具的 `inputSchema.required` 必含 `confirm: boolean`；`initialize` 的 instructions 与每个工具的 confirm
  描述按命令的 `WriteKind` 动态生成（RealWrite："true performs the real write; there is no dry-run in the MCP
  path"；PlanOnly："true only acknowledges the plan; nothing is written"）。
- **MCP 不接受 `allow-redline`**：schema 不暴露该参数，调用时传入返回 JSON-RPC 错误 `-32602`
  （"redline override is CLI-only"）；红线覆盖只能用 CLI 的 `--allow-redline`。
- 参数校验错误返回 `-32602`：未知参数名、`confirm` 等 boolean 参数传了非布尔值（如字符串 `"true"`）。
- `tools/call` 的 `arguments` 为空对象/缺省可用。
- 写工具 `confirm` 缺省（false）时只返回计划清单（`gate=plan-only`），`isError=false`，`_meta.exitCode` 为 4；
  `gate=refused` 时 `isError=true`（`structuredContent.reason` 给出具体拒绝原因）；`_meta` 同时带 `gate`、
  `exitCode`、`stderr`。`gate=plan-only` 与 `refused` 都可能是 exit 4，客户端应读 `gate`/`reason` 而不是只看退出码。

自测：

```bash
printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | ./KirinTool.Cli mcp
```
