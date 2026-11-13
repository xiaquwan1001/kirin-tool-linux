# 债务登记 / Debt register (baseline: v2.4.14, commit `2ade54b`)

这份文件回答一个问题：审计报告里列的 **36 条 GUI 黑盒 + 95 条硬编码 + 43 条静默降级 + 21 条假成功 +
27 条平台假设**（共 222 条，见 [audit-gui-blackbox.md](audit-gui-blackbox.md)、
[audit-hardcoded-and-hidden-steps.md](audit-hardcoded-and-hidden-steps.md)）之中，**哪一条已经修了、哪一条还是债**。

审计报告本身是只读快照（`35c6f6b`），只记录「当时有什么问题」。本文件是它的**状态侧**，逐条给证据。
没有证据的条目不写「已修」——不确定的一律标 `unknown` 并写清缺什么证据。


## v2.4.15 变更（2026-11-13）

- **D-01 → fixed-in-2.4.15**：`oeminfo` 拆成 `oeminfo-convert`（PlanOnly，只在本机构造镜像）与
  `oeminfo-flash`（Write + 红线 + 不可逆 + 手输确认串）。GUI 的 `HandleOemInfoFlash` 先过
  `RequestGuiApprovalAsync`，再走 `DeviceWriteOps.FlashOemInfoAsync(ticket, path)`；CLI/MCP 同名
  能力同步。注册表不再谎报 PlanOnly，`SurfaceParity` 的 H1/H3/H7 会拦住任何回退。
- **D-02 → fixed-in-2.4.15**：`WriteResult` 只接受调用点构造的 `WriteReport`（Applied / Partial /
  Failed / Unconfirmed），没有默认 `true`。9 个写命令 + `flash-ota`/`flash-xml` 的 JSON 全部取设备
  真实结论；`tests/WriteGates` 用桩断言失败时 `applied=false, gate=failed`。
- **D-03 → fixed-in-2.4.15**：CLI/MCP 的 `write-sn` 写后读回 `nve:SN`，用 `NveWriteCheck.Compare`
  出 verdict（`verified / reason / readBack`），只有读回一致才 exit 0。
- **D-13 → fixed-in-2.4.15**：`downgrade-enable` 拆成 `downgrade-enable`（PlanOnly，只打印 4 条计划、
  exit 4）与 `downgrade-erase`（Write + 红线 + 不可逆 + 手输确认串）。GUI `HandleEnableDowngrade` 先过
  `RequestGuiApprovalAsync`，再走 `DeviceWriteOps.EnableDowngradeAsync(ticket)`（op 日志
  `downgrade.erase`）；CLI/MCP 同名能力同步，结论走 `WriteReport`（0 条 OKAY = `applied=false,
  gate=failed`）。设备侧仍是 4 条 `oem oeminfoerase-*ver`，顺序与时序未变（[downgrade-gate.md](downgrade-gate.md)）。
- **日志已合流（v2.4.15）**：一次操作 = `sessions/<ts>-<pid>-<op>/`（`session.jsonl` + `vcom-trace.txt`
  + `summary.json`），新增只读 `logs list|show|path`；旧 `{vcom,fastboot,flash}/*.jsonl` 只读兼容。
- **D-04 → fixed-in-2.4.15**：USB Update 通道改出三态结论（`verified / partial / failed`），
  握手未建立、缺 `.img`/`.img.header`/0 字节、读到 0 字节早退、空 `else` 全部变成
  `degraded:`/`skipped:`/`missing:` 事件；`FlashPartitions` 返回 `UsbUpdateVerdict`，GUI/CLI 按三态显示，
  `WriteReport` 映射 Verified→Applied、Partial→Partial(exit 3)、Failed→Failed(exit 3)。协议命令/时序未变
  （[usb-update-verdict.md](usb-update-verdict.md)，`tests/UsbUpdateVerdict`）。
- **D-13 → fixed-in-2.4.15**：`downgrade-enable` 拆出 `downgrade-erase`（Write+红线+不可逆），
  GUI 先过 `RequestGuiApprovalAsync` 再走 `DeviceWriteOps.EnableDowngradeAsync(ticket)`；CLI/MCP 同步（[downgrade-gate.md](downgrade-gate.md)）。
- **剩余 open 项**：见下表 `open` 行；`D-12`（整包 USB Update 仍无 CLI/MCP 面）等下一轮。

## 0. 口径

| 状态 | 含义 |
| :--- | :--- |
| `fixed-in-2.4.14` | 代码已改，证据是「文件:行」或「提交号」或「门禁/测试名」。行号取本 worktree 基线 `2ade54b`。 |
| `open` | 仍是债。按「会不会让用户或 AI **误判成功/误操作**」排序，越靠前越危险。 |
| `wontfix` | 有意不修，后面必须跟理由（谁决定的、为什么不修）。 |
| `unknown` | 没核实到，写清需要什么证据才能定状态。 |

