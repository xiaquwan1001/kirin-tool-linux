# 统一操作日志（op-logging）

> 实现 lane D。基线 `35c6f6b`，分支 `feat/op-logging`。
> 上游审计：[`audit-gui-blackbox.md`](audit-gui-blackbox.md)（BB-02/BB-34）、
> [`audit-hardcoded-and-hidden-steps.md`](audit-hardcoded-and-hidden-steps.md)（H39/H40/H61/H62、S23/S24/S25/S26、P20/P25）。

## 1. 目标

设备侧写操作必须**可事后举证**：谁发起、调了哪个外部二进制、完整 argv（脱敏）、
退出码、耗时、输出尾部、以及「`ok` 到底是哪条输出/哪一行判出来的」。
当前状态（审计口径）：

| 现状 | 后果 |
|---|---|
| `Utils/ProcessRunner` 是唯一子进程出口，但只写 `kirintool_log_*.log` 纯文本，且仅当可执行名含 `fastboot` 才写（H61/S23） | adb/其它工具零日志；写入三层空 `catch`，日志没写成功也没人知道 |
| `Services/FastbootClient.UnlockBootloaderAsync` 用裸 `ProcessStartInfo` 直发 `oem unlock UUUU…`（H39） | 全仓唯一「无日志的设备写」（BB-34）；`ok` 只看退出码（H40） |
| stdout/stderr 混流成一个 `Output`（S24） | `Contains("OKAY")` 无法区分设备应答与错误文本 |
| GUI/CLI 各自在 handler 里拼 fastboot 参数 | 两条路径的命令集无法比对 |

本次目标（四件事）：

1. **结构化**：每个操作一份 JSONL 会话文件，字段固定、可 `jq` 直接消费。
2. **脱敏**：argv 落盘前按规则掩码（密钥类选项、解锁码等已知 secret）。
3. **唯一出口**：`Process.Start` 只允许出现在 `Utils/ProcessRunner`；需要流式读输出的
   （解锁）下沉为 `ProcessRunner` 的一个重载，复用同一份日志。
4. **判据外显**：`ok` 旁边必须写 `verdictSource`（`exit-code`、`arg-validation`、`timeout`、
   输出关键字命中等），杜绝「只有布尔值没有依据」。

不做：不改设备侧协议、不改现有 console/GUI 输出文案、不引入第三方日志库。

## 2. 日志字段表

**位置**：`${KIRIN_LOG_DIR:-~/.local/state/kirin-tool}/<category>/<UTC时间戳>-<pid>.jsonl`

- 目录 `0700`，文件 `0600`（`FileStream` 以 `UnixFileMode` 创建；Windows 走 ACL 默认）。
- `category`：`vcom` | `fastboot` | `flash`；其它工具（`adb`/`internal`）按工具名建同级子目录。
- **一个操作一个文件**（不是一天一个），同一毫秒同 pid 重名时追加 `-2`、`-3` 后缀。
- 关闭开关：`KIRIN_LOG_DISABLE=1`（测试与 CI 用）。

一行 = 一个 JSON 对象（无换行符内嵌，序列化后 `\n` 收尾）：

| 字段 | 类型 | 何时有值 | 说明 |
|---|---|---|---|
| `ts` | string | 每行 | ISO8601 UTC 带毫秒，如 `2026-11-13T09:12:33.481Z` |
| `pid` | int | 每行 | 进程号（JSONL 会话文件名里也有） |
| `op` | string | 每行 | 操作名，点分层级：`fastboot.unlock`、`fastboot.flash`、`vcom.session`、`flash.ota` |
| `phase` | string | 每行 | `start` / `step` / `prompt` / `result` |
| `tool` | string | 每行 | `fastboot` / `adb` / `vcom` / `internal` |
| `argv` | array | `start`（其余行可省略） | 脱敏后的参数数组；`start` 行**必带** |
| `exitCode` | int? | `result` | 子进程退出码；参数校验失败/未执行时为 `null` |
| `durationMs` | int? | `result` | 从 `Begin` 到 `Result` 的墙钟毫秒 |
| `stdoutTail` | string? | `result` | stdout 尾部，`<=2048` 字节（UTF-8 边界安全截断） |
| `stderrTail` | string? | `result` | stderr 尾部，同上；**失败时是主证据** |
| `ok` | bool? | `result` | 结论；`step`/`prompt` 行不写 |
| `verdictSource` | string? | `result` | 判据来源：`exit-code` / `arg-validation` / `timeout` / `output-contains:OKAY` / `exception:…` |
| `notes` | string? | 任意行 | 自由文本；`step` 行就是步骤说明 |

