# USB Update 通道的三态结论（审计 D-04）

本条 lane 只做一件事：让 USB Update 通道**不再可能静默地报成功**。
结论从 `bool` 换成三态 `verified / partial / failed`，每一种降级都变成显式事件
（`degraded:` / `skipped:` / `missing:` 前缀 + 结构化字段），并且同时喂给 OpLog、GUI 与 CLI。

- 基线：`a62fb6c`（integration2），分支 `feat/usb-verdict`，版本 v2.4.15
- 债务编号：D-04（审计 F10 / S03 / S04 / S06 / S07 / S09 / F13）
- 边界：**不碰 USB 协议时序**（命令字节、延时、重试次数、帧布局全部保持基线）

## 1. 现状（D-04 的五处，行号取基线 `a62fb6c`）

`Services/USBUpdate/UsbDownloadService.cs`

| # | 位置 | 原文行为 | 后果 |
|---|---|---|---|
| 1 | `:211`（`DoHandshake` 末尾） | 3 次握手都没看到 `7E 26 00 00 25 A7` 前缀，只 `_log("Expected handshake response not observed; continuing the existing protocol sequence.")` 然后继续 | 握手状态无人知道，后面所有「成功」都建立在一个未确认的链路上 |
| 2 | `:269` / `:278` | `partition_mapping.txt` 指向的 `.img` 不存在或 0 字节 → `continue` | 该分区一片没写，**不计失败** |
| 3 | `:295` / `:311` / `:317` | `dload` 下的 `.img` 缺失 / `.img.header` 缺失 / `.img` 0 字节 → `continue` | 同上 |
| 4 | `:406`（`SendImage`） | `if (bytesRead == 0) break;` 之后 `return null`（`null` = 无错误 = 成功） | 提前读到 EOF 也算这个分区写成功；`FlashPartitions` 可以返回 `true` 而设备一片没写 |
| 5 | `:99-110`（`FlashImages`） | `if (success) { 重启 }` `else { }` —— 空分支 | 失败被吞：既不记事件，也不改变结论，函数仍返回调用者以为的「结论」 |
| + | `:216`（`SendUnlockCommand`） | `if (!SendCommand(...)) {}` 空 if 体 | 同 5，属同类静默 |

上级调用者（`Services/USBUpdate/USBUpdateFlasherService.cs:358`、`SoftwareTestpointService.cs:96`、
`MainWindow.axaml.cs:1103`）只看这个 bool：于是 GUI 在「设备一片没写」时照样
`SetOverallComplete(true, "USB Update flash completed successfully!")`。

（本节 DONE-1）

## 2. 三态模型

```
verified  每个必需分区都走了 head(data)+tail 且设备对每个命令都回了预期应答；
          握手必须建立（HandshakeEstablished = true）。少一个条件就不是 verified。
partial   至少一个必需分区写完，但不是全部/或握手未建立/或传输中途硬失败；
          也可能出现「全部必需分区都写了但握手没建立」——最多 partial。
failed    一个必需分区都没写（含握手未建立 + 零写入、list.txt 缺失、零分区计划）。
```

判定顺序（`UsbUpdateVerdictBuilder.Build`）：

1. 硬失败（list.txt 缺失、某分区中途抛错）⇒ 上限 `partial`；
2. 一个必需分区都没写 ⇒ `failed`；
3. 全部必需分区写了 **且** `HandshakeEstablished` ⇒ `verified`；
4. 其余 ⇒ `partial`（原因里指名握手/跳过/缺失/早退）。

**必需分区集合不硬编码**：`FlashPartitions`/list.txt 里的选中集合（`flag == 1`）就是必需集合；
调用方可以显式传入（`FlashImagesWithVerdict(requiredPartitions: ...)`）。显式集合优先。
GUI 传的是用户在包选择器里勾中的分区集合（`filesData[].SelectedPartitions` 的并集）。