排序原则（本文件唯一排序依据）：**先看「会不会让人或 AI 以为写成功了」**，再看「会不会让 AI 发起本不该发起的写」，
最后才是「可读性/可移植性/美观」。审计编号沿用原报告（`BB-` / `H` / `S` / `F` / `P`）。

---

## 1. 已修（fixed-in-2.4.14）

| 编号 | 摘要 | 状态 | 证据 |
| :--- | :--- | :--- | :--- |
| S01 / S02 / H27 | VCOM 帧 ACK 校验从「整段注释」变成真的判 `0xAA`；`strict` 超时/坏 ACK 直接抛，`lenient` 只记录不中断（GUI 历史行为），`off` 盲发 | fixed-in-2.4.14 | `Services/VcomFlasher.cs:279-338`（`isAck = ack == 0xAA`，strict 抛错）；模式定义 `:38-46`；缺帧统计进账本 `Services/Verdict/VcomAckLedger.cs` |
| F01 | GUI 不再把「loader 上传完」写成 "Unlock Complete"，改为按 `verified` 显示 "Upload Verified" / "Uploaded — Unverified" | fixed-in-2.4.14 | `MainWindow.axaml.cs:1582`；`UI/ProgressDialog.axaml.cs:51-61` |
| F02 / S15 | 缺 `.ktl` 的 stage 不再 `Skipped`+继续：loader 预检在发第一帧前失败（`MissingLoader`，`framesSent=0`），阶段中间缺文件也中止 | fixed-in-2.4.14 | `Services/FirmwareUnlocker.cs:186`（注释写明旧行为）、`:202`、`:251`；`Services/Verdict/LoaderPrecheck.cs` |
| S10 / S11 | `--require-backup` 在「备份目录建不出来（`!Attempted`）」时也拒绝写入（exit 4），不再静默放行 | fixed-in-2.4.14 | `Services/Verdict/BackupGate.cs:23-42`（`!result.Attempted` 判为 refusal，`RefusalExitCode=4`）；调用点 `Cli/Commands/WriteCommands.cs:785-811` |
| S12 | 未登记的 CLI 选项是用法错误（exit 1），`--lenient-args` 才恢复旧的「当 flag 收下」并把未知项回报给调用方 | fixed-in-2.4.14 | `Cli/ArgParser.cs:66-121`（`UnknownOptions` + 抛 `CliUsageException`） |
| F04 / S18（GUI 侧） | 写 NVE 序列号后读回 `getvar nve:SN` 比对，只有完全一致才报 Success；不一致弹 "Write Not Confirmed" 并附读回值与原因 | fixed-in-2.4.14 | `MainWindow.axaml.cs:2247-2261`；判定 `Services/Verdict/NveWriteVerdict.cs:41-60` |
| BB-01 | `vcom-unlock` CLI/MCP 不再打印 "not invoked in this build"，真的走 `FirmwareUnlocker.UnlockFastboot` + `VcomFlasher`（与 GUI 同源） | fixed-in-2.4.14 | `Cli/Commands/VcomUnlockCommand.cs:20,318-330`；注册表行 `Services/Capabilities/CapabilityRegistry.cs:92-104`（`CapabilityWriteKind.Write`）；测试 `tests/VcomUnlockCli`（pty 假 BootROM） |
| BB-02 | `unlock-bootloader` 成为三面写能力（`oem unlock`，红线+不可逆，需手输确认串） | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:261-296`；注册表 `:130-137`；GUI `MainWindow.axaml.cs:2402-2417` |
| BB-03 | `frp-remove` 成为三面写能力（`erase frp`/`erase config`/`oem frp-erase`） | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:219-258`；注册表 `:122-129` |
| BB-06 | 写 SN 有了独立写命令 `write-sn`（不再只有 GUI 的「写伪装成 getvar」），`getvar` 仍然只读 | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:175-216`；注册表 `:114-121`；`Services/FastbootClient.cs:118-122`（op 名 `fastboot.setvar.nve`） |
| BB-07 | `reboot-usb-update` 成为三面写能力（先刷救援分区，再读回 `rescue_*` 判模式） | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:366-450`（`modeEntryConfirmed` 按 OKAY 判）；注册表 `:147-154` |
| BB-08 | `usb-testpoint` 成为三面写能力（改 XLOADER + VCOM 传输） | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:299-360`；注册表 `:138-146` |
| BB-10 | `reboot` 成为三面写能力 | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:143-171`；注册表 `:155-160` |
| BB-34 | 解锁 BL 不再自建裸 `Process`：改走 `ProcessRunner.RunStreamingAsync`（统一超时、杀进程树、JSONL 日志） | fixed-in-2.4.14 | `Services/FastbootClient.cs:221-247` |
| H39 | `oem unlock UUUU…` 从写死魔数变成「有默认值、可覆盖、先校验 16 字符、非默认值按凭据打码」的参数 | fixed-in-2.4.14 | `Services/FastbootClient.cs:41-47,224-247`（`DefaultUnlockCode`/`UnlockCodeLength`/长度校验/`RegisterSecret`） |
| P22 | MCP 侧 `confirm:true` 不再是「一句话就真写」：写工具一律过审批闸门，没人带外批准就 `approval_required_human` | fixed-in-2.4.14 | `Cli/Mcp/McpServer.cs:161-171`（`confirm` 描述仍声明无 dry-run）；审批 IPC `MainWindow.axaml.cs:1787-1817`；设计 [approval-gate.md](approval-gate.md) |
| BB-11（部分） | `psid` 有了 CLI/MCP 面（`oem get-psid`）；`partitions`/`capabilities`/`udev` 也补齐只读三面 | fixed-in-2.4.14 | `Cli/Commands/InspectCommands.cs:87-88,16-17,161-162,231-232`；注册表 `:69-72` |
| BB-13（CLI 侧） | CLI 的软件测试点不再用相对 CWD 的 `dload`，改落 `$TMPDIR/kirin-tool-dload` | fixed-in-2.4.14 | `Cli/Commands/DeviceWriteCommands.cs:342-345` |

