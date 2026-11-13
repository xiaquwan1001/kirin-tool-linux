# 结论诚实化（verdict honesty）

> 目标：**任何"成功"都必须有可指认的设备侧证据，或者显式声明这是降级结论**；做不到就报
> "已上传但未验证"，不许报成功。
>
> 基线：`75a18ad`（VCOM 真执行 + 会话日志）。审计出处：
> `docs/audit-hardcoded-and-hidden-steps.md`（S 系列静默降级 / F 系列假成功）、
> `docs/audit-gui-blackbox.md`（B3 "结论只活在 UI"）。

## 1. 现状假成功清单

"假成功"= 返回 `true` / 弹成功文案，但没有任何设备侧独立确认（无 OKAY、无读回、无 size/CRC
对比，或确认标准低于同仓其它路径）。

| # | 审计条目 | 位置 | 假成功内容 | 本 lane 处置 |
|---|---|---|---|---|
| 1 | S01 / S02 / H27 | `Services/VcomFlasher.cs` ACK 读取 | 历史上 ACK 校验被整段注释；35c6f6b 起有 `AckMode` 记录但**结论里没有 ACK 证据** | 已修：ACK 计数进结论（`verified/reason/missing/missingRatio`，全局 + 每阶段），`--require-ack` 让证据缺失变成失败 |
| 2 | F01 | `MainWindow.axaml.cs` `HandleUnlockFastboot` | 进度窗标题 "Unlock Complete" 只代表"上传函数没抛异常" | 已修：`verified` 才绿色 "Upload Verified"；否则琥珀色 "Uploaded — Unverified" + ACK 计数 |
| 3 | F02 / S15 | `Services/FirmwareUnlocker.cs` 缺 loader 分支 | 缺 `.ktl` 标 `Skipped` 继续 → 最终仍 `IsSuccess=true` | 已修：发第一帧前预检全部 loader（存在 + DTL 可解），缺一即 `MissingLoader`，`framesSent=0`；`Skipped` 语义删除 |
| 4 | S10 / S11 | `Cli/Commands/WriteCommands.cs` `PreOpBackupCli.RefusalExit` | 备份目录创建失败（`Attempted=false`）时 `--require-backup` 静默放行 | 已修：`!Attempted` 与 `!Complete` 同样拒绝（exit 4）+ 原因 + JSON `reason=require_backup_failed` |
| 5 | S12 | `Cli/ArgParser.cs` | 未登记选项一律当 flag，`--require-backup` 拼错即静默关闭闸门 | 已修：未知选项 exit 1；`--lenient-args` 兼容开关 + 警告 |
| 6 | F04 / S18 | `MainWindow.axaml.cs` `HandleWriteSerialNumber` | 写 NVE SN 只看退出码就弹 "Successfully rewrote…" | 已修：写后 `getvar nve:SN` 读回比对，仅一致才成功；否则 "Write Not Confirmed" + 读回值 + 原因 |
| 7 | `MainWindow.axaml.cs` `HandleUnlockFastboot` 的 `Skipped` 文案 | 进度行没有设备侧含义 | 已修：`Verified` / `Missing` 两态取代 `Decrypting`→`Skipped` 的模糊状态 |
| 8 | F03 / S17 | `MainWindow.axaml.cs` flash 计数 | `skipSecure` 分区被计成 success | **未做**（见 §9） |
| 9 | F07 / S20 | `WriteCommands` JSON `applied=true` | `flashed 0/N`（exit 5）时也输出 `applied:true` | **未做**（见 §9） |

> 判据说明：`Skipped`/未验证 ≠ 失败。**它必须被标成未验证**，而不是被改写成成功，也不是
> 被强行改成失败（后者会让"设备确实收到了但没答话"这一常见情形与"根本没发出去"无法区分）。

（本节 DONE-1）

## 2. 判定模型

一次写/加载操作有两个**正交**的结论：

| 维度 | 含义 | 判据来源 |
|---|---|---|
| `applied` | 上位机是否真的把字节/命令发出去了 | 本机：帧写出计数、进程退出码、命令返回 |
| `verified` | 设备侧是否有独立证据表明它接受了 | 设备侧：ACK 字节（VCOM）、OKAY/FAILED（fastboot）、读回值（NVE SN） |

因此四态：