（本节 DONE-2：`Services/USBUpdate/UsbUpdateVerdict.cs` 的 `UsbUpdateOutcome` /
`UsbUpdateVerdictBuilder.Build`）

## 3. 事件与字段

`UsbUpdateEvent`（`Services/USBUpdate/UsbUpdateVerdict.cs`）是唯一的事件单位，
`Token` 直接就是操作日志前缀，`Line` 是人读行，`Fields` 是结构化字段：

| `Kind` | `Token`（日志前缀） | 何时产生 | 对结论的影响 |
|---|---|---|---|
| `HandshakeMissing` | `degraded:` | 3 次握手都没看到 `7E 26 00 00 25 A7` | `HandshakeEstablished=false`，结论上限 `partial` |
| `AuthorizationMissing` | `degraded:` | 包内有 `unlockcode` 但授权命令没得到预期应答 | 只记事件 |
| `Written` | `ok:` | head+data+tail 全部被设备应答 | 计入 `Written` |
| `Skipped` | `skipped:` | 缺 `.img` / 缺 `.img.header` / list.txt `flag != 1`（未选中） | 进入 `Skipped[]`；若是必需分区则结论上限 `partial` |
| `Missing` | `missing:` | `.img` 0 字节 / 映射源 0 字节 | 进入 `Missing[]`；若是必需分区则结论上限 `partial` |
| `StreamEndedEarly` | `degraded:` | 读到的字节数小于文件声明大小（原 `break` + 成功） | 进入 `Missing[]`，并终止该分区 |
| `TransferStopped` | `degraded:` | 设备拒绝 head/data/tail 之一，传输在该分区终止 | 进入 `Missing[]`，结论上限 `partial` |
| `Degraded` | `degraded:` | reboot 未应答、list.txt 行畸形、硬停后不发 reboot | 只记事件 |

`Fields` 的三个键：`event`（`Kind` 小写）、`partition`、`required`。
事件同时：①进 `_log`（人读行）②进 `UsbUpdateVerdict.Events` ③送到上层回调
（`FlashImagesWithVerdict(onEvent:)` → flasher 落成带字段的 `op.Step`）。

`UsbUpdateVerdict` 携带：`Outcome` / `RequiredPartitions` / `Written` / `Skipped[]` /
`Missing[]` / `HandshakeEstablished` / `Reason` / `Events[]`，`Token` 是机器词
（`verified`/`partial`/`failed`），`Describe()` 给 GUI/CLI 用，形如：

```
usb-update partial: required=1/2, written=1, skipped=1, missing=0, events=4, handshake=established — 1/2 required partition(s) written; 1 skipped
```

（本节 DONE-3：`UsbDownloadService.FlashImagesWithVerdict` 填满这些事件）

## 4. 上层对接

| 层 | 改动 |
|---|---|
| `UsbDownloadService` | `FlashImagesWithVerdict(requiredPartitions, onEvent) -> UsbUpdateVerdict` 是新入口；旧的 `bool FlashImages()` 保留为 **`Verified` 的别名**（不再可能是「设备一片没写却 true」）。`LastVerdict` 供只有 bool 的调用方追查 |
| `USBUpdateFlasherService.FlashPartitions` | 返回类型 `Task` → `Task<UsbUpdateVerdict>`；必需集合 = 调用方 `filesData[].SelectedPartitions` 并集（空 = list.txt 选中项）；每个 `UsbUpdateEvent` 落成一条带字段的 `op.Step`（`step=usb-update.event`）；结尾 `op.Result(ok: verdict.Verified)` + `verdictSource=usb-verdict:<token>:required=n/m` |
| GUI（`MainWindow.axaml.cs` USB Update 段） | 读返回值分三态：`verified` 才 `SetOverallComplete(true, …)`；`partial`/`failed` 都是 `false` 并把 `Describe()` 原文显示 |
| CLI（`usb-testpoint`） | `SoftwareTestpointService.LastVerdict` + `WriteReports` 映射；JSON 里带 `verdict/required/written/skipped/missing/handshakeEstablished/reason` |
| `WriteReport` 映射 | `verified → Applied(exit 0)`；`partial → Partial(exit 3)`；`failed → Failed(exit 3)` |

