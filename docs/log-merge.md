# 日志合流（log-merge）：一次操作 = 一个会话 = 一个目录 + 一份主日志

> 实现 lane G1。worktree 分支 `feat/log-merge`，基线 `2ade54b`（v2.4.14 main）。
> 前序：[op-logging.md](op-logging.md)（结构化 JSONL 单写入器）、
> [cli-vcom-unlock-parity.md](cli-vcom-unlock-parity.md)（VCOM 人读会话日志）、
> [approval-gate.md](approval-gate.md)（审批决定）。

## 0. 缺口（v2.4.14 release notes 已列）

同一次「VCOM 上传 loader → 换普通线 → `fastboot oem unlock`」在磁盘上是两份互不引用的日志：

| 现状 | 后果 |
|---|---|
| `Services/VcomSessionLogger` → `<root>/vcom-<UTC>-<cpu>.log`（纯文本，逐帧） | 只有人能读；帧级证据无法被 `jq`/脚本消费 |
| `Services/Diagnostics/OperationLogger` → `<root>/{vcom,fastboot,flash}/<UTC>-<pid>.jsonl` | 一个操作一个文件；一次「操作」被拆成 N 个文件，字段各写各的 |
| 两份日志没有共同的 id | 事后对账只能靠时间戳手拼；跨阶段（VCOM → fastboot）根本没有关联键 |

## 1. 目标与非目标

**目标**

1. **单写入器**：只有 `OperationLogger` 写盘。VCOM 文本日志降级为**适配器 + 人读转录**，不再是第二份事实来源。
2. **会话概念**：一次操作 = 一个 `sessionId` = 一个目录 + 一份 `session.jsonl`。同进程内后续所有 scope 共享同一 sessionId。
3. **同构**：GUI / CLI / MCP 三个入口写出的 JSONL 字段完全一致（同一份代码路径）。
4. **时间线**：VCOM 帧、审批决定、fastboot 命令按 `seq` 单调交错在同一个文件里，可用一个 query 还原整段操作。
5. **兼容**：旧路径（`vcom/`、`fastboot/`、`flash/`、`vcom-*.log`）**只读兼容**，读取仍可用；新写入一律走 `sessions/`。

**非目标**：不改设备侧协议；不改任何设备命令、时序、重试；不删既有字段（只能加）；不删 GUI 现有日志路径显示。

## 2. 目录布局

```
${KIRIN_LOG_DIR:-~/.local/state/kirin-tool}/
├── sessions/                                     # 新写入的唯一落点
│   └── 20261113-094735-761095-vcom.session/      # = sessionId，0700
│       ├── session.jsonl                         # 主日志（0600，JSONL，时间线）
│       ├── vcom-trace.txt                        # 可选：VCOM 人读转录（0600，旧格式逐行）
│       └── summary.json                          # 会话结束时的结论（0600，任何路径都写）
├── vcom/ fastboot/ flash/ …                      # 旧：一操作一文件（只读兼容）
└── vcom-<UTC>-<cpu>.log                          # 旧：VCOM 文本日志（只读兼容）
```

| 路径 | 权限 | 说明 |
|---|---|---|
| `sessions/`，`sessions/<sessionId>/` | `0700` | 目录私有 |
| `session.jsonl` / `vcom-trace.txt` / `summary.json` | `0600` | 含设备标识，仅属主可读 |

`sessionId` = 目录名 = `<YYYYmmdd-HHMMSS>-<pid>-<op>`（本地时间戳，排序友好、无冒号，Windows 安全）。
同一毫秒同 pid 撞名时追加 `-2`、`-3`（`FileMode.CreateNew` 保证不串写）。

## 3. 字段统一表

一行 = 一个 JSON 对象。**每行都有的字段**：`ts`、`pid`、`sessionId`、`seq`、`phase`、`op`、`tool`。