| applied | verified | `gate` | 结论串 |
|---|---|---|---|
| true | true | `applied` | `unlocked-fastboot-loaded`（设备 ACK 逐帧确认） |
| true | false | `applied-unverified` | `loaders-uploaded-unverified`（**本 lane 的核心产物**） |
| false | false | `failed` / `refused` / `plan-only` | `failed` |
| false | true | 不可能（无写出就没有设备证据） | — |

`reason`（机器可读，稳定，按优先级）：

| reason | 含义 | 退出码 |
|---|---|---|
| `ack_ok` | 所有期待 ACK 的帧都收到 `0xAA` | 0 |
| `ack_missing:<n>` | `n` 帧没有拿到可用 ACK（超时或非 `0xAA`） | 0；加 `--require-ack` 后 3 |
| `ack_not_requested` | `AckMode=Off`：从未读取 ACK，没有任何传输层证据 | 0；加 `--require-ack` 后 3（且被参数校验禁止） |
| `missing_loader` | 发帧前预检失败：loader 缺失或无法 DTL 解密 | 5 |
| `readback_mismatch:<v>` / `readback_unavailable` / `command_failed` | NVE 写入读回失败（GUI 文案 + `NveWriteVerdict.Reason`） | — |
| `require_backup_failed` | `--require-backup` 拒绝（备份未做或不完整） | 4 |
| `not_attempted` / `replug_required` / `cancelled` / `no_device` | 没走到设备 | 4 / 4 / 4 / 2 |

`missingRatio = missing / framesExpectingAck`。分母**不含**"本就不期待 ACK"的帧（START、TAIL、
exploit 的第二次 HEAD），否则握手帧会把比例稀释成假的健康值。

（本节 DONE-2）

## 3. 每个能力的证据来源

| 能力 | 设备侧证据 | 无证据时的结论 |
|---|---|---|
| VCOM loader 上传（`vcom-unlock` / GUI VCOM unlock） | 每帧 `0xAA` ACK（`--ack-mode strict|lenient`） | `verified=false`，`reason=ack_missing:<n>`（strict 下是传输中断：`failureKind=device-rejected` + exit 3）；`off` ⇒ `ack_not_requested` |
| loader 预检 | 本机文件系统 + DTL 解密（决定"会不会发第一帧"） | `FailureKind=MissingLoader`，`framesSent=0`，exit 5 |
| fastboot 写命令（SN / model / vendor） | 输出 OKAY/FAILED（`ClassifyCommandOutput`）+ 写后读回 | GUI "Write Not Confirmed" + 读回值 |
| 写前备份 | 备份目录 + manifest + 每分区 sha256 | `--require-backup` ⇒ exit 4 + 原因 |
| 参数解析 | 选项是否在 `CliParam` 表里登记 | 未知选项 exit 1（`--lenient-args` 降级为警告） |

（本节 DONE-3）

## 4. 迁移与兼容

### 4.1 CLI

| 变化 | 兼容性 |
|---|---|
| `vcom-unlock` 新增 `--require-ack` | 新增可选开关，默认关闭 |
| JSON 新增 `verified` / `reason` / `requireAck` / `ack.{missing,missingRatio,acked,notExpected}` / `stages[].{framesSent,missing,missingRatio}` | 纯增量；原有键（`applied`/`gate`/`ack.{expected,ok,bad,timeouts}`/`stages[]`/`conclusion`/`exitCode`）语义与取值不变 |
| `gate` 新增取值 `applied-unverified` | 之前只有 `applied`，故只读 `gate=="applied"` 的消费者仍能正确区分"验证过的成功" |
| `conclusion` 新增取值 `loaders-uploaded-unverified` | 同上 |
| 未知选项 exit 1 | **行为变更**（原来当 flag 收下）。兼容开关 `--lenient-args`；`--help` 优先于该报错 |
| `flash-ota`/`flash-xml`：`--require-backup` + 备份未做 ⇒ exit 4 | **行为变更**（原来静默放行）。这正是 S10 要修的假成功 |
| `--no-backup` + `--require-backup` ⇒ exit 1 | **行为变更**（互相矛盾，原来后者被静默吞掉） |
| `--no-backup` 审计行 | 文案变明确：`pre-op backup SKIPPED (--no-backup): …` |

### 4.2 为什么 lenient 仍 exit 0，而默认已经不是 lenient

