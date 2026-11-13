# vcom unlock：GUI / CLI / MCP 行为对齐

状态：**已落地**（分支 `feat/cli-vcom-unlock`，`dotnet build` 0 error、18 套 harness 全绿）。本文是
GUI↔CLI 的唯一对齐依据：每一行都要能追到代码位置，改行为时先改本文的对齐表。

## 1. 现状差异（DONE-1）

| 维度 | GUI（真实实现） | CLI（改造前） |
| :--- | :--- | :--- |
| 入口 | `MainWindow.axaml.cs:HandleUnlockFastboot()` → `Services/FirmwareUnlocker.UnlockFastboot(...)` | `Cli/Commands/WriteCommands.cs` 的 `VcomUnlockCommand : GatedWriteCommand` |
| 设备操作 | 真跑 `VcomFlasher`：START→null→拔插重连→XLOADER(pwn)→UCE→FASTBOOT→BL2→结尾拔插提示 | **零设备操作**；`details.implementation` 写着 `(not invoked in this build)` |
| 退出码 | 无（弹窗 `Unlock Complete/Unlock Failed`） | 恒 `4`（`ExitCodes.Gated`），确认与未确认都一样 |
| ACK 语义 | `VcomFlasher.WriteAndVerify` 里 ACK 校验**被注释掉**：读 1 字节、最多等 10 s、值不校验、读不到也继续（=lenient） | 无法表达任何 ACK 策略 |
| 拔插 | `interactionHandler` 弹窗阻塞，确认后 Linux 侧 `ReconnectAfterCableAsync` 轮询 15 s 重连，**不重发 START** | 无 |
| loader 目录 | 固定 `AppContext.BaseDirectory/loaders/<cpu>` | 计划里打印同一路径，但不可改 |
| 进度 | `ProgressItemViewModel` 逐阶段百分比 | 无 |

差异结论：CLI 缺的不是"参数"，而是 **执行通道**（设备 I/O + ACK 策略 + 人工拔插点 +
阶段选择）。因此 CLI 不能另写一套协议，必须把 GUI 的 `FirmwareUnlocker`/`VcomFlasher`
参数化成可注入，再由 CLI 侧注入非交互实现。

## 2. 设计（DONE-2）

### 2.1 协议层参数化，不复制实现

- `Services/VcomFlasher.cs`
  - 新增 `enum VcomAckMode { Strict, Lenient, Off }` 与 `sealed class VcomFlasherOptions`
    （`PortName`、`BulkOnly`、`AckMode`、`AckTimeout`、`FrameObserver`）。
  - `WriteAndVerify` 按 `AckMode` 分三支：`Strict` 读不到或非 `0xAA` 抛 `IOException`；
    `Lenient` 读 1 字节、记录、不抛（**与 GUI 现状逐位一致**）；`Off` 不读（等价 GUI
    的盲发分支 `expectAck:false`）。
  - 帧计数器（`AckExpected/AckOk/AckNonAck/AckTimeout`）与 `FrameObserver`（每帧一条
    `VcomFrameInfo`）供 CLI 逐帧打到 stderr 并汇总进 JSON。
  - `PortName`/`BulkOnly` 仅在 CLI 显式给 `--port` 时使用；自动枚举 12d1:3609 与
    option 驱动判定保持原样（默认路径行为不变）。
- `Services/FirmwareUnlocker.cs`
  - 新增 `sealed class UnlockOptions`：`LoaderDirectory`、`Flasher`、`Stages`
    （`All|Null|Xloader|Rest`）、`StageObserver`、`ConnectRetryWindow`。
  - `UnlockFastboot(..., UnlockOptions? options = null)`：`options == null` 时**代码路径与
    GUI 现状完全一致**（GUI 调用点不改）。新增能力只在 options 非空时生效。
  - `Stages` 语义（分步执行，配合人工拔插）：
    | 取值 | START | null | XLOADER | UCE/FASTBOOT/BL2 | 拔插 |
    | :--- | :--- | :--- | :--- | :--- | :--- |
    | `all` | 发 | 传 | 传 | 传 | null 后重连 + 结尾提示（=GUI） |
    | `null` | 发 | 传 | — | — | 不做（留给下一次 `rest`） |
    | `xloader` | 发 | 传 | 传 | — | null 后重连 |
    | `rest` | **不发** | — | 传 | 传 | 端点已就绪，仅 `Connect`（可等 `ConnectRetryWindow`） |
  - `UnlockResult.FailureKind`（`None|NoDevice|DeviceRejected|LocalFile|Cancelled|Other`）
    让 CLI 把"设备侧失败"和"文件/校验失败"映射到不同退出码；GUI 忽略该字段。

