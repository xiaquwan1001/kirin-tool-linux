# CLI 与 MCP 入口（本地 fork 实验）
> **Where to get the CLI / MCP server:** part of every fork release — install the `.deb` (`kirin-tool-cli`) or grab the single-file `kirin-tool-cli-<ver>-linux-x86_64` asset from the [releases page](https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest). Install overview: [README §Install](../README.md#install); package layout and CI: [fork-release.md](fork-release.md). Entry points: `KirinTool.Cli` (headless) or `Kirin-Tool --mcp` (GUI binary speaking MCP on stdio).

Quick example — plan first, write only after explicit confirmation:

```bash
kirin-tool-cli flash-ota base.app --json   # prints the plan, gate=plan-only, exit 4 — nothing written
kirin-tool-cli flash-ota base.app --yes    # real write of all non-redline partitions
```


> 上游自 2026-09-11 起为新版本 Read-Only Reference License；本 fork 继承的 v2.4.2 基线仍按 BSL 1.1 发布
> （含 CLI/MCP 二进制）。许可与合规口径见 `FORK-NOTICE.md` 与 `LICENSE`。

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
| `backup [partition...]` | 只读备份：把指定分区（默认身份分区集 + `ptable`）dump 进时间戳目录并写 `manifest.json` | `Services.PreOpBackup` + `FastbootFlasherService.DumpPartitionAsync` |
| `partitions` | 设备分区表（GUI 分区选择器读的第一步） | `FastbootFlasherService.GetPartitionTableAsync`（GUI 同一个方法） |
| `psid` | 设备标识 `oem get-psid`（GUI「Read identifiers」按钮） | `FastbootClient.ReadPsidAsync` |
| `capabilities` | 逐芯片能力矩阵 + 内置 loader 清单 + rescue 分区计划（不需设备） | `ChipCapabilities.Allows`、`RescuePartitionPlan`、`FirmwareUnlocker.CpuAddresses` |
| `udev` | 宿主机 udev 规则覆盖检查（GUI「Check again」按钮）；覆盖则 exit 0，否则 exit 5 并打印修复命令 | `Services.UdevRuleNotice.Inspect` |
| `logs list` / `logs show <session|latest>` / `logs path <session>` | 只读查看合流后的操作日志（会话目录 / `session.jsonl` / `summary.json`）；旧 `vcom-*.log` 与 `<category>/*.jsonl` 也能读，标 `legacy: true` | `Services/Diagnostics/LogSessionStore`（写入器与 GUI/CLI 共用 `OperationLogger`） |

写操作分两类（`CliWriteKind`，`Cli/Commands/ICliCommand.cs`）：

| 命令 | 类型 | `--yes` / `confirm:true` 后的行为 |
|---|---|---|
| `flash-ota`、`flash-xml` | **RealWrite** | **真实刷写分区**（MCP 路径没有 dry-run）；红线分区仍需 `--allow-redline`（仅 CLI） |
| `vcom-unlock` | **RealWrite** | **真实上传 loader 到内存**（不碰任何分区），与 GUI「Unlock Fastboot」同一实现；详见下节 |
| `oeminfo`、`downgrade-enable` | **PlanOnly** | 只确认计划，**永远不写设备**，一律 exit 4 |

无 `--yes` 时打印计划清单，exit 4。`--strict-names` 时若 loader 未暴露计划中的分区/标识符则拒绝执行（exit 3），默认只 warning。

### gate 信号（机器可读）

写命令的 `--json` 输出统一带 `gate` 字符串字段，MCP 侧同步写进 `_meta.gate`：

| 取值 | 含义 |
|---|---|
| `plan-only` | 只生成了计划，未确认 |
| `refused` | 被拒绝，`isError=true`；`reason` 字段说明原因（红线分区未给 `--allow-redline` → exit 4；flash-xml 缺图 → exit 5，缺图先于红线判定） |
| `confirmed` | PlanOnly 命令已确认，但未写任何内容 |
| `applied` | 真实写入已执行（全部成功 exit 0；全失败 exit 5；部分失败 exit 3） |
| `failed` | 真实执行但失败（`vcom-unlock`：`failureKind` 说明是设备侧还是本地文件；`isError=true`） |

### 退出码

0 成功 / 1 用法 / 2 无设备 / 3 设备拒绝（含 `--strict-names` 拒绝、部分刷写失败）/ 4 被闸门拒（未确认或 PlanOnly 确认）/ 5 文件或校验失败（含刷写 0/0 全失败）。

### 写入前自动备份（pre-op backup）

flash-ota / flash-xml（以及 GUI 的全部真实写入路径）在**第一次真实写入之前**自动备份
「写入计划中的红线分区 + `ptable`」，目录为时间戳目录：

- `--backup-dir <dir>` 优先；否则 `$KIRIN_BACKUP_DIR`；否则 `~/.local/share/kirin-tool/backups/<yyyyMMdd-HHmmss>/`。
- 同秒冲突时追加序号（`-2`、`-3`…），**绝不覆盖已有备份目录**。
- 每分区先 `oem dump-emmc`，失败回退 `oem dump-storage`；成功计算 SHA256。
- 目录里写 `manifest.json`（先 `.tmp` 再原子 rename）：工具版本、UTC 时间、fastboot 路径、
  设备型号/SN（读不到为 null）、触发命令、写入计划、每分区 `{name,file,size,sha256,status,reason}`，
  status ∈ `ok` / `unsupported`（loader 不支持 dump）/ `failed`。
- **降级语义**：备份不完整**不阻塞写入**；CLI 在 stderr 打
  `warning: pre-op backup incomplete (n/m)`，`--json` 的 applied 文档带
  `backup: {dir, complete, skipped, manifest, partitions:[...]}`。GUI 在进度对话框里加一行说明。
- `--no-backup` 跳过备份；`--require-backup`（严格模式，默认关）在备份不完整时拒绝写入（exit 4）。
- 同一进程内同一分区只 dump 一次。

flash-ota 默认 staging 目录 `<首个文件目录>/.kirintool-cli-tmp` 在结束时（含异常/取消）自动删除；显式 `--work-dir` 则保留。

## 三面一致性（parity）

GUI、CLI、MCP 的对应关系登记在 `Services/Capabilities/CapabilityRegistry.cs`，由
`tests/SurfaceParity` 在 CI 里强制对齐（命令缺失、写工具没有 `confirm` 门控、GUI 长出未登记的
`Handle*`/`*_Click` 都会让它变红）。设计与迁移计划见 [parity-design.md](parity-design.md)。
## `vcom-unlock`：VCOM/BootROM 装载（GUI 对齐）

与 GUI 的「Unlock Fastboot」共用 `Services/FirmwareUnlocker.UnlockFastboot` +
`Services/VcomFlasher`（同一帧格式、同一 `CpuAddresses` 地址表、同一装载顺序）。逐项对齐表见
[cli-vcom-unlock-parity.md](cli-vcom-unlock-parity.md)。

```bash
kirin-tool-cli vcom-unlock --cpu hisi990 --json          # 计划，gate=plan-only，exit 4
kirin-tool-cli vcom-unlock --cpu hisi990 --yes           # 真上传（设备需已在 12d1:3609 VCOM 模式）
kirin-tool-cli vcom-unlock --cpu hisi990 --yes --dry-run # 有 --yes 也只出计划
kirin-tool-cli vcom-unlock --cpu hisi990 --yes --stage-only null   # 第一段：START + null
# 手动拔插（电脑侧）后：
kirin-tool-cli vcom-unlock --cpu hisi990 --yes --stage-only rest   # 第二段：不重发 START
```

| 参数 | 默认 | 说明 |
|---|---|---|
| `--cpu <chip>` | 必填 | 枚举来自 `FirmwareUnlocker.CpuAddresses` |
| `--yes` / MCP `confirm:true` | false | 门控；缺省只打印计划 |
| `--dry-run` | false | 即使 `--yes` 也只打印计划 |
| `--ack-mode strict|lenient|off` | `lenient` | `lenient` = GUI 现状（读 1 字节、不校验值、超时也继续）；`strict` = 读不到或非 `0xAA` 即失败；`off` = 不等待 ACK |
| `--ack-timeout <秒>` | 10 | 每个 ACK 的等待上限（GUI 同为 10 s） |
| `--replug skip|prompt|wait:<秒>` | `skip` | 非交互默认 `skip`，stderr 明示"设备需要人工拔插"；`wait:<秒>` 会在提示后等待指定秒数再由服务轮询重连（不重发 START）；`prompt` 读 stdin（重定向时按 wait 15 s） |
| `--loader-dir <dir>` | `<程序目录>/loaders` | 指向"包含每芯片子目录"的根目录，可任意指定 |
| `--stage-only all|null|xloader|rest` | `all` | 分步执行配合人工拔插；`rest` 排除 null 且**不重发 START** |
| `--port <tty>` | 自动枚举 12d1:3609 | 显式端口跳过枚举，并按 bulk-only 处理（不发 modem 线信号） |
| `--use-fastflash-loader` | false | 对应 GUI 的 `fastbootf.ktl` 开关 |
| `--no-frame-log` | false | 关掉 stderr 的逐帧 trace（JSON 汇总仍完整） |

输出：进度/ACK 逐帧到 **stderr**；结束（含失败）时**最后一行 stdout 是一份 JSON**：
`stages[]`（每阶段 `{stage,file,address,pwn,bytes,frames,acks,status,durationMs,note}`）、
`ack{expected,ok,bad,timeouts}`、`framesSent`、`elapsedMs`、`stageSelection`、`replug`、`ackMode`、
`conclusion`、`failureKind`、`gate`、`exitCode`。

退出码：0 完成 / 1 用法 / **2 无 VCOM 设备或端口打不开或拔插后未重连** / **3 设备侧拒绝
（strict ACK 超时或非 0xAA）** / 4 未确认、`--dry-run`、`--replug skip` 下必须拔插而被拒 /
**5 loader 目录缺失、选中阶段无 loader、.ktl 解密失败**。

### 会话日志（GUI + CLI 同一份）

每次 VCOM 会话写 `$KIRIN_LOG_DIR`（默认 `~/.local/state/kirin-tool/`，目录 700）下
`vcom-<UTC yyyyMMddTHHmmssZ>-<cpu>.log`：pid、端口、framing、逐帧 type/addr/len/seq/bytes、
ACK 原始字节或超时、每阶段起止与耗时、每条拔插提示与用户选择、最终结论、异常 stack。
GUI 在进度对话框显示该路径并保留到结束；CLI 把路径打在 stderr 第一行，**`--dry-run` 也会写出
`event=plan` 头**。字段表见 [cli-vcom-unlock-parity.md §7](cli-vcom-unlock-parity.md)。
## 约定

- `--json` 输出结构化 JSON 到 stdout；进度/诊断一律 stderr，stdout 可安全管道
- fastboot 解析顺序：`KIRIN_FASTBOOT` 环境变量 → 程序目录及 `fastboot/` → PATH → `/usr/bin`、`/usr/local/bin`；adb 同理（`KIRIN_ADB`）
- 设备拒绝判定：输出包含 `FAILED`/`invalid command`/`not allowed`/`locked` 等 → exit 3

## MCP

`KirinTool.Cli mcp` 或 `Kirin-Tool --mcp`：stdio、**换行分隔的 JSON-RPC 2.0**（每行一条报文），
日志只写 stderr。支持 `initialize`、`notifications/initialized`、`ping`、`tools/list`、`tools/call`。

工具名：`info`、`doctor`、`getvar`、`extract`、`dump`、`backup`、`flash_ota`、`flash_xml`、`vcom_unlock`、`oeminfo`、`downgrade_enable`。
`vcom_unlock` 的 `confirm:true` 会**真的上传 loader**（`RealWrite`）；`--replug prompt` 在 MCP 里
按"stdin 非交互"降级为等待 15 s，因此自动化应显式传 `replug: "wait:<秒>"`。

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