`tests/VcomUnlockCli` 在 35c6f6b 已固化"lenient + 设备静默 ⇒ exit 0"这一 CLI 闸门契约
（`applied` 描述的是"字节已写出"，与"设备确认"分开）。结论诚实化 lane **没有**动这条契约，
只把诚实结论放进 `verified/reason`：

- 显式 `--ack-mode lenient`：`applied=true, verified=false, gate=applied-unverified, exit 0`（结论诚实、闸门契约不变）
- `--ack-mode lenient --require-ack`：同一场景 `gate=failed, failureKind=ack-missing, exit 3`

**v2.4.15 起默认值分入口**（[ack-default.md](ack-default.md)）：CLI/MCP 的 `--ack-mode` 默认改为
`strict`，于是"设备一声不吭"在自动化侧直接 exit 3（`failureKind=device-rejected`，ACK 证据仍在
`ack.timeouts/missing`）；GUI 保持 `lenient`。要旧的退出码就显式写 `--ack-mode lenient`——
那仍然是"诚实的 0"，不是"静默的成功"。

### 4.3 GUI

| 变化 | 位置 |
|---|---|
| `verified` ⇒ 绿色 "Upload Verified"；否则琥珀色 "Uploaded — Unverified" + `frames/ACKed/missing` | `MainWindow.axaml.cs` `HandleUnlockFastboot`（见 §6 行区间） |
| 新 `ProgressDialog.ShowVerdict(verified, title, detail)`（琥珀 `#B26A00` / 绿 `#2E7D32`） | `UI/ProgressDialog.axaml.cs` |
| 写 SN 后读回比对 | `MainWindow.axaml.cs` `HandleWriteSerialNumber` |
| GUI 的 VCOM 会话日志现在也含 `frame.write`/`frame.ack` 行 | `HandleUnlockFastboot` 传入 `VcomFlasherOptions{Logger,FrameObserver}`（此前 GUI 传 `Flasher=null`，帧行缺失） |

（本节 DONE-4）

## 5. 测试计划

见 §8（19 套，新增 `tests/VerdictHonesty`）。

## 6. 改动的 GUI 行区间（与其它 lane 的边界）

`MainWindow.axaml.cs`（`git diff` 行区间，基线 `75a18ad`）：

| 新文件行号（基线 `75a18ad` 的 `git diff -U0`） | 内容 |
|---|---|
| `MainWindow.axaml.cs:24` | 新增 `using Kirin_Tool.Services.Verdict;` |
| `MainWindow.axaml.cs:1535-1548` | `HandleUnlockFastboot`：`VcomAckLedger` + `VcomFlasherOptions{Logger,FrameObserver}` + `StageBeginObserver` |
| `MainWindow.axaml.cs:1556-1569` | 同一方法收尾：三态（`ShowVerdict(verified,…)` / "Uploaded — Unverified" / "Unlock Failed"），替换原 `ShowCloseButton(IsSuccess,"Unlock Complete")` |
| `MainWindow.axaml.cs:2159-2175` | `HandleWriteSerialNumber`：写后 `ReadNveVariable("SN")` 读回 + `NveWriteCheck.Compare`，不一致弹 "Write Not Confirmed" |
| `UI/ProgressDialog.axaml.cs:6,49-70` | 新增 `ShowVerdict(verified,title,detail)`（琥珀/绿 + 计数行） |

其余 GUI 文件只动 `UI/ProgressDialog.axaml.cs`（新增一个方法，未改 `ShowCloseButton`/`ShowLogPath`）。
`Services/Approval/*` 未触碰（审批层留给另一 lane；需要调用处留了说明性注释，无 `IApprovalGate`
强依赖）。

## 7. 冻结边界（StableParity boundary.json）

`tests/StableParity/boundary.json` 冻结了协议/设备路径相关文件。本条 lane 改动了其中 **3 个**，
`reference` 字段逐条写明改动性质（`tests/StableParity` 本身未放宽：仍是逐文件 sha256 比对，35 checks）：

| 文件 | 改动性质 |
|---|---|
| `Services/FirmwareUnlocker.cs` | 发帧前 loader 预检 + `UnlockFailureKind.MissingLoader` + `UnlockOptions.StageBeginObserver`；`CpuAddresses` 表、帧序列、延时未变 |
| `MainWindow.axaml.cs` | VCOM 收尾按 ACK 证据分三态；SN 写后读回。设备命令未变 |
| `UI/ProgressDialog.axaml.cs` | 新增 `ShowVerdict`；`ShowCloseButton`/`ShowLogPath` 未变 |