### 2.2 CLI 命令（`vcom-unlock` 由 PlanOnly 升级为 RealWrite）

`Cli/Commands/VcomUnlockCommand.cs`（新文件，从 WriteCommands.cs 迁出）。

参数：

| 参数 | 语义 |
| :--- | :--- |
| `--cpu <hisi990|...>` | 必填，枚举来自 `FirmwareUnlocker.CpuAddresses` |
| `--yes` / MCP `confirm:true` | 门控必需；缺省仍是 plan-only（exit 4） |
| `--dry-run` | 即使 `--yes` 也只打印计划（exit 4），保持旧 CLI 输出字段 |
| `--ack-mode {strict|lenient|off}` | CLI/MCP 默认 `strict`（v2.4.15 起），GUI 仍是 `lenient`；三档含义见 2.1，分入口默认值见 [ack-default.md](ack-default.md) |
| `--ack-timeout <秒>` | 默认 10（=GUI 的 10 s），测试用小值 |
| `--replug {prompt|wait:<秒>|skip}` | 非交互默认 `skip`，并在 stderr 明示"设备需要人工拔插" |
| `--loader-dir <路径>` | 默认 `AppContext.BaseDirectory/loaders`，可指向任意目录 |
| `--stage-only {null|xloader|rest|all}` | 默认 `all`；`rest` 不重发 START |
| `--port <tty>` | 显式 tty（跳过 12d1:3609 枚举；隐含 bulk-only，无 modem 线信号），供受限环境与离线测试 |
| `--use-fastflash-loader` | 对应 GUI 的 `fastbootf.ktl` 开关 |

输出：进度/ACK 逐帧 → stderr；结束时 stdout 单行 JSON（`stages[]` 每阶段
`{stage,file,address,pwn,bytes,frames,acks,durationMs,status}` + `ackMode`、
`replug`、`stageSelection`、`port`、`loaderDirectory`、`ack` 汇总、
`elapsedMs`、`conclusion`、`gate`、`exitCode`）。非 `--json` 时同样在最后打印这段 JSON
（单行），人的可读输出去 stderr。

退出码（沿用 `Cli/ExitCodes.cs`）：

| 场景 | 码 |
| :--- | :--- |
| 全部阶段完成 | 0 |
| 用法错误（缺 --cpu、非法枚举、`--replug`/`--stage-only` 值错） | 1 |
| 无 VCOM 设备 / 端口打不开 / 拔插后 15 s 未重连 | 2 |
| 设备侧拒绝：strict ACK 超时或非 0xAA | 3 |
| 未确认、`--dry-run`、用户 `--replug prompt` 取消 | 4 |
| 本地文件问题：loader 目录不存在、选中阶段无任何 loader、DTL 解密失败 | 5 |

### 2.3 MCP 对齐

- 同名工具 `vcom_unlock`，`WriteKind = RealWrite` → schema 的 `required` 自动含
  `confirm: boolean`，缺省 `false` 时只返回计划（`gate=plan-only`, `isError=false`,
  `_meta.exitCode=4`）。
- `confirm:true` 才真正执行；执行结果同样带 `gate`（`applied`/`failed`）与
  `structuredContent.stages`，MCP 侧无需新代码。
- `--port`/`--loader-dir` 一并暴露（受限环境/自动化需要），`--replug` 在 MCP 里等价
  `wait:<秒>`（MCP 不可交互，命令实现把 `prompt` 降级为 `wait` 并在 stderr 记录）。

## 3. 与 GUI 的逐项对齐表（DONE-3，实测见第 6 节）