脱敏规则（`Services/Diagnostics/Redactor` 内置在 logger 里）：

| 规则 | 例子 |
|---|---|
| 密钥型选项的**下一个**参数整段掩码为 `***` | `--password hunter2` → `--password ***` |
| 已注册 secret（`OperationLogger.RegisterSecret`）整段掩码为 `***(<len>)` | 解锁码 `UUUU…`(16) → `***(16)` |
| 其它参数原样保留 | `flash boot /tmp/boot.img` 原样 |

## 3. 与 VCOM 会话日志的对接

`Services/VcomSessionLogger.cs`（另一 lane 在建）继续拥有 **VCOM 人读转录**（帧级 ACK、
重试、拔插提示），`OperationLogger` 拥有 **机器读判定**。两者不互相替代，约定：

1. VCOM 会话开始时：`using var scope = OperationLogger.Begin("vcom.session", "vcom", new[]{"<port>","<soc>"})`。
2. `VcomSessionLogger` 每写一条转录行，同时调一次 `scope.Step(text)`（同一文本，人读/机读对齐）。
3. 需要用户动作（工程线短接、按电源键）时调 `scope.Prompt(question, answer)`；
   **应答一律由 `VcomSessionLogger` 记录原文**，scope 只记 `ok/answer` 摘要，避免双份数据漂移。
4. 会话结束调 `scope.Result(ok:…, exitCode:null, verdictSource:"vcom:ack|loader-exit|unconfirmed", notes: transcriptPath)`；
   转录文件路径写进 `notes`，这样 JSONL 是索引、转录是全文。
5. `category` 固定 `vcom`，于是 `~/.local/state/kirin-tool/vcom/` 下同一会话的
   JSONL 与转录文件时间戳相邻，`ls -t` 即可配对。

## 4. 迁移顺序

| # | 步骤 | 原子性 |
|---|---|---|
| 1 | `Services/Diagnostics/OperationLogger.cs`（本文件）+ `tests/OpLogging` | 新增文件，零调用方，可单独回滚 |
| 2 | `Utils/ProcessRunner`：每个 `RunAsync` 同时写 JSONL；保留原 `WriteDiagnostic` 与 fastboot 文本日志 | 加可选参数 `op`/`progress`，旧签名不变 |
| 3 | `Services/FastbootClient`：逐命令显式 `op`；解锁走 `ProcessRunner.RunStreamingAsync`；解锁码变参数（默认 16×`U`，长度≠16 即失败 + `verdictSource=arg-validation`） | 对外元组签名不变，新增可选参数 |
| 4 | VCOM 会话（另一 lane 的 `VcomSessionLogger` 落地后）按 §3 接 `scope.Step/Prompt` | 只加调用，不改协议 |
| 5 | 其余设备写路径（`FastbootFlasherService`、`USBUpdate`、`PreOpBackup`、`OemInfo*`）补 `op` | ✅ 已落地（lane G2，`feat/opscope`）；逐行清单与 `verdictSource` 取值见 [`op-scope-map.md`](op-scope-map.md) |
| 6 | 日志消费侧：`doctor` 打印最近会话摘要；GUI「打开日志目录」 | 纯读 |

## 5. 兼容性

- **文件位置**：新的 JSONL 目录默认 `~/.local/state/kirin-tool/`（不带 `log` 子目录、大小写不同），
  与老 `kirintool_log_YYYY_MM_DD.log`（`<exe>/log` 或 `$XDG_STATE_HOME/Kirin-Tool/log`）**并存**。
  老文件继续写，存量用户脚本/工单模板不受影响。
- **开关**：`KIRIN_LOG_DIR` 覆盖根目录（CI/容器）；`KIRIN_LOG_DISABLE=1` 整层关闭。
- **失败降级**：logger 自身任何异常被吞掉且只在 stderr 打**一次**，绝不改变主流程返回值；
  磁盘满/只读时行为与今天「日志静默丢失」一致，但至少有一行 stderr 证据。
- **退出码语义不变**：`ProcessResult.ExitCode`/`IsSuccess` 与 `UnlockBootloaderAsync` 的元组语义保持原样，
  新字段只是旁路记录。
- **输出不删**：`ProcessRunner.WriteDiagnostic`、GUI 进度回调、CLI stdout 全部保留。

（本节 DONE-1）

## 6. 实施记录

### 6.1 DONE-2：`Services/Diagnostics/OperationLogger.cs`

新增 1 个文件（约 430 行，含 XML 注释），零外部依赖（`System.Text.Json` 已在框架内）：