**`Services/VcomFlasher.cs` 未被改动**（协议帧布局/CRC/地址/延时/ACK 读取逻辑保持基线哈希）：
ACK 会计完全在新的 `Services/Verdict/VcomAckLedger.cs` 里，靠既有的 `FrameObserver` 取值。

## 8. 测试（19 套，全绿）

新增 `tests/VerdictHonesty`（75 checks；v2.4.15 加默认 strict 用例后 81 checks，已加入 `tests/suites.txt`，CI 两条流水线都会跑）：

| 用例 | 断言 | 对应审计 |
|---|---|---|
| ①a 设备全程静默（**默认 = strict**，v2.4.15） | exit 3、`verified=false`、`gate=failed`、`failureKind=device-rejected`、`ack.timeouts>0` | S01/S02/F01 |
| ①a′ 设备全程静默 + 显式 `--ack-mode lenient`（GUI 策略） | `verified=false`、`applied=true`、`gate=applied-unverified`、`reason=ack_missing:N`、`missingRatio=1`、exit 0（既有闸门契约） | S01/S02/F01 |
| ①b 设备全程静默 + `--require-ack`（配显式 lenient：strict 会先中断上传） | exit 3、`gate=failed`、`failureKind=ack-missing` | 同上 |
| ①c `--ack-mode off` | `verified=false`、`reason=ack_not_requested` | S02 |
| ② 只有一帧 missing（第 2 帧回 `0x55`，显式 lenient + `--require-ack`） | `reason=ack_missing:1`、`ack.bad=1`、`0<missingRatio<1`、`--require-ack` ⇒ exit 3 | S01 |
| ③ loader 缺失 / 无法 DTL | `failureKind=missing-loader`、`framesSent=0`、`reason=missing_loader`、该阶段 `status=missing`、**无 `status=skipped`**、exit 5 | F02/S15 |
| ④ `--require-backup` + 不可写目录（`/proc/…`） | `Attempted=false` + 原因；`BackupGate` ⇒ 拒绝、exit 4、"never taken"；无该开关时仍只是警告；`--no-backup`+`--require-backup` ⇒ exit 1 | S10/S11 |
| ⑤ `--getvr` / `--jso` / `--require-backu` | exit 1 + 指名未知选项；`--lenient-args` 下放行并警告；`--help` 优先 | S12 |
| ⑥ SN 写回 | 仅"读回值 == 期望值"算成功；不一致 ⇒ `readback_mismatch:<v>` 且提示"未生效"；命令失败/读不到/空值都不得算成功 | F04/S18 |
| 纯函数 | `VcomVerdict.From` 五态、`VcomAckLedger` 的分阶段归属（null 与 XLOADER 同址 0x22000）、`UnlockVerdictText` 文案 | — |

全量：`dotnet build -c Release` 0 error；`tests/suites.txt` 逐套 `dotnet run --project tests/<suite>`：
**19/19 绿**（基线 18 套 + 新增 1 套）；`StableParity` 35 checks 全绿。

（本节 DONE-8）

## 9. 明确未做（本 lane 范围外）

| 条目 | 为什么没做 |
|---|---|
| F03 / S17（`skipSecure` 计入 `successCount`，"all N partitions" 永远成立） | 属 flash 计数语义，`MainWindow` flash 路径是另一 lane 正在改的区域 |
| F07 / S20（`applied:true` 在 `flashed 0/N` 时仍输出） | 同上，写命令 JSON 语义 |
| F05（OMeInfo 转换 0 命中仍 `IsSuccess`）、F09/S22（dump 只判 `Length>0`）、S19（`disablewp` 结果丢弃） | 各自需要独立的设备侧判据设计，不在本 lane 的四条主线内 |
| F10/S06/S07（USB Update 跳过/截断计成功） | 需要重做 `FlashPartitions` 的失败聚合，改动面在 USB Update 服务层 |
| 把"无设备证据 ⇒ 非 0"设成默认 | 见 §4.2：会改掉既有 `tests/VcomUnlockCli` 的闸门契约 |
| 真机验证（ACK 严格模式下真实 BootROM 是否逐帧回 `0xAA`） | 本 lane 禁止碰真机；`--require-ack` 默认关闭，待有设备时实测后再考虑改默认 |