> `fixed-in-2.4.14` **不等于「已上真机验证」**。真正上过硬件的只有 ELS-AN00 的 `flash-ota`
> 路径，其余见 README「已验证 / 未验证」表。

---

## 2. 未修（open，按危险度排序）

### 2.1 会让人/AI 误判「写成功」或误判「没风险」

| # | 编号 | 摘要 | 状态 | 证据 / 为什么还是债 |
| :--- | :--- | :--- | :--- | :--- |
| D-01 | BB-05 / S19 / F05 / P20 | **GUI「Convert OEMInfo」的直写分支仍然直写设备**（`oeminfoerase-disablewp` + `oeminfowrite-KTModel@/KTVendor@`），既不经过审批闸门，也没有 CLI/MCP 对应物；而注册表把 `oeminfo` 声明成 `PlanOnly` → 三面契约与现实矛盾 | fixed-in-2.4.15 | GUI 直写 `MainWindow.axaml.cs:2526,2533,2554`，分支入口 `:2500`（`useDirectCommand`）；`BtnConvertOem_Click`（`:2485`）直到 `:2570` 之间没有任何 `RequestGuiApprovalAsync`/`ConfirmRedlineWriteAsync`（闸门只出现在 `:1517,1602,1846,1888,2030,2227,2409,2675,3039`，其中 `:2675` 属另一条「转换后 Flash Back」路径 `FlashOemInfoToDevice`）；注册表 `CapabilityRegistry.cs:105-108`；`oeminfoerase-disablewp` 返回值被丢弃（S19）。**误判后果**：读文档/注册表的人以为 oeminfo 只出计划，实际 GUI 一点就写；写 SN/FRP 都要手输确认串，这里连普通确认都没有 |
| D-02 | F07 | 所有新写命令共用 `WriteResult`，**无条件**输出 `applied=true, gate="applied", exitCode=0`，即使 `result.success=false` | fixed-in-2.4.15 | `Cli/Commands/DeviceWriteCommands.cs:118-133`。**误判后果**：只读 `applied`/`gate`/`exitCode` 字段的 AI 会把失败写当成功（这是审计 F07 的更严重版本，覆盖面从 flash 扩到全部写命令） |
| D-03 | F04 残余（CLI/MCP 侧） | `write-sn` 的 CLI/MCP 路径**没有读回比对**，只有 fastboot 退出码 | fixed-in-2.4.15 | `Cli/Commands/DeviceWriteCommands.cs:210` 原文 "no read-back check is available on this path"；GUI 侧已有（§1）。**误判后果**：同一个能力在 GUI 会报「未确认」，在 CLI/MCP 报成功 |
| D-04 | F10 / S03 / S04 / S06 / S07 / S09 / F13 | USB Update 通道的成功判据仍然虚：握手 3 次失败只写一行日志继续；缺 `.img`/`.img.header`/0 字节的分区 `continue` 且不计失败；读到 0 字节 `break` 后 `return null`（=成功）；`if (success) … else { }` 空分支 | fixed-in-2.4.15 | 五处全部改成显式结论/事件：握手 `DoHandshake` 返回 bool（失败 = `HandshakeEstablished=false` + `degraded:`），缺 `.img`/`.img.header` → `Skipped[]`、0 字节 → `Missing[]`（`skipped:`/`missing:`），`bytesRead==0` → `NoteStreamEndedEarly`（不再是成功），空 `if/else` → `degraded:` 事件。结论三态见 `Services/USBUpdate/UsbUpdateVerdict.cs`，上层映射见 [usb-update-verdict.md](usb-update-verdict.md)；`tests/UsbUpdateVerdict`（39 checks）逐条覆盖；`StableParity` 哈希按「结论模型变化、协议未变」刷新 |
| D-05 | F03 / S17 | GUI 把被跳过的分区计成成功：`CompletePartition(..., true, "Skipped")` 且 `successCount++` → 「Successfully flashed all N partitions!」恒成立 | open | `MainWindow.axaml.cs:637-638`、`:698-701`。CLI 侧已有 `OtaPlan.Skipped` 单列（`Cli/Commands/OtaFlashRunner.cs:32,212`），GUI 没有 |
| D-06 | H20 / H23 | pwn 后跳转的 `xloaderStart` 与漏洞 `returnAddr` 仍是 `switch` 里的裸字面量，与 `CpuAddresses` 表分离，无法外置/审查 | open | `Services/VcomFlasher.cs:213-222`、`:239-247`。审计定为「变砖级」字面量，改一个字节即换行为；外置表尚未做 |
| D-07 | P11 / P12 / P13 / H32 | 找不到随包 fastboot 时**静默**回落到 `/usr/bin/fastboot` → PATH（Google platform-tools 对华为临时 loader 的 `getvar` 全面失败）；`doctor` 的 `ok` 只判「找到 && 能跑」，不判血统 | open | `Services/FastbootClient.cs:78-86`（回落后无警告）；`Cli/Commands/ReadCommands.cs:191`（`ok = fastbootPath != null && fastbootVersion != null`）、`:143-156`（有 per-check 表，无 parity 探针）。**误判后果**：AI 按 `ok:true` 认为环境健康，随后所有读都失败并被归因到「连接问题」 |
| D-08 | F17 / F18 | `doctor` 的 `ok` 与 `info` 的「连上但一无所知」语义仍未区分 | open | `Cli/Commands/ReadCommands.cs:191`、`:27-36`（`connected` 为真即 exit 0，变量可全空） |
| D-09 | F09 / S22 | dump 成功判据仍是「文件存在且 >0 字节」，没有与 header 声明大小/分区大小比对 | open | `Services/FastbootFlasherService.cs:92-97`（`info.Exists && info.Length > 0`） |
| D-10 | F12 / P27 | `ClassifyCommandOutput` 仍按子串判 OKAY/SUCCESS 与 FAIL/ERROR（已补 `NOT ALLOWED`/`NOT SUPPORT`），且匹配的是 stdout+stderr 混流 | open | `MainWindow.axaml.cs:2292-2303`；混流来源 `Utils/ProcessRunner.cs`（`Output = output + error`，S24 未变） |
| D-11 | F05 | oeminfo 转换「没有任何块命中」时仍 `IsSuccess=true` | open | `Services/OemInfoService.cs:81`；命中条目数/diff 未纳入判定 |