| 类型 | 作用 |
|---|---|
| `OperationLogger`（static） | `Begin(op, tool, argv)` 开一个操作 → 返回 `OperationScope`；`Redact`/`ParseArgv`/`RegisterSecret`；`RootDirectory`/`Enabled` 读 `KIRIN_LOG_DIR`、`KIRIN_LOG_DISABLE` |
| `OperationScope`（IDisposable） | `Step(note)` / `Prompt(question, answer)` / `Result(ok, exitCode, durationMs, stdout, stderr, verdictSource, notes)`；`FilePath` 暴露本操作的文件路径；`Dispose` 时若没写 result 则补一条 `ok=false, verdictSource=unknown:no-result`（操作不允许静默消失） |

实现要点：

- **一操作一文件**：`<root>/<category>/<yyyyMMddTHHmmssZ>-<pid>.jsonl`，同一毫秒同 pid 撞名时追加 `-2`… 后缀；`FileMode.CreateNew` 保证不会串写别人的文件。
- **权限**：目录 `0700`、文件 `0600`（`File.SetUnixFileMode`，非 Windows 才执行）。
- **线程安全**：写行在 scope 内的 `lock(_gate)` 里完成 `Utf8JsonWriter` → 单次 `Write` + `Flush`；不同 scope 完全独立，交错调用不会混行（测试 ②）。
- **异常策略**：所有路径 `catch` → `OperationLogger.ReportFailure`，整个进程只往 stderr 打一次 `[operation-log] disabled after error: …`，之后静默；log 挂掉不影响任何返回值。
- **截断**：`stdoutTail`/`stderrTail` 按 **UTF-8 字节** 截到 `<=2048`（`Tail()` 从尾部向前累计，代理对不拆）。
- **脱敏**：密钥型选项（`--password`、`--token`…）掩码其值（`--token=abc` → `--token=***`）；`RegisterSecret` 注册的字面量掩码成 `***(len)`。

### 6.2 DONE-3：`Utils/ProcessRunner` + `Services/FastbootClient`

**ProcessRunner**（同一文件，只加不改语义）：

- 三个入口都新增可选 `string op = null`：`RunAsync(path, string)`、`RunAsync(path, IReadOnlyList<string>)`、新增 `RunStreamingAsync(path, args, IProgress<string>, …)`。
- 每次运行固定序列：`Begin(op ?? <tool>.<verb>, tool, argv)` → `step("exec <path>")` → 执行 → `Result(ok=ExitCode==0, exitCode, durationMs, stdout, stderr, verdictSource)`。
- `tool` 由可执行文件名判定（含 `fastboot`/`adb`，否则 `internal`），`op` 缺省取第一个非选项 token。
- **stdout/stderr 分开记**（`ProcessResult.Output` 仍是 `stdout+stderr` 的混流，保持向后兼容），审计 S24「OKAY 分不清来自哪个流」在日志层解决。
- 超时：`kill(entireProcessTree)` 后写 `ok=false, verdictSource=timeout` 再抛原有 `TimeoutException`；启动失败写 `verdictSource=exception:…` 后重抛。
- 原 `WriteLog`/`WriteDiagnostic` 一字未改（fastboot 名的老文本日志照旧）。

**FastbootClient**（每个命令都有 op，argv 由 ProcessRunner 记录）：

| 方法 | op |
|---|---|
| `IsDeviceConnected` | `fastboot.devices` |
| `QueryVariableAsync` / `GetVarAsync` / `ReadNveVariable` | `fastboot.getvar` |
| `ReadPsidAsync` | `fastboot.get-psid` |
| `WriteNveVariable` | `fastboot.setvar.nve`（写伪装成 getvar，见 H34） |
| `FlashPartition` / `FlashOemInfo` | `fastboot.flash` / `fastboot.flash-oeminfo` |
| `PullOemInfo` | `fastboot.dump-oeminfo` |
| `EraseFrpWithSteps` | `fastboot.erase-frp.step1..3` |
| `EnableDowngradeWithSteps` | `fastboot.enable-downgrade.step1..4` |
| `OemCommandAsync` / `CommandAsync` | `fastboot.oem.<verb>` / `fastboot.<verb>` |
| `RebootBootloaderAsync` / `RebootAsync` | `fastboot.reboot-bootloader` / `fastboot.reboot` |
| `UnlockBootloaderAsync` | `fastboot.unlock` |

**解锁码参数化**（H39 从内联字面量变成参数 + 常量默认值）：