| # | GUI 行为（代码位置） | CLI 对应 | 判定方式 |
| :-- | :--- | :--- | :--- |
| A1 | `VcomFlasher.Connect()` 打开 12d1:3609，Linux option 驱动走 bulk-only（`Handshake.None`/`DtrEnable=false`） | 同函数；`--port` 时按 bulk-only 强制 | 同代码路径 |
| A2 | `SendStartFrame()`：写 14 字节 START（`FE 00 FF 01 ...`），`expectAck:false`，之后 `Task.Delay(50)` | 同 | 测试断言首帧字节 == GUI 常量 |
| A3 | 每阶段：head(14B) → n*data(0xDA, seq, ~seq, ≤0x400 payload) → tail(0xED, seq+1) 或 pwn 序列 | 同（`UploadData` 未改） | fake bootrom 记录帧序列 |
| A4 | pwn：XLOADER 阶段 `head(0x22000,4)` → `head(returnAddr,4)` → 读残余 → `data(1,addr)` → `tail(2)` | 同 | 同上 |
| A5 | ACK：读 1 字节、10 s、值不校验、读不到继续 | `--ack-mode lenient`（**GUI 默认**；CLI/MCP 需显式给） | 测试：无回包时 lenient 退出 0 但 `verified=false` |
| A6 | `--ack-mode strict`（CLI 新增能力，GUI 无） | 读不到/非 0xAA → 失败 exit 3；**CLI/MCP 默认** | 测试：默认（不传 `--ack-mode`）+ 静默 ⇒ exit 3、`verified=false`；回 0x55 同样失败 |
| A7 | `--ack-mode off`（CLI 新增能力） | 不读任何字节 | 测试：回包滞留不影响后续阶段 |
| A8 | null 阶段后：Delay 1 s → Linux 上 `Dispose()` → 提示拔插（电脑侧）→ `ReconnectAfterCableAsync` 15 s 轮询 → **不重发 START** | `--replug skip`（非交互默认）：不做拔插，直接提示"设备需要人工拔插"，由第二次 `--stage-only rest` 承担；`wait:<s>` 才轮询重连 | 测试：`rest` 首帧不是 START |
| A9 | 结尾（hisi980/810/820/985/990 且非 null 阶段）：提示两端拔线换普通线 | `all` 阶段末尾同样提示到 stderr（不阻塞） | 日志比对 |
| A10 | loader 文件缺失 → 该阶段 `Skipped`，流程继续 | 同（`File.Exists` 跳过）；但**选中阶段全部缺失**时 CLI 直接 exit 5（离线可判） | 测试：空 loader 目录 exit 5 |
| A11 | 进度：`ProgressItemViewModel{ProgressValue,StatusText}` | CLI 构造同结构但只用于内部计数，人读进度走 stderr | 代码复用 |
| A12 | cpu/地址表 | 共用 `FirmwareUnlocker.CpuAddresses`（未改一个字节） | 测试断言 hisi990 地址/参数 |

## 4. 测试计划（DONE-4）

### 4.1 新增 harness：`tests/VcomUnlockCli`（进双 CI，硬件无关）

假 BootROM 用 `posix_openpt` 造 pty（master 侧由 harness 扮演 12d1:3609），
CLI 用 `--port <pty slave>` 指过去。**不碰真机**：无 adb、无 fastboot、无 /dev/ttyUSB*、
无 12d1:3609 枚举（给了 `--port` 就跳过枚举）。

| 组 | 断言 |
| :-- | :--- |
| 1 参数/门控 | 缺 `--yes` → exit 4 / gate=plan-only / applied=false；`--yes --dry-run` → 仍 exit 4；缺 `--cpu`、非法 chip、非法 `--ack-mode`/`--stage-only`/`--replug` → exit 1；端口不存在 → exit 2 + `failureKind=no-device`；空 loader 目录 → exit 5 + `failureKind=local-file` |
| 2 ACK 三档 | **默认（不传 `--ack-mode`）+ 静默 → exit 3、`verified=false`、`ackMode=strict`、`failureKind=device-rejected`**；strict + 0xAA → exit 0 且 `ack.ok>0`；strict + 静默 → exit 3 且 `ack.timeouts>0`；strict + 0x55 → exit 3 且 `ack.bad>0`；显式 `lenient` + 静默 → **exit 0**（GUI 行为）但 `verified=false`/`gate=applied-unverified`/`reason=ack_missing:<n>`，timeouts 被记录；off + 静默 → exit 0、`ack.expected==0`、总耗时 < 5 s（不等待） |
| 3 分段 | `--stage-only null` 首帧必须是 START、只到 null；同一 pty 上再跑 `--stage-only rest`：**零 START 帧**、首帧是 XLOADER payload head(0x22000, len>4)、能到 UCE/FASTBOOT/BL2，JSON `stages` 只 4 条 |
| 4 全量顺序 | `--stage-only all --replug wait:1`：首帧 START 且字节 == GUI 常量；0x60000000/0x1A400000/0x1E400000 都出现；pwn 序列 head(0x22000,4)→head(0x673c8,4)→data(seq=1,4B)→tail(seq=2)；DATA 帧 > 4000；阶段顺序 null,XLOADER,UCE,FASTBOOT,BL2 |
| 5 拔插纪律 | hisi990 全量 + 默认 `--replug skip` → exit 4、gate=refused，理由里含 "replug" |
| 6 日志 | 日志目录 700；dry-run 也写出同格式头（event=plan）；行首毫秒 UTC + pid + seq + event；含 session.open/command.start/session.close/port.open(bulkOnly)/frame.write(START/HEAD+DATA 累积字节)/frame.ack(raw=0xAA)/stage.start/stage.end/unlock.done |