**GUI 的整包 USB Update 刷新仍无 CLI/MCP 面**（那是 D-12，不在本条 lane 内），所以 CLI 侧的三态
证据只在 `usb-testpoint`（XLOADER 传输）这条通道上：它现在也只把 `verified` 记成 `applied`。

`UsbDownloadService` 不再在「分区 head 命令被拒」时把异常抛出传输层（旧行为让调用方永远拿不到
结论）；它按 data/tail 阶段的做法停下并返回 `failed`/`partial` 结论，异常文本原样进 `Reason`。
`SoftwareTestpointService` 仍然在非 `verified` 时抛 `IOException`（对它的调用者，「XLOADER 没写上」
就是失败），但异常消息里带 `Describe()` 全文。

（本节 DONE-4）

## 5. 测试（离线，无真机）

新增 `tests/UsbUpdateVerdict`（已进 `tests/suites.txt`，两条 CI 流水线都会跑）。
「设备」是 `FakeTransport`（实现 `IUpdateTransport`）：它按命令首字节识别握手（`0x26`）与普通命令，
分别回 `7E 26 00 00 25 A7 …` 与 `7E 02 6A D3 7E`；`AnswerHandshake=false` 模拟静默链路。
镜像/头文件写在临时目录，内核之外不碰任何设备节点。

| 组 | 用例 | 断言 |
|---|---|---|
| 1 | 握手无应答 + 一条命令也没应答 | `failed`、`HandshakeEstablished=false`、`Written` 空、`degraded:` 行 + `HandshakeMissing` 事件、**仍是 3 次握手**（协议未变） |
| 2 | 握手无应答但分区写成功 | `partial`，且**永不** `verified` |
| 3 | 必需分区缺 `.img` | `partial`、`Skipped` 里 `Required=true`、`skipped:` 事件、该分区**一条 head 命令都没发** |
| 3b/3c | 缺 `.img.header` / 0 字节镜像 | 前者 `skipped`，后者 `missing` + `missing:` 事件 |
| 4 | 镜像流提前结束（`OpenImageStream` 注入空流） | `partial`、进入 `Missing`、`StreamEndedEarly` 事件 + `degraded:` 行（旧行为是成功） |
| 5 | 全部必需分区都被应答 | `verified`、`RequiredWrittenCount=2`、每个分区一条 `ok:` 事件、事件都带结构化字段 |
| 6 | `Describe()` | partial 文本含 `required=/written=/skipped=/missing=/handshake=` 全部计数；failed 文本含 `handshake=not-established` |
| 7 | 旧 `bool FlashImages()` | partial ⇒ `false`；verified ⇒ `true` |
| 7b | 设备拒绝第一条 head 命令 | 返回 `failed` 结论而不是把异常抛出传输层 |
| 8 | `UsbUpdateVerdictBuilder` 规则本身 | 必需分区全没写 ⇒ `failed`；`NoteFatal` 压过已写分区；空必需集合 = 「计划决定」；未选中分区是 `Required=false` 的 skip；`flag != 1` 是声明式 skip；缺 `list.txt` ⇒ `failed` 且带原因 |

`FakeTransport` 与真实传输的差异只在于「应答从哪来」，命令字节、帧间隔（50/200 ms）、
重试次数（3）都由生产代码原样产生，因此「协议未变」这条也有断言（第 1 组第 6 条）。

（本节 DONE-5）

## 6. 风险与回滚