### 2.2 会让 AI 发起本不该发起的动作（可达性 / 闸门缝隙）

| # | 编号 | 摘要 | 状态 | 证据 |
| :--- | :--- | :--- | :--- | :--- |
| D-12 | BB-09 | GUI 的「USB Update 模式刷整包」仍无 CLI/MCP 面（`flash-ota` 只走 fastboot） | open | GUI `MainWindow.axaml.cs:968,979`（`StartUsbUpdateFlash`）；注册表全表（`CapabilityRegistry.cs:64-160`）无对应能力 |
| D-13 | BB-04 / H90 | `downgrade-enable` 仍 `PlanOnly`：CLI/MCP 只打印 4 条 `oem oeminfoerase-*` 计划，GUI 照旧真擦，**且 GUI 这条写路径也不经过审批闸门**（`HandleEnableDowngrade` 内直接 `EnableDowngradeWithSteps()`，只有装饰性 `Task.Delay(500)`×4） | fixed-in-2.4.15 | 拆成 `downgrade-enable`（PlanOnly，exit 4）与 `downgrade-erase`（Write + 红线 + 不可逆 + `RedlineTypedConfirm`，CLI/MCP 同名）：注册表 `Services/Capabilities/CapabilityRegistry.cs`；GUI `HandleEnableDowngrade` 先 `RequestGuiApprovalAsync` 再走 `DeviceWriteOps.EnableDowngradeAsync(ticket)`（op 日志 `downgrade.erase`，verdict `step-outcomes:n/4-okay`）；门禁 `SurfaceParity` H1/H3/H7 + `ApprovalGate` + `WriteGates`；详见 [downgrade-gate.md](downgrade-gate.md) |
| D-14 | BB-13 / BB-16（GUI 侧） | GUI 仍用相对 CWD 的 `dload`，且开跑前 `Directory.Delete(dload, true)`（无 sentinel 校验） | open | `MainWindow.axaml.cs:159,165`（字面量 `"dload"`）；`Services/USBUpdate/SoftwareTestpointService.cs:46-49` |
| D-15 | BB-19 | rescue 目标分区仍由 **CPU 下拉** 决定，不读设备真实平台 | open | `MainWindow.axaml.cs:2027`（`RescuePartitionPlan.ForChip(GetSelectedCpu())`） |
| D-16 | S14 | 电量 <50% 仍只是横幅，任何写路径都不因电量被拒 | open | `MainWindow.ChipCapabilities.cs:72-95` |
| D-17 | H45 | 能力矩阵仍是「不是 Kirin9000 就全允许」（未知 SoC 默认放行） | open | `Services/ChipCapabilities.cs:8` |
| D-18 | P02 / H15 | Linux 自动探测仍按「内核驱动名恰好是 `option`」决定是否关 RTS/DTR（已有 `--port`/`BulkOnly` 逃逸，默认路径未改成能力探测） | open | `Services/VcomFlasher.cs:117-122` |
| D-19 | H06 / H50 / H51 | loader 目录仍是 `<base>/loaders/<cpu>`：CLI 只有 `--loader-dir` 参数，没有 `KIRIN_LOADERS_DIR`；命名仍是 `<stage>.ktl`/`fastbootf.ktl` | open | `Services/FirmwareUnlocker.cs:89,178`；`Cli/CommandSupport.cs:195-229` |
| D-20 | P15 | `packaging/publish.sh` 不产出 `loaders/`，但 deb/AppRun 假定它存在 → 打包产物里 VCOM 解锁会报 `Loader directory for <cpu> not found` | open | `packaging/build-deb.sh:5`、`packaging/AppRun:3`（注释假定），`packaging/publish.sh` 内无 `loaders` 拷贝 |
| D-21 | P21 | MCP `protocolVersion` 固定 `2024-11-05`，不校验客户端版本 | open | `Cli/Mcp/McpServer.cs:14,112` |
| D-22 | P09 / P08 | udev 文案与随包规则仍是两套写法（内置 `MODE=0660 + TAG uaccess` 无 GROUP；包用 `GROUP=plugdev`/`adbusers`/`uucp`）；且检查是静态读文本，`TAG+="uaccess"` 依赖 logind 会话，SSH/无桌面会话照样拿不到权限 | open | `Services/UdevRuleNotice.cs:19-22,62-78`；`packaging/51-kirin-tool-fastboot.rules:1-10` |
| D-23 | BB-14 / BB-25 / BB-29 / S34 | GUI 侧隐式步骤仍在：写前备份恒开不可关、结果只进进度框一行；USB Update 新红线分区二次确认后台阻塞（超时=静默取消）；路径过长只 warn | open | GUI 备份调用点见审计原文；超时分支 `Task.WhenAny(..., Task.Delay(10min))`（S34 同源） |
| D-24 | S21 | `FrpBypassResult.OverallSuccess` / `EnableDowngradeResult.OverallSuccess` 仍是「任一 step 成功即 true」的 OR 语义（当前无调用者，属死代码陷阱） | open | `Models/FrpBypassResult.cs:34`；`Models/EnableDowngradeResult.cs:37` |
| D-25 | S28 / S23 / S25 / S31 / S33 | 失败与「不支持」「超时」仍不可区分：`TryReadAsync` 三连 catch→null、诊断日志三层空 catch、`IsDeviceConnected` 吞异常返回 false、设置损坏静默改名 `.bad`、sysfs 读失败→null | open | `Cli/CommandSupport.cs:57-93`；`Utils/ProcessRunner.cs:221-264`；`Services/FastbootClient.cs:53-65`；`Services/UserSettings.cs:341-375`；`Utils/LinuxSerialPortFinder.cs:144-158` |
| D-26 | H93 / H09 | 需要拔插线/需要关写保护的 SoC 名单仍有 4 份拷贝 | open | `Services/FirmwareUnlocker.cs:118-141`；`MainWindow.axaml.cs:2491-2492,2521-2524`；`Services/VcomFlasher.cs:213-247` |
| D-27 | H49 / H74 / S42 / S43 | 知识重复：`OEM_INFO`/0x400 版本探测、UPDATE.APP 别名表、magic 扫描各有第二份实现 | open | `Cli/Commands/WriteCommands.cs:711-722`；`Cli/Commands/OtaFlashRunner.cs:51-59` vs `Utils/UpdateApp.cs:229-246` |