### 4.2 实测数字（本机，Debug 构建）

| 项 | 值 |
| :-- | :--- |
| `dotnet run --project tests/VcomUnlockCli` | **94 checks passed**，退出 0（v2.4.14 基线 84；v2.4.15 加默认 strict / 显式 lenient 两组断言） |
| 全量 5 阶段跑通耗时 | 18.3 s（5750 帧，含 5 次 500 ms 阶段延时 + null 1 s + replug wait:1） |
| DATA 帧数（hisi990 全量） | 5750 帧总计，其中 DATA > 4000 |
| `tests/WriteGates` | 24 checks passed（vcom-unlock 改用 `--dry-run` 断言门控，不再依赖"命令恒 exit 4"） |

## 5. 风险（DONE-5）

| 风险 | 影响 | 处置 |
| :-- | :-- | :--- |
| **CLI/MCP 默认值 lenient → strict（行为变更）** | 设备不回 ACK 的自动化调用过去 exit 0，现在 exit 3。门控/审批语义、帧序列与结论字段都没变 | 显式 `--ack-mode lenient` 恢复旧退出码（结论仍是 `verified=false`），或 `--ack-mode off`；理由与迁移见 [ack-default.md](ack-default.md) |
| **lenient 语义有一处 GUI 可见变化** | 旧代码在设备完全无 ACK 时 `ReadAsync` 超时会抛 `OperationCanceledException` → GUI 直接失败。现在 lenient 捕获超时并继续（"读不到也继续"，即注释掉校验的本来意图）。 | 想要旧的严格行为用 `--ack-mode strict`；GUI 若要恢复严格，把 `UnlockOptions.Flasher.AckMode` 设为 `Strict` 即可。已在 A5 记录 |
| `--port` 隐含 bulk-only | 显式端口一律不发 modem 线信号（DTR/RTS/流控），因为 pty/固定 tty 路径无法判定驱动 | 真实 option 驱动设备同理；Windows COM 口用户不要显式给 `--port` |
| `--replug skip` + hisi9xx 全量被拒（exit 4） | 非交互运行无法完成 null→XLOADER 的物理拔插 | 报错文案给出两条出路：`--replug wait:<秒>` / `--replug prompt`，或 `--stage-only null` + `--stage-only rest` |
| 显式 `--port` 的拔插无法用 VID/PID 判定 | `ReconnectAfterCableAsync` 在显式端口下只能重开同一路径（不核实真拔过） | 只影响显式端口；自动枚举路径仍是 15 s 轮询 12d1:3609 重连 |
| pty 假设备 ≠ 真 USB | termios/EPIPE、bulk 边界、真实时序都不覆盖 | 真机验收仍需 `docs/testing-checklist.md`；harness 只保证协议帧与策略 |
| 每帧一行日志的体积 | hisi990 全量约 1.15 万行 / ~1 MB | `--no-frame-log` 关 stderr 逐帧；日志文件本身不裁剪（审计用） |
| GUI 无自动化回归 | Avalonia UI 不在 CI | GUI 与 CLI 共用 `FirmwareUnlocker`/`VcomFlasher`，行为差异面被压到"注入参数"这一层 |

## 6. 对齐表实测回填（DONE-6）