| 风险 | 说明 | 处置 |
|---|---|---|
| 语义变严导致既有「成功」变成 `partial` | 以前只要没抛异常、没在分区循环里 return false，就是成功；现在握手未确认或有必需分区被跳过就是 `partial` | 这正是本条 lane 的目的；`docs/debt-register.md` D-04 记录该语义变化，GUI 文案从 "completed successfully" 改为三态原样 |
| `FlashPartitions` 返回类型从 `Task` 变成 `Task<UsbUpdateVerdict>` | 忽略返回值的调用方会静默丢掉结论 | 唯一调用点（`MainWindow.axaml.cs` USB Update 段）已按三态改写；`FlashImages()` 保留为 `Verified` 别名以免别的调用点被无意放宽 |
| head 命令被拒不再抛异常 | 旧行为让调用方拿不到结论（直接进 GUI 的 catch） | 现在按 data/tail 阶段的做法停下并返回结论；异常文本进 `Reason`/`LastError`，`SoftwareTestpointService` 仍对非 `verified` 抛 `IOException`（消息里有 `Describe()`） |
| 回滚 | 单 lane、单分支 | `git revert` 本 lane 的提交即可；没有协议/时序改动，回滚不需要动设备侧任何东西 |

**明确未做**：整包 USB Update 的 CLI/MCP 面（D-12）、进度条按分区计数（D-05，GUI 另一条路径）、
`FlashPartitions` 之前的分区发现/红线闸门（未改）。

（本节 DONE-6）

## 7. 冻结边界（StableParity boundary.json）

`tests/StableParity/boundary.json` 冻结了 13 个协议/设备路径文件。本条 lane 改动了其中 **3 个**，
`reference` 字段逐条追加了理由（`tests/StableParity` 本身没有放宽：仍是逐文件 sha256 比对，35 checks）：

| 文件 | 改动性质 |
|---|---|
| `Services/USBUpdate/UsbDownloadService.cs` | 结论与事件：`DoHandshake` 返回 bool、`SendUnlockCommand` 返回 bool、跳过分区进 `Skipped/Missing`、`bytesRead==0` 变成错误、空 if/else 变事件。**命令字节、CRC、延时、重试次数、块大小全部未变** |
| `Services/USBUpdate/SoftwareTestpointService.cs` | 暴露 `LastVerdict`，只有 `verified` 算成功；命令未变 |
| `MainWindow.axaml.cs` | USB Update 收尾改为三态显示；设备命令/时序/重试未变 |

新增文件 `Services/USBUpdate/UsbUpdateVerdict.cs` 不在冻结表内（纯结论层，无设备访问）。

```
结论模型变化、协议未变。
```

另：`tests/UsbDiagnostics`（既有套件）通过反射调用 `FlashPartitions(port)`，并断言「header 被拒 ⇒
抛异常」。签名与结论模型变了，该套件同步改为传 `UsbUpdateVerdictBuilder` 且断言「停传输 + 结论 +
原始设备应答文本、不再抛出」，其余断言（命令字节数、offset、超时恢复、线字节不变）原样保留。

（本节 DONE-7）

## 8. 结果

| 项 | 结果 |
|---|---|
| `dotnet build -c Release`（`Kirin-Tool.csproj` + `KirinTool.Cli`） | 0 error |
| `tests/suites.txt`（24 套，含新增 `UsbUpdateVerdict`） | **24/24 绿**（`FAILURES=0`） |
| 新增套件 | `tests/UsbUpdateVerdict`：39 checks（对应 §5 的 1/2/3/3b/3c/4/5/6/7/7b/8/8b/8c） |
| 被结论变化影响而更新的既有套件 | `tests/UsbDiagnostics`（49 checks，反射签名 + header 不再抛异常）、`tests/StableParity`（3 个冻结哈希刷新，35 checks 全绿） |
| 真机 | **未触碰**：无 adb / 无 fastboot / 无 `/dev/ttyUSB*`；传输是 `FakeTransport`，镜像是临时文件 |

（本节 DONE-8）