### 2.3 已声明 / 有意不修（wontfix）

| 编号 | 摘要 | 状态 | 理由 |
| :--- | :--- | :--- | :--- |
| BB-12 / BB-35 | About 页三个外链用 `Process.Start(UseShellExecute=true)` 打开浏览器，无服务抽象、无 CLI 面 | wontfix | 打开外部链接不是设备写，也不该暴露成 CLI/MCP 能力（会把「上网」变成 agent 可发起动作）。代码位置 `MainWindow.axaml.cs:2864,2870,2875` |
| BB-33 | 进度框「关闭=取消」语义、关窗后不保留结果 | wontfix | 结论现在同时写 JSONL 操作日志与会话日志（`Services/Diagnostics/OperationLogger.cs`、`Services/VcomSessionLogger.cs`），UI 不再是唯一落点 |
| H12 / H18 / H28 / H70 / H75 / H76 / H77 / H78 / H79 / H84 / H85 / H87 / H88 | 协议固定值（VID/PID、START 帧、命令字、UPDATE.APP magic、sparse magic、USB Update 端口/帧/CRC）不外置 | wontfix | 外置协议常量只会把「配错就变砖」从代码问题变成配置问题；审计原文也把这些标为 `可外置=N` |
| `gui-ui-only.txt` 里的 17 个 handler（导航/文件对话框/勾选/剪贴板/外链） | 纯 UI 管道，无设备副作用 | wontfix | 已由 `tests/SurfaceParity`（门禁 E/E3）逐个要求「必须存在 + 必须写理由」；清单 `tests/SurfaceParity/gui-ui-only.txt` |