```csharp
public const string DefaultUnlockCode = "UUUUUUUUUUUUUUUU";  // 厂商固定口令，默认值
public const int    UnlockCodeLength = 16;
public async Task<(bool IsSuccess, string Output)> UnlockBootloaderAsync(
    IProgress<string> progress = null, string? unlockCode = null)
```

- 长度 ≠ 16 → **不发任何命令**，写 `phase=result, ok=false, exitCode 缺省, verdictSource=arg-validation`，返回 `(false, "Unlock code must be exactly 16 characters long (got N); nothing was sent to the device.")`。
- 调用方传自定义码 → `OperationLogger.RegisterSecret(code)`，日志里是 `***(16)`；默认的 `UUUU…` 是公开常量（审计报告里就写着），保留可读以便溯源。
- 对外元组语义 `(IsSuccess, Output)` 未变，GUI 的 `ClassifyUnlock` 不受影响。

### 6.3 DONE-4：设备侧入口唯一性（`Process.Start` 前后对比）

审计口径：`new ProcessStartInfo` + `Process.Start` 出现在**生产代码**的每个位置。

| 位置 | 基线 35c6f6b | 现在 | 说明 |
|---|---|---|---|
| `Utils/ProcessRunner.cs` | 1 处（`StartInfo()` 工厂，:50）+ 1 处 `process.Start()`（:64） | 同 1 处工厂 + 1 处 `process.Start()` | 仍是唯一出口；新增 `RunStreamingAsync` 复用它 |
| `Services/FastbootClient.cs` | **1 处**（:190-291，`oem unlock UUUU…` 裸起） | **0 处** | 下沉为 `ProcessRunner.RunStreamingAsync`，同超时/同 kill-tree/同 JSONL |
| `MainWindow.axaml.cs` 外链 3 处（:2722/:2728/:2733） | 3 处 `Process.Start{UseShellExecute=true}` | 3 处（未动） | **保留**：打开浏览器不是设备路径，属审计 BB-35（另一 lane），本轮不碰 |
| `tests/**` | `Process.Start` 驱动 CLI 二进制 | 同 | 测试桩，非生产路径 |

结论：**设备写路径上的 `ProcessStartInfo` 从 2 处降到 1 处（唯一工厂）；`FastbootClient` 内 0 处。**
流式读输出没被牺牲——`RunStreamingAsync` 的 `PumpAsync` 保留逐块 `progress.Report`（GUI 解锁进度条行为不变），
失败时也保留已读文本（对应审计 S26）。

### 6.4 DONE-5：`tests/OpLogging`

仓库现有测试是 **console 断言程序**（`tests/<name>/Program.cs` + `ProjectReference` 到主 csproj，由
`tests/suites.txt` 被两条 CI 流水线循环执行），没有 xunit 包。为「沿用仓库风格」并且不引第三方包，
`tests/OpLogging` 用同一模板实现，覆盖要求里的 5 组：

| 组 | 覆盖 | 判据 |
|---|---|---|
| ① 字段完整性 | `start/step/prompt/result` 四行、`ts/pid/op/tool/argv/exitCode/durationMs/stdoutTail/stderrTail/ok/verdictSource/notes` 逐字段 | 缺任一字段即失败；另查 `0600`/`0700` |
| ② 两 scope 交错 | 同工具两个 scope 交替 `Step` | 文件不同、行不串、各自 verdict 独立 |
| ③ 失败路径 | `/bin/sh -c 'echo boom >&2; exit 3'` | `ok=false` + `stderrTail` 含 boom + `exitCode=3` + `verdictSource=exit-code:3` |
| ④ 解锁码校验 | 15/17 字符被拒、16 字符放行、自定义码掩码 | `verdictSource=arg-validation` 且无 `exitCode`；`***(16)` 出现；明文不出现在磁盘 |
| ⑤ ProcessRunner 自洽 | `/bin/echo`（桩，不依赖 fastboot）+ 超时路径 | 日志 `exitCode/ok/stdoutTail` 与返回值一致；超时写 `verdictSource=timeout` 且仍抛 `TimeoutException` |
| 附加 | 脱敏、引号 argv 解析、2 KB 截断、无 result 的 Dispose、`KIRIN_LOG_DISABLE=1` | 行为即文档 |

**测试数**：

| | 检查项 |
|---|---|
| 基线（`dotnet test` 无测试工程，按 `tests/suites.txt` 17 个 harness 计） | **387** |
| 新增 `tests/OpLogging` | **79** |
| 现在全量 | **466**（`0 FAILED`） |
| `dotnet build Kirin-Tool.sln -c Release` | 0 error（基线 0 error / 117 warning，新增文件 0 新警告） |

