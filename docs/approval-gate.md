# 审批闸门（Approval Gate）：所有写能力的唯一入口

> 状态：lane E 实现中（分支 `feat/approval-gate`，基线 `bd641bc`）。
> 目标一句话：**GUI 不再独占任何写能力；GUI / CLI / MCP 三个入口都必须经过同一个审批闸门，
> 批准后执行同一份 service 层代码。**

## 0. 为什么要有这一层

基线（`bd641bc`）的三面门禁（`tests/SurfaceParity`）保证了「三面声明一致」，但**放行机制**不一致：

| 面 | 现状（基线） | 问题 |
|---|---|---|
| GUI | 每个 handler 自己 `ShowConfirmDialog` / `ConfirmRedlineWriteAsync` | 确认文案与门控强度逐处不同；不可复用 |
| CLI | `--yes`（红线再加 `--allow-redline`） | 无人值守时可被脚本静默放行红线写 |
| MCP | `confirm:true` | **AI 自助放行**：`confirm:true` 就是模型自己按下确认键 |

结论：能力矩阵的「有/没有」已经对齐，「谁有权放行」还没对齐。审批闸门补的是后者。

## 1. 目标与非目标

### 目标
1. 每个 `CapabilityWriteKind.Write` 能力都有 GUI + CLI + MCP 三个入口（不得再有 GUI-only 写能力）。
2. 三个入口**执行前**都调用 `IApprovalGate.RequestAsync`，同一份 `ApprovalRequest` 结构、同一套审计字段。
3. 批准后调用**同一个 service 方法**（不允许把业务逻辑复制到 CLI/MCP 里）。
4. 红线/不可逆项必须**人工手输确认串**，不能只靠布尔标志。
5. MCP 的 `confirm:true` **永远不足以放行**：必须再有一次带外人工批准（GUI 弹窗）或结构化拒绝。

### 非目标（本次不做）
- 不做远程/多用户授权、不做 RBAC、不做持久化「永久免确认」策略。
- 不把只读能力塞进闸门（读没有副作用，拦它只会让人习惯性点同意）。
- 不重写 lane D 的 op-logging；先用 `IApprovalAuditSink` 自己的 JSONL，接 lane D 时一行适配（见 §6）。
- 不改 lane A/B 的 VCOM/传输层实现。

## 2. 威胁模型

| 威胁 | 具体形态 | 闸门怎么挡 |
|---|---|---|
| T1 AI 自助授权 | MCP 客户端自己填 `confirm:true` 就能写设备 | `McpApprovalGate`：`confirm` 之后再要一次**带外人工**批准；GUI 不在跑就返回 `approval_required_human` |
| T2 脚本静默放行红线 | CI 里 `--yes --allow-redline` 一把梭 | 红线项除 `--yes`+`--allow-redline` 外必须**从 stdin 手输确认串**；非交互 stdin 直接拒绝（exit 4） |
| T3 误点 | GUI 弹窗只有「确定」 | 红线/不可逆项要求键入 `I UNDERSTAND <capability-id>`，且红字标注「不可逆」 |
| T4 确认文案与实际命令不符 | 弹窗写着「重启」，实际发了 `flash` | `ApprovalRequest.Argv` **逐项**展示真实 argv（脱敏后），`Targets` 列出将被写的分区/对象 |
| T5 沉默超时被当同意 | 弹窗没人答，代码默认继续 | 超时 = `TimedOut`（拒绝的一种），只有 `Approved` 才发 `WriteTicket` |
| T6 复用旧授权 | 上次批准过的请求被重放 | `Fingerprint` = 能力+session+argv+targets 的哈希；`SessionApprovalCache` 只按指纹复用，且审计记 `reused=true` |
| T7 绕过闸门直接发命令 | 新代码直接 `fastboot flash` | service 方法签名要求 `WriteTicket`（只有闸门能造）；`tests/SurfaceParity` 静态扫描每条写路径 |

信任边界：**闸门信任本机上的人**，不信任调用方（模型/脚本）。GUI 弹窗是唯一「有人在场」的证据。

## 3. 三个入口怎么接

```
        GUI handler ─┐
        CLI command ─┼─→ ApprovalGuard.AdmitAsync(ApprovalRequest) ─→ IApprovalGate
        MCP tool    ─┘        (fail-closed: 没装 gate 就 NotSupported)      │
                                                                             ├─ GuiApprovalGate  → IApprovalPrompt（模态弹窗，键入确认串）
                                                                             ├─ CliApprovalGate  → --yes / --allow-redline / stdin 确认串
                                                                             └─ McpApprovalGate  → confirm:true + GUI 带外人工批准
                                                                             │
                                          ApprovalDecision → WriteTicket ────┘
                                                    │
                                    Service 层方法（同一个方法，三面共用）
```