| 字段 | 类型 | 何时有值 | 说明 |
|---|---|---|---|
| `ts` | string | 每行 | ISO8601 UTC 带毫秒 |
| `pid` | int | 每行 | 写日志的进程 |
| `sessionId` | string | 每行 | 会话 id（= 目录名），跨进程阶段的对账键 |
| `seq` | int | 每行 | **会话内单调递增**，跨 scope 全局唯一（不是帧序号） |
| `phase` | string | 每行 | `start` / `step` / `prompt` / `result` |
| `op` | string | 每行 | 点分层级操作名：`vcom.session`、`fastboot.unlock`、`flash.ota` … |
| `tool` | string | 每行 | `fastboot` / `adb` / `vcom` / `internal` |
| `scopeId` | string | 每行 | 本 scope 的 id（`phase=start` 起） |
| `parentId` | string? | 子操作 | 父 scope/session id：某条 fastboot 命令属于哪个阶段 |
| `capabilityId` | string? | 有上下文时 | 对应 `CapabilityRegistry` 的 id（`vcom-unlock`、`unlock-bootloader` …） |
| `approvalId` | string? | 有审批时 | 审批指纹（与 `approvals.jsonl` 的 `decisionId` 同值） |
| `step` | string? | `phase=step/prompt` | 结构化事件名：`frame.write`、`frame.ack`、`port.open`、`progress`、`approval.decision` … |
| `argv` | array | `phase=start` | 脱敏参数数组（规则同 op-logging §2） |
| `notes` | string? | 任意行 | 自由文本（人读的一句话） |
| `answer` | string? | `phase=prompt` | 已接受的人类回答 |
| `exitCode` | int? | `phase=result` | 子进程退出码；未执行时为 `null` |
| `durationMs` | int? | `phase=result` | 墙钟毫秒 |
| `stdoutTail`/`stderrTail` | string? | `phase=result` | UTF-8 安全截断 ≤2048 字节 |
| `ok` | bool? | `phase=result`（及 `step` 的错误行） | 结论 |
| `verdictSource` | string? | 有 `ok` 时 | 判据：`exit-code` / `arg-validation` / `timeout` / `vcom:ack` / `unknown:no-result` … |
| `frame` | object? | VCOM 帧 | `{type, addr, frameSeq, bytes, cumulativeBytes, expectAck}` |
| `ack` | object? | VCOM ACK | `{type, result, raw, waitedMs, fatal}` |
| `port` / `stage` / `approval` | object? | 对应事件 | 端口、阶段、审批决定的细节 |

> 行级 `seq` 是**时间线序号**；VCOM 数据帧自己的序号在 `frame.frameSeq` 里，两者不冲突。

**VCOM 文本行 → `phase=step` 映射**（适配器 `VcomSessionLogger` 负责，一行转录 = 一行结构化事件）：

| VCOM event | phase | `step` | 结构化字段 |
|---|---|---|---|
| `session.open` | `start` | `session.open` | `tool`/`version`（并写会话 `start` 行） |
| `command.start` | `step` | `command.start` | `command`/`cpu`/`json`/`yes` |
| `plan` | `step` | `plan` | `loaderDirectory`/`stages`/`ackMode`/`stageCount`… |
| `unlock.start` | `step` | `unlock.start` | `cpu`/`stages`/`loaderDirectory` |
| `port.open` / `port.reconnect` / `port.close` | `step` | 同名 | `port` 对象 |
| `stage.start` / `stage.end` | `step` | 同名 | `stage` 对象 |
| `frame.write` | `step` | `frame.write` | `frame` 对象（`type`/`addr`/`frameSeq`/`bytes`/`cumulativeBytes`/`expectAck`） |
| `frame.ack` | `step` | `frame.ack` | `ack` 对象（`type`/`result`/`raw`/`waitedMs`/`fatal`） |
| `progress` | `step` | `progress` | `notes` = 进度文本 |
| `prompt` / `prompt.answer` | `prompt` | 同名 | `notes` = 提示原文 / `answer` |
| `error` | `step` | `error` | `ok=false`、`verdictSource=exception:<类型>`、`notes=where: message` |
| `unlock.done` / `command.end` / `session.close` | `step` | 同名 | 结论摘要（会话级 `result` 由 `EndSession` 单独写） |
| 审批决定 | `step` | `approval.decision` | `approval` 对象 + `capabilityId` + `approvalId` |

`summary.json`（会话结束写一次，**失败路径也写**）：

```json
{ "sessionId": "20261113-094735-761095-vcom.session", "op": "vcom.session", "tool": "vcom",
  "startedUtc": "…", "endedUtc": "…", "durationMs": 41234, "ok": false,
  "verdictSource": "vcom:no-ack", "exitCode": 1,
  "paths": { "directory": "…", "mainLog": "…/session.jsonl", "trace": "…/vcom-trace.txt", "summary": "…/summary.json" },
  "counts": { "lines": 812, "scopes": 2, "steps": 806, "prompts": 2, "frames": 804, "acks": 800, "ackBad": 0, "children": 2, "childrenFailed": 1 },
  "note": "…" }
```

## 4. 会话与作用域模型

```csharp
using var session = OperationLogger.BeginSession("vcom.session", "vcom", argv);   // 建目录 + 写 start
using (var scope = OperationLogger.Begin("fastboot.unlock", "fastboot", argv))     // 同 sessionId、同文件
    scope.Result(ok, exitCode, …, verdictSource: "exit-code");
session.End(ok: true, verdictSource: "vcom:ack");                                  // 写 result + summary.json
```