### 6.5 DONE-6：真实 JSONL 示例

以下三行是实际跑出来的（`KIRIN_LOG_DIR=/tmp/oplog-sample`，用命名为 `fastboot` 的 shell 桩代替真机，
未接触任何设备；`tool` 与目录取自可执行文件名，所以与生产一致）：

成功（`fastboot flash boot`，`/tmp/oplog-sample/fastboot/20261113T094735Z-761095.jsonl` 的 result 行）：

```json
{"ts":"2026-11-13T09:47:35.766Z","pid":761095,"phase":"result","op":"fastboot.flash","tool":"fastboot","exitCode":0,"durationMs":60,"stdoutTail":"Sending 'boot' (4096 KB)                     OKAY [  0.120s]\nWriting 'boot'                                OKAY [  0.210s]\nFinished. Total time: 0.341s\n","stderrTail":"","ok":true,"verdictSource":"exit-code"}
```

设备拒绝（`oem dump-emmc`，stderr 是唯一证据）：

```json
{"ts":"2026-11-13T09:47:35.771Z","pid":761095,"phase":"result","op":"fastboot.oem","tool":"fastboot","exitCode":1,"durationMs":2,"stdoutTail":"","stderrTail":"FAILED (remote: 'Command not allowed')\n","ok":false,"verdictSource":"exit-code:1"}
```

参数校验拦下（解锁码长度，根本没起进程）：

```json
{"ts":"2026-11-13T09:47:35.772Z","pid":761095,"phase":"result","op":"fastboot.unlock","tool":"fastboot","durationMs":0,"stderrTail":"Unlock code must be exactly 16 characters long (got 5); nothing was sent to the device.","ok":false,"verdictSource":"arg-validation","notes":"unlock code rejected before fastboot was started"}
```

（本节 DONE-2/3/4/5/6）

### 6.7 DONE-5（续）：其余写路径接入（lane G2，分支 `feat/opscope`）

§4 的第 5 步落地。逐行映射、每行允许的 `verdictSource`、以及「哪一行没有硬件无关断言、为什么」都在
[`op-scope-map.md`](op-scope-map.md)；这里只记实现要点：

- 新增 `Services/Diagnostics/OpLog.cs`：`OpNames`（op 名常量，调用点不写字面量）+ `OpLog`（step 前缀
  `ok:`/`failed:`/`degraded:`/`skipped:`、verdict 字符串、字节数、dload 目录清单与 `CleanDirectory`）。
- 接入的 op：`flash.xml`、`flash.ota`、`flash.rescue`、`flash.image`（单镜像兜底）、`backup.preop`、
  `backup.dump`（单分区 dump 兜底）、`usbupdate.flash`、`usbupdate.unpack`、`usbupdate.dload-cleanup`、
  `oeminfo.read`、`oeminfo.convert`、`oeminfo.write`。
- **补上的静默点**（只加记录，不改控制流）：`PreOpBackup` 的 manifest 写入 `catch { return null; }`、
  getvar 身份读取、sha256 计算、部分文件删除失败；`USBUpdateFlasherService`/`SoftwareTestpointService`
  的 `try { Directory.Delete } catch { }`；`OtaFlashRunner` 暂存目录清理；`DeviceWriteOps` 的
  `rescue_ugs_port` 读失败；USBUpdate 包缺失时的静默 `continue`。
- 「不支持/跳过」不再是 console warning：`backup.preop` 的 `unsupported`/`skipped-timeout` 写
  `degraded:`/`skipped:` step，并在 `result.notes` 里点名；`flash.*` 的未解析名/白名单排除写 `skipped:`；
  redline 拒绝写 `result.ok=false, verdictSource=redline-gate`（异常照旧抛出）。
- 冻结边界（`tests/StableParity/boundary.json`）只刷新 1 个文件：
  `Services/USBUpdate/SoftwareTestpointService.cs`（两处 dload 删除改成 `OpLog.CleanDirectory`，控制流不变），
  理由写在该条 reference 里；`MainWindow.axaml.cs`、`OemInfoEditor.cs`、`USBUpdateApp.cs` 等未动。
- 测试：`tests/OpLogging` 79 → **127**（新增 backup.preop / flash.image / backup.dump / oeminfo.* /
  dload-cleanup 五组：start+result、`verdictSource` 非空且点名依据、`degraded:`/`skipped:` 事件、
  失败路径 `ok=false` 带原因）；`tests/PreOpBackup` 新增 (f) 组断言备份运行的 op 文件。
  全量 `tests/suites.txt` 22 套全绿，`SurfaceParity` 369 项保持绿。