- 进程启动时安装一次：GUI → `GuiApprovalGate`；CLI → `CliApprovalGate`；MCP 子进程 → `McpApprovalGate`。
- 未安装时 `ApprovalGates.Current` 返回 `UnsupportedApprovalGate`（fail-closed，`NotSupported`）。
- **`WriteTicket` 是编译期强制**：写 service 方法的第一个参数就是它，没有 ticket 无法调用 —— 静态扫描只是第二道保险。

### 3.1 GUI
模态弹窗逐项显示：能力名 / 副作用 / argv / targets / 红线段 / 可逆性 / 发起方。
普通写：`批准一次` / `本会话批准` / `拒绝`。
红线或不可逆：输入框 + `I UNDERSTAND <capability-id>` 才能点亮确认按钮，且「不可逆」红字。

### 3.2 CLI
- 非红线写：`--yes`（等价于「批准一次」）。
- 红线写：`--yes` + `--allow-redline` + 从 stdin 读确认串；stdin 非交互（`Console.IsInputRedirected` 且读不到）→ 拒绝，exit 4，提示改用 GUI 批准。

### 3.3 MCP
- `confirm:true` 只表示「调用方理解这不是 dry-run」，**不是批准**。
- 之后必须有一次带外人工批准：GUI 在跑（Unix socket 探测）→ 把 `ApprovalRequest` 推给 GUI 弹窗并等待结果；GUI 不在跑 → 结构化错误
  `{"error":{"code":"approval_required_human"},"message":"请在 GUI 里批准或改用 CLI + --yes"}`（MCP `-32603`/exit 4 语义）。
- 红线项即使 GUI 在跑也必须走「键入确认串」分支（GUI 弹窗自己要求手输）。

## 4. 红线门控清单

「红线」= 不可恢复或影响身份/启动链的写入。判据（`ApprovalPolicy.IsRedline`）：

- 分区名/标识命中 `oeminfo nvme persist modem_secure modemnvm* certification hisee_encos veritykey frp config ERECOVERY*`；
- 能力声明 `ApprovalKind.RedlineTypedConfirm`（`write-sn` / `frp-remove` / `unlock-bootloader` / `usb-testpoint` / `reboot-usb-update`）；
- 请求声明 `Reversibility.Irreversible`。

触发任一 ⇒ 必须手输确认串，且审计行 `touchesRedline=true`。

## 5. 审计字段

每条决定一行 JSONL（默认 `$XDG_STATE_HOME/kirin-tool/approvals.jsonl`，可用 `KIRIN_APPROVAL_AUDIT` 覆盖）：

```json
{"ts":"2026-11-12T10:00:00Z","event":"approval.decision","decisionId":"9f2c…","fingerprint":"…",
 "capabilityId":"frp-remove","titleZh":"清除 FRP 锁","origin":"gui","decision":"approved","scope":"once",
 "reused":false,"touchesRedline":true,"reversibility":"irreversible","typedConfirm":true,
 "argv":["erase","frp"],"targets":["frp","config"],"sessionId":"…","reason":null,"tool":"fastboot"}
```

`decision` ∈ `approved | denied | timed_out | not_supported`；`reused=true` 表示命中会话缓存（幂等复用，未再次弹窗）。

## 6. 与 lane D（op-logging）的接入

`IApprovalAuditSink` 是唯一出口。接 lane D 只需一个适配器（一行说明）：

> `ApprovalAudit.Sink = new OpLogApprovalAuditSink(opLogWriter);` —— 把 `ApprovalAuditEntry` 映射成 lane D 的 op-log 记录（`op=approval`，`ts/actor/capability/argv/decision` 同名字段），不改变闸门逻辑。

## 7. 迁移顺序

| 步 | 内容 | 判据 |
|---|---|---|
| M1 | 闸门类型 + 三面 gate + 审计（本节） | `tests/ApprovalGate` ⑥ 全绿 |
| M2 | 注册表加 `ApprovalKind` / `ServiceMethod`；6 条 GUI-only 写能力补 CLI+MCP | `tests/SurfaceParity` H 段全绿；`gui-ui-only.txt` 无写能力 |
| M3 | 三面统一到同一 service 方法 + `WriteTicket` | 反射断言：写 service 方法首参必须是 `WriteTicket` |
| M4 | GUI/CLI/MCP 入口安装各自的 gate | 三入口冒烟（`--yes` 通过 / 无 GUI 的 MCP 拒绝） |
| M5 | 文档与 README | `docs/parity-design.md` §7 + `README.zh-CN.md` |

## 8. 能力 → 三面 → 同一 service 方法 → gate 位置

22 条能力中 8 条是 `Write`，全部三面齐备（`gui-ui-only.txt` 已无写能力，22/22 有 CLI+MCP）。