---

## 3. 结构性防线（让「新债」不能悄悄出现）

| 防线 | 挡什么 | 位置 / 门禁名 |
| :--- | :--- | :--- |
| 单一能力注册表 | 某个能力「只在 GUI 能写」「声明只读实际写」「参数不在注册表」 | `Services/Capabilities/CapabilityRegistry.cs`；`tests/SurfaceParity`（A/A3/B/C/C2/E/E3/G） |
| GUI-only 白名单 | GUI handler 悄悄新增却没登记 | `tests/SurfaceParity/gui-ui-only.txt`（每行必须有理由，且不得与注册表重复） |
| 审批闸门 + 写票据 | 新写路径绕过批准（`DeviceWriteOps` 方法第一参数必须是 `WriteTicket`，只有闸门能铸） | `Services/Approval/*`；`Services/WriteOperations/DeviceWriteOps.cs`；测试 `tests/ApprovalGate`；设计 [approval-gate.md](approval-gate.md) |
| 审批审计 | 谁批了/谁拒了 | `~/.local/state/kirin-tool/approvals.jsonl`（`Services/Approval/ApprovalAudit.cs`） |
| 操作日志 | 每次操作一个 JSONL（argv 打码、exit code、时长、verdictSource） | `Services/Diagnostics/OperationLogger.cs`；测试 `tests/OpLogging`；字段表 [op-logging.md](op-logging.md) |
| VCOM 会话日志 + ACK 账本 | 「上传完成」不再等于「设备确认」 | `Services/VcomSessionLogger.cs`、`Services/Verdict/VcomAckLedger.cs`；测试 `tests/VerdictHonesty` |
| 判定辅助件集中 | 结论规则散落各处 → 口径不一 | `Services/Verdict/{BackupGate,NveWriteVerdict,LoaderPrecheck,UnlockVerdictText}.cs` |
| 测试清单唯一来源 | 某个 pipeline 不跑的测试会烂掉 | `tests/suites.txt`（22 个套件，两处 CI 都跑） |
| 本文件 | 审计项状态没处可查 | `docs/debt-register.md`（发布前必须复核 TOP3） |

---

## 4. 变更记录方法（改代码的人怎么更新这份表）

1. **修掉一条**：把该行状态改成 `fixed-in-<版本>`，证据列必须是三者之一：`文件:行`、提交号、门禁/测试名。
   同时在该版本的 `docs/release-notes/vX.Y.Z.md` 写一句，并在 `docs/refresh-changelog.md` 该版本段指出本文件。
2. **新发现一条债**：追加一行，状态 `open`，**必须**写「误判后果」（让人以为成功？让人多写一次？）。
   能挂门禁就挂（`tests/SurfaceParity` 行、`gui-ui-only.txt` 理由行、或新的 hardware-free 测试）。
3. **决定不修**：状态 `wontfix` + 理由（为什么不是风险 / 为什么不值得）+ 谁决定的；不接受「以后再说」。
4. **没核实**：状态 `unknown` + 「需要什么证据」（例如「需要真机 VCOM 抓帧」「需要跑 `tests/SurfaceParity` 看计数」）。
   不允许凭印象改状态。
5. **排序**：新 open 项插到正确的小节，小节内按危险度（误判成功 > 误操作 > 可移植性 > 可读性）插位，不要追加到末尾。
6. **发布前复核**：`rg -n '\| open \|' docs/debt-register.md`，确认 TOP3 仍成立或已改状态；
   `docs/INDEX.md` 指向本文件的那一行不要漏。

---