| # | 对齐项 | 实测证据（tests/VcomUnlockCli） |
| :-- | :--- | :--- |
| A1 | 端口打开 / bulk-only | 日志 `event=port.open ... bulkOnly=true`（断言 6） |
| A2 | START 帧常量 | 断言 "START frame bytes are the GUI constant"（逐字节比对） |
| A3 | head→data→tail | 断言 HEAD/DATA/TAIL 帧型与 `DATA>4000`、每阶段 `bytes>0` |
| A4 | pwn 序列 | 断言 head(0x22000,4)、head(0x673c8,4)、data(seq=1,4B)、tail(seq=2) |
| A5 | lenient = GUI | 断言 lenient + 静默仍 exit 0 且 timeouts 记录 |
| A6 | strict 失败 | 断言 strict + 静默/0x55 → exit 3、failureKind=device-rejected |
| A7 | off 不等待 | 断言 `ack.expected==0` 且耗时 < 5 s |
| A8 | rest 不重发 START | 断言 rest 运行零 START 帧、首帧为 XLOADER head |
| A9 | 结尾换线提示 | 全量 run 的 stderr 含 GUI 原文（"unplug it from BOTH SIDES"） |
| A10 | 缺文件 | 断言空 loader 目录 → exit 5；单阶段缺文件仍记 skipped |
| A11 | 进度复用 | `ProgressItemViewModel` 由 CLI 构造并喂给同一服务（代码复用，无独立断言） |
| A12 | 地址表未改 | 断言 0x60000000/0x1A400000/0x1E400000/0x673c8 与 `CpuAddresses` 一致 |

## 7. 日志格式与字段表（DONE-7）

GUI 与 CLI 共用 `Services/VcomSessionLogger.cs`（无新依赖，只用 System.IO）。

- 路径：`$KIRIN_LOG_DIR`（若设置）否则 `~/.local/state/kirin-tool/`；目录不存在则创建，
  Unix 上 `chmod 700`（已存在且属他人时保持原样，只记 Debug 输出）。
- 文件名：`vcom-<UTC yyyyMMddTHHmmssZ>-<cpu>.log`，追加写、每行 `AutoFlush`。
- GUI：`ProgressDialog` 里显示 `log: <path>`（可选中复制），失败时该路径也会拼进状态行；
  文件在会话结束后保留，可事后 `cat`。
- CLI：stderr 首行打印 `log: <path>`；dry-run 也会写 `event=plan` 头。

行文法（一行一个事件，UTF-8，空格分隔，含空格的取值加双引号）：

```
<yyyy-MM-ddTHH:mm:ss.fffZ> pid=<pid> seq=<n> event=<name> [key=value ...]
```

| event | 出现时机 | 字段 |
| :--- | :--- | :--- |
| `session.open` | 建文件后 | `tool`, `version` |
| `command.start` | CLI 命令入口 | `command`, `cpu`, `json`, `yes` |
| `plan` | CLI 只打印计划（含 `--dry-run`） | `cpu`, `loaderDirectory`, `stages`, `ackMode`, `replug`, `stageCount`, `presentStages`, `dryRun`, `confirmed` |
| `run` | CLI 真正开始执行 | `cpu`, `loaderDirectory`, `stages`, `ackMode`, `replug`, `port`, `pid` |
| `unlock.start` | 服务入口 | `cpu`, `stages`, `loaderDirectory`, `useFastFlashLoader` |
| `port.open` | 串口打开成功 | `port`, `explicit`, `bulkOnly`, `handshake`, `ackMode`, `ackTimeoutMs` |
| `port.close` | `VcomFlasher.Dispose` | `port`, `framesSent`, `ackOk`, `ackNonAck`, `ackTimeouts` |
| `port.reconnect` | 拔插后重连开始 | `port`, `waitMs` |
| `frame.write` | 每帧写出 | `type`(START/HEAD/DATA/TAIL), `addr`, `len`(声明负载长度), `seq`, `bytes`(实际写字节), `cumulativeBytes`(DATA 累计), `expectAck` |
| `frame.ack` | 每帧等 ACK 结果 | `type`, `result`(ok/bad/timeout), `raw`(原始字节，如 0xAA), `waitedMs`(超时), `fatal`(strict 下是否致命) |
| `progress` | `overallProgress.Report` 每一条 | `text`(原文) |
| `stage.start` | 阶段开始 | `stage`, `file`, `addr`, `pwn` |
| `stage.end` | 阶段结束 | `stage`, `file`, `addr`, `status`, `bytes`, `frames`, `acks`, `durationMs`, `note` |
| `prompt` | interactionHandler 提示 | `text`(对话框/终端原文) |
| `prompt.answer` | 用户作答 | `accepted` |
| `unlock.done` | 结论 | `success`, `message`, `elapsedMs`, `failureKind`(失败时) |
| `error` | 异常 | `where`, `type`, `message`, `stack`(换行折成 ` | `) |
| `command.end` / `session.close` | 收尾 | 无 / 无 |