| 能力 | GUI handler | CLI | MCP | 共用 service 方法 | gate 位置 |
|---|---|---|---|---|---|
| `reboot` | `RebootDevice_Click` | `reboot` | `reboot` | `DeviceWriteOps.RebootAsync` | 每个入口调用 service **之前** `ApprovalGuard.AdmitAsync`（GUI 走 `RequestGuiApprovalAsync`，CLI/MCP 走基类 `GatedDeviceWriteCommand.ExecuteAsync`） |
| `write-sn` | `HandleWriteSerialNumber` | `write-sn` | `write_sn` | `DeviceWriteOps.WriteSerialNumberAsync` | 同上 |
| `frp-remove` | `HandleFrpRemove` | `frp-remove` | `frp_remove` | `DeviceWriteOps.FrpEraseAsync` | 同上 |
| `unlock-bootloader` | `HandleUnlockBootloader` | `unlock-bootloader` | `unlock_bootloader` | `DeviceWriteOps.UnlockBootloaderAsync` | 同上 |
| `usb-testpoint` | `HandleSoftwareTestpoint` | `usb-testpoint` | `usb_testpoint` | `DeviceWriteOps.SoftwareTestpointAsync` | 同上 |
| `reboot-usb-update` | `HandleRebootToUsbUpdate` | `reboot-usb-update` | `reboot_usb_update` | `DeviceWriteOps.FlashRescuePartitionAsync`（每分区）+ `EnterUsbUpdateModeAsync`（模式入口） | 同上 |
| `flash-ota` | `StartFullOtaButton_Click` → `StartFullOtaFlash` → `ConfirmRedlineWriteAsync` | `flash-ota` | `flash_ota` | `FastbootClient.FlashPartition`（逐分区写原语） | GUI：`ConfirmRedlineWriteAsync`；CLI：`FlashOtaCommand` 写前 |
| `flash-xml` | `FlashFromXml_Click`（`FlashPartitionsProcess` 内同样经 `ConfirmRedlineWriteAsync`） | `flash-xml` | `flash_xml` | `FastbootClient.FlashPartition` | GUI：`ConfirmRedlineWriteAsync`；CLI：`FlashXmlCommand` 写前 |

**已知残余差异（如实记录，不粉饰）**：`flash-ota` / `flash-xml` 的三面共用的是**逐分区写原语** `FastbootClient.FlashPartition`，它们上层的编排（取包、解包、pre-op 备份、进度、结果判定）仍各有实现，本轮没有合并（会被 `DeviceWriteOps` 之外的 UI/进度耦合拖成大改）。闸门本身已经完全统一；编排合并单独列为一个后续条目。

### gate 的强制手段（两道，互补）

1. **编译期**：`DeviceWriteOps` 每个写方法首参都是 `WriteTicket`，而 `WriteTicket` 只能由 `ApprovalGuard` 内部 `Mint`。绕过闸门 = 编译不过。
2. **静态扫描**（`tests/SurfaceParity` H 段）：`Write` 能力必须三面齐备 + 声明 `ServiceMethod` + GUI handler（4 跳内可达的方法体）与 CLI 命令都出现 `ApprovalGuard`；CLI 入口装 `CliApprovalGate`、MCP 入口装 `McpApprovalGate`；`gui-ui-only.txt` 不得出现写能力。

## 9. 本 lane 的实测结果（分支 `feat/approval-gate`，基线 `bd641bc`）

| 指标 | 基线 | 本 lane |
|---|---|---|
| 能力 / CLI 命令 / MCP 工具 | 22 / 15 / 15 | 22 / 21 / 21 |
| GUI-only 能力 | 6（全是写能力） | **0** |
| SurfaceParity 检查 | 268 | **363** |
| 新增 gate 测试套件 | — | `tests/ApprovalGate`（68 checks） |
| 测试套件 | 18 套 / 655 PASS | 19 套 / ~800 PASS |

新增测试套件已写入 `tests/suites.txt`，CI 两个流水线自动纳入。

**另需注意的两处测试维护**（不是放宽判定）：
- `tests/StableParity/boundary.json`：`MainWindow.axaml.cs` 的冻结哈希随本次接线更新，reference 文字里写明只改了 gate 接线、命令/时序/重试未变。
- `tests/Kirin9000`：救援流程现在先弹审批框，测试要键入 `I UNDERSTAND reboot-usb-update` 才解锁「Approve once」（并断言未输入时按钮是禁用的）——这正好把 GUI 闸门端到端钉住。

## 10. 不做什么（明确写下来，防止后续被合理化）

## 9. 不做什么（明确写下来，防止后续被合理化）
1. 不允许任何写能力重新回到 `gui-ui-only.txt`。
2. 不允许 `confirm:true` / `--yes` 单独放行红线项。
3. 不允许 gate 直接返回 `Approved` 的「测试专用」捷径进生产代码（测试用 fake prompt，不用 fake gate）。
4. 不允许 CLI 复制 GUI 的业务逻辑；差异只允许在「展示层」。