## 附录 A —— audit B「B1 类 13 条」逐条现状

B1 = 副作用在 CLI/MCP 完全不可达。E 之后写能力三面齐备，但仍需回答「还剩几条」。

| 编号 | 动作 | 现状 | 证据 |
| :--- | :--- | :--- | :--- |
| BB-01 | VCOM 解锁 loader 上传 | fixed | 见 §1 |
| BB-02 | 解锁 bootloader | fixed | 见 §1 |
| BB-03 | Remove FRP | fixed | 见 §1 |
| BB-04 | Enable Downgrade（4×`oem oeminfoerase-*ver`） | fixed | 拆成 `downgrade-enable`（PlanOnly）+ `downgrade-erase`（Write，过闸门），见 D-13 |
| BB-05 | OEMInfo 直写（`oeminfoerase-disablewp` + `oeminfowrite-*`） | **open（最高危）** | D-01 |
| BB-06 | 写 NVE SN | fixed | 见 §1 |
| BB-07 | Reboot to USB Update（救援分区） | fixed | 见 §1 |
| BB-08 | Software Testpoint Enter/Exit | fixed | 见 §1 |
| BB-09 | USB Update 模式刷整包 | **open** | D-12 |
| BB-10 | `fastboot reboot` | fixed | 见 §1 |
| BB-11 | `oem` 类读（`lock-state info`/`oeminforead-*`/`get-build-number`） | **部分 open** | `psid` 有面（`Cli/Commands/InspectCommands.cs:87`）；其余仍只有 GUI |
| BB-12 | About 三外链 | wontfix | §2.3 |
| BB-13 | 写 `./dload`（先递归删除） | **部分 open** | CLI 侧已改临时目录；GUI 仍 `"dload"`（D-14） |

**结论：13 条里 7 条已关闭，5 条 open（其中 1 条最高危 = BB-05），1 条 wontfix。**

### 「仍有副作用的只读/UI 项」清单（本次明确保留的）

这些**应当**保留为 UI/只读动作，不需要 CLI 面；列出来是为了下一个人不要把它们误当债务去「补能力」：

| GUI 动作 | 副作用 | 是否需要 CLI 面 | 依据 |
| :--- | :--- | :--- | :--- |
| Browse / Clear / Edit 分区选择（6+2 个按钮） | 写 `settings.json`（本机配置，不碰设备） | 否 | `gui-ui-only.txt` 逐行理由；`Services/UserSettings.cs` |
| Welcome▸udev Copy commands | 写剪贴板 | 否（已有只读 `udev` 命令） | `MainWindow.UdevNotice.cs`；注册表 `:72` |
| 主题/最后选择的 CPU、路径记忆 | 写 `settings.json` | 否 | v2.4.10 的 QoL |
| About▸Guide/Telegram/GitHub | 起浏览器进程 | 否（wontfix） | §2.3 |
| Extractor▸勾选/取消 | 本地勾选 + CTS | 否（`extract` 已覆盖真正解包） | 注册表 `:73` |
| 进度框/提示框本身 | 无 | 否 | 结论已落 JSONL |

---

## 附录 B —— 审计 TOP15 核对（§1/§2 的展开索引）

| 审计序 | 条目 | 状态 | 本文件条目 |
| :--- | :--- | :--- | :--- |
| 1 | S01/S02 ACK 校验被注释 | fixed | §1 |
| 2 | H20/H23 pwn 入口与返回地址 | open | D-06 |
| 3 | F01 "Unlock Complete" | fixed | §1 |
| 4 | F02/S15 `Skipped` + 继续 | fixed | §1 |
| 5 | F10/S06/S07 USB Update 假成功 | fixed | D-04 |
| 6 | S10/S11 `--require-backup` 静默失效 | fixed | §1 |
| 7 | S12 ArgParser 宽松 | fixed | §1 |
| 8 | F04/S18 写 SN 只看退出码 | GUI fixed / CLI open | §1 + D-03 |
| 9 | F03/S17 跳过计成功 | GUI open / CLI 部分 | D-05 |
| 10 | F07 `applied:true` 在 0/N 时也出 | open（范围更大） | D-02 |
| 11 | S03/S04/S05 握手与 reboot 返回值丢弃 | fixed | D-04 |
| 12 | P11/P12/P14/P13 fastboot 血统 | open | D-07 |
| 13 | F09/S22 dump 只判 >0 字节 | open | D-09 |
| 14 | S19 `oeminfoerase-disablewp` 结果丢弃 | open | D-01 |
| 15 | F05 oeminfo 命中 0 条仍成功 | open | D-11 |

---

## 附录 C —— 平台假设（P 类）里会**导致误判**的条目

只挑「会让用户/AI 把环境问题当成设备问题、或把成功当成失败」的；其余 P 项属可移植性问题，未在此展开。