- `OperationLogger.CurrentSession` 是进程内的环境会话；`Begin(...)` 在有环境会话时**加入**它（`parentId` = 父 scope 或 sessionId），没有时**隐式建一个**（单 scope 会话）。
- 会话生命周期：显式 `EndSession()` / `session.Dispose()`，或**空闲轮换**（`KIRIN_LOG_SESSION_IDLE_SECONDS`，默认 1800，0 = 关闭）。GUI 常驻进程因此不会把一整天写进同一个目录：一段「VCOM 上传 → 换线 → oem unlock」在同一会话内，超过空闲窗口则自动新开。
- `OperationLogger.CapabilityScope(capabilityId, approvalId)` 提供 `capabilityId`/`approvalId` 环境上下文，三面入口在进入时包一层即可，帧/命令行自动带上关联键。
- `KIRIN_LOG_DISABLE=1` 仍然整层关闭（不建目录、不改返回值）。

## 5. `logs` 只读命令（CLI + MCP）

| 命令 | 作用 |
|---|---|
| `logs list [--json]` | 列出 `sessions/` 下的会话 + 旧路径日志（`legacy: true`），按时间倒序 |
| `logs show <session\|latest> [--json]` | 一份会话：`session`/`summary`/`events`（旧文本日志解析成同名事件） |
| `logs path <session>` | `{sessionId, dir, mainLog, trace, summary}`，供脚本 `cd "$(… | jq -r .dir)"` |

- 选择器：`latest`、完整 sessionId、sessionId 前缀（唯一才接受）、绝对/相对路径、旧日志文件名。
- 一律输出 JSON（stdout），诊断走 stderr；MCP 同名工具 `logs`（`readOnlyHint`，**不需要 confirm**）。
- 注册表：`CapabilityRegistry` 新增 `logs`（`ReadOnly`，`GuiHandler = null`），`tests/SurfaceParity` 的 A/A2/A3/A4/B/B2/C/C2 门禁自动覆盖。

## 6. GUI

进度对话框结束时显示 **session 目录**（`sessions/<sessionId>/`），并保留原有 `log:` 行（现在指向 `vcom-trace.txt`）。
两者都留在屏幕上，可选中复制（`UI/ProgressDialog.axaml.cs` 只新增 `ShowSessionDirectory`，`ShowLogPath` 行为不变）。

## 7. 迁移与兼容

| 面 | 处理 |
|---|---|
| 新写入 | 一律 `sessions/<id>/session.jsonl`；`vcom/`、`fastboot/`、`flash/` 不再新增文件 |
| 旧 JSONL | 原样保留，`logs list` 标 `kind: "legacy-jsonl"`，`logs show` 直接读（`sessionId` 合成 `legacy:<category>/<file>`） |
| 旧 VCOM 文本 | 原样保留，`logs show` 解析成同名事件（`kind: "legacy-vcom-text"`） |
| `VcomSessionLogger` | 变成适配器：`FilePath` 仍返回可读路径（现在是 `vcom-trace.txt`），新增 `SessionDirectory`/`SessionId`/`MainLogPath`；`Event/Progress/Error` 签名不变（`VcomFlasher`/`FirmwareUnlocker` 零改动） |
| `OperationScope.FilePath` | 语义变为「本会话的主日志」（同一会话内多个 scope 相同） |
| 字段 | 只加不减：`ts/pid/op/phase/tool/argv/exitCode/durationMs/stdoutTail/stderrTail/ok/verdictSource/notes` 全部保留 |

## 8. 保留策略

- 工具**不自动删除**任何日志（日志是举证材料）；`logs list` 会给出体积与计数，由用户自行清理。
- 建议（写进 README）：`sessions/` 按需保留最近 N 次；旧 `vcom/`、`fastboot/`、`flash/` 可在确认无工单引用后整体归档。
- 权限固定 `0700`/`0600`，与 op-logging 一致；分享前需抹 SN/PSID。

## 9. 测试计划

| 套件 | 覆盖 |
|---|---|
| `tests/OpLogging`（扩展） | 会话布局（`sessions/<id>/session.jsonl`）、`seq` 单调、`sessionId`/`scopeId`/`parentId` 字段、`capabilityId`/`approvalId` 上下文、`summary.json`、权限、kill 开关 |
| `tests/LogMerge`（新增） | ①VCOM 会话 + 后续 fastboot 命令在同一 `session.jsonl` 且 `seq` 单调 ②旧路径日志仍可被 `logs show` 读到 ③`logs list --json` 字段完整 ④`summary.json` 失败路径也写 ⑤权限 600/700 |
| `tests/SurfaceParity` | `logs` 的 CLI/MCP/GUI 三面对齐（A/A2/A3/A4/B/B2/C/C2），GUI handler 为 `null` 不引入 GUI-only 债务 |
| `tests/VcomUnlockCli` | 转录文件位置改为 `sessions/<id>/vcom-trace.txt`，逐帧内容不变 |
| 全量 | `dotnet build` + `tests/suites.txt` 逐套 `dotnet run` 全绿 |