## 8. 冻结边界（StableParity boundary.json）刷新（DONE-8）

`tests/StableParity/boundary.json` 按 SHA256 冻结了协议/设备路径相关文件的"未经审阅不得改动"。
本次改动了其中 4 个文件的哈希，`reference` 字段逐条写明了原因：

| 文件 | 改动性质 |
| :--- | :--- |
| `Services/VcomFlasher.cs` | 可注入选项（ACL 策略/显式端口/帧观察者/日志）；**帧布局、CRC、地址、延时、重试时序未变** |
| `Services/FirmwareUnlocker.cs` | 增量 `UnlockOptions` + `FailureKind`；`options == null` 时 GUI 路径与旧实现一致，地址表未改 |
| `MainWindow.axaml.cs` | `HandleUnlockFastboot` 接入会话日志并把路径交给对话框；设备命令/时序未变 |
| `UI/ProgressDialog.axaml.cs` | 新增 `ShowLogPath`（可选中文本），状态与关闭行为未变 |

`tests/StableParity` 本身未放宽：仍是逐文件哈希比对（35 checks 全绿）。

## 9. 结论诚实化：`verified` / `--require-ack`（本 lane 增补）

§2 的 ACK 策略只规定了"读不读、fail 不 fail"。本 lane 把**读到的 ACK 变成结论**：

| JSON 字段 | 含义 |
| :--- | :--- |
| `applied` | 帧真的写出去了（与设备是否答话无关） |
| `verified` | 每个期待 ACK 的帧都收到 `0xAA` |
| `reason` | `ack_ok` / `ack_missing:<n>` / `ack_not_requested`（`--ack-mode off`）/ `missing_loader` / `replug_required` / `not_attempted` |
| `gate` | `applied`（已验证）/ `applied-unverified`（已上传未验证）/ `failed` / `refused` / `plan-only` |
| `conclusion` | `unlocked-fastboot-loaded` / `loaders-uploaded-unverified` / `failed` |
| `ack.{expected,ok,bad,timeouts,missing,missingRatio,acked,notExpected}` | 全局计数；`missingRatio = missing/expected`（分母不含本就无 ACK 的 START/TAIL/exploit HEAD） |
| `stages[].{framesSent,acks,missing,missingRatio}` | 同一套计数**按阶段**归属（靠 `UnlockOptions.StageBeginObserver`，不靠地址——hisi970..990 的 `null` 与 `XLOADER` 同址 0x22000） |

新增参数：

```bash
kirin-tool-cli vcom-unlock --cpu hisi990 --yes --require-ack      # 缺 ACK ⇒ exit 3, gate=failed
kirin-tool-cli vcom-unlock --cpu hisi990 --yes                    # 缺 ACK ⇒ exit 0, gate=applied-unverified
```

`--require-ack` **不改变线上行为**：flasher 仍是 lenient（继续记录、不中途抛），失败由结论层判定，
因此传输中断不会因为"要严格"而半途炸掉；`--ack-mode off` 与 `--require-ack` 组合是参数错误（exit 1）。

配套的服务侧改动（`Services/FirmwareUnlocker.cs`）：**发第一帧前**校验该 SoC 全部选中 loader 存在且
DTL 可解，缺一即 `UnlockFailureKind.MissingLoader`、`framesSent=0`、exit 5；进度状态里不再有
`Skipped`（缺失是失败，不是跳过）。

GUI 侧同一套判定：ACK 计数够 ⇒ 绿 "Upload Verified"；不够 ⇒ 琥珀 "Uploaded — Unverified" 并显示
`frames/ACKed/missing`；**不再**出现"只上传成功就弹 Unlock Complete"。GUI 的会话日志如今也含
`frame.write`/`frame.ack` 行（此前 GUI 传 `Flasher=null`，帧行缺失）。

（本节 DONE-9）

## 10. 未做（明确边界）

- GUI 侧没有自动化用例（Avalonia 不在 CI）；GUI 的日志接入靠共用服务 + `ProgressDialog` 显示路径。
- `--stage-only rest` 不校验"确实拔过线"（只保证不重发 START）。
- MCP 路径不给 `--replug prompt` 的交互能力：`prompt` 在 MCP 里按"stdin 非交互 → wait 15s"降级。