| 编号 | 假设 | 会造成的误判 | 状态 |
| :--- | :--- | :--- | :--- |
| P11 | 无随包 fastboot 时静默回落系统 fastboot | Google platform-tools 的 `getvar` 对华为临时 loader 全失败 → 被读成「设备没连/线坏」 | open（D-07） |
| P12 | CLI 候选链含 5 级相对回溯 + `/usr/bin` | 同上；且 `doctor` 不区分血统 | open（D-07） |
| P13 | `doctor.ok = 找到 && 能跑` | `ok:true` 被当成「环境健康」 | open（D-08） |
| P14 | 随包 fastboot 需联网构建；仓库里只有 `fastboot/payload` | 未跑构建时必然落到 P11 | open |
| P15 | 打包产物假定含 `loaders/`，`publish.sh` 不产出 | VCOM 解锁报「loader 目录不存在」→ 被读成本机权限问题 | open（D-20） |
| P01 | Windows 用 WMI `Caption LIKE '%(COM%'` + `Substring` 解析 | 设备名不含 `(COMx)` 时静默 null → 报「没有 VCOM 设备」而不是「端口名解析失败」 | open |
| P02 | Linux 按驱动名 `option` 决定关 RTS/DTR | 驱动换成 `cdc_acm`/`usbserial` 时表现为「连上但一发就断」 | open（D-18） |
| P05 / P06 | 只认 `/dev/ttyUSB*`、`ttyACM*`、`ttyGS*`，接口匹配只认 `ff/13/23` | 描述符一变 → 「未找到端口」，被读成设备没进模式 | open |
| P08 | 静态读 udev 文本判「权限已覆盖」 | `TAG+="uaccess"` 在 SSH/无桌面会话无效 → 报「规则齐全」但 `open` 失败 | open（D-22） |
| P21 | MCP 协议版本固定 | 新客户端协商行为未定义 → 工具「没出现」被读成服务没起 | open（D-21） |
| P27 | 依赖英文前缀 `(bootloader)`/`OKAY`/`FAILED` | loader 改语言/加前缀 → 结论退化 Unknown，看起来像「设备异常」 | open（D-10） |
| P04 | Linux 与 Windows 是同一协议的两套传输实现 | Linux 侧行为差异无法用 Windows 结果背书 | open（无对照测试） |

---

## 附录 D —— README 与代码的清单差异（本次已修）

核对方法：`rg -n 'Name =>|McpName =>' Cli/Commands/*.cs`（21 条 CLI / 21 条 MCP）与
`Services/Capabilities/CapabilityRegistry.cs`（22 条能力）逐项对照两份 README 的命令/工具清单。

| 文件 | 差异 | 处理 |
| :--- | :--- | :--- |
| `README.md` | 「Safety model」只写 `flash-ota`、`flash-xml` 是写 — 漏 6 条写能力 | 已补齐为 8 条三面写能力 + 1 条仅写 RAM |
| `README.md` | 「Tools (15, …)」— 实为 21；把 `vcom_unlock` 归入 plan-only、漏 6 个写工具 | 已改为 21，并按只读 10 / 真实写 9 / 仅规划 2 分组 |
| `README.zh-CN.md` | MCP 表把 `vcom_unlock` 归入「仅规划（3）」，与注册表 `Write` 矛盾 | 已改为真实写入（9）/ 仅规划（2），总数 10+9+2=21 |
| 两份 README | 命令清单与代码一致（`partitions/psid/capabilities/udev` 均已列出） | 无需改 |

---

## 附录 E —— 未逐条复核的审计项（诚实声明）

本文件逐条给证据的是：**B1 全 13 条、审计 TOP15、P 类中会导致误判的 12 条**，以及
`S/F/H` 里被点名要求核对的那几条。审计报告里其余条目（H 系列多数「可外置」策略常量、S 系列多数
`catch` 语义、F 系列少数反例项、P 系列可移植性项）**本次没有逐行复核**，状态一律视为 `unknown`
（默认按 still-open 对待，不得当成已修）。

补齐它们需要：读对应 `文件:行` + 在 `tests/` 里找到可复现的门禁，或提供一次真机/单元证据。
不要凭「v2.4.14 讲过三面一致」就假设这些已被顺手修掉。

## 统计（v2.4.14 基线）

- 已关闭：16 条（B1 类 7 条、TOP15 中 6 条、其余为 S/F/H/P 单点）。
- 仍 open：27 条（D-01…D-27），TOP3 = D-01（GUI OEMInfo 直写且绕闸门）/ D-02（`applied:true` 恒真）/ D-03（CLI 写 SN 无读回）。
- wontfix：4 组（B 类 2 条 + 协议常量 13 条 + 纯 UI handler 17 个）。
- unknown：审计报告中未逐行复核的剩余条目（见附录 E），数量取决于复核粒度，不在此虚报。
