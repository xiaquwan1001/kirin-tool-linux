# 剩余写路径的 op 覆盖表（op-scope-map）

> lane G2，分支 `feat/opscope`，基线 `2ade54b`（v2.4.14）。
> 上游：[`op-logging.md`](op-logging.md) §4 迁移顺序第 5 步（"其余设备写路径补 op"）。
> 本文件是**逐行实现清单**：每行 = 一条路径 → 一个 op 名 → 关键事件 → 允许的 `verdictSource`，
> 并标注「已接入」与钉住它的测试；没测试的那一行在 §5 说明为什么（需要真机）。

## 1. 规则（写进代码，也写进这张表）

1. **一个 op = 一个 JSONL 文件**（`Services/Diagnostics/OperationLogger`），一个 op 至少写 `phase=start` 与 `phase=result`。
   op 名只来自 `Services/Diagnostics/OpLog.cs` 的 `OpNames` 常量，调用点不写字面量。
2. **每分区 / 每文件 / 每包写 `phase=step`**：step 的 `notes` 是唯一自由文本字段，用固定前缀区分语义：
   - `ok: …` 该项成功（带字节数、exitCode、sha256 等证据）
   - `failed: …` 该项失败（原因必须自解释，不能只写 "error"）
   - `degraded: …` 显式降级：不支持 / 能力缺失（**不再是静默的 warning 文本或空 catch**）
   - `skipped: …` 显式跳过：用户过滤、包内缺失、预算耗尽
3. **`result.verdictSource` 必须点名「判定那一行」的依据**，取值只能来自 §3 的词汇表。
   例：`flash-ota` 的 `ok` 是「exit 0 且输出含 OKAY」，所以 `verdictSource=output-contains:OKAY`；
   失败则写 `output-missing:OKAY` 或 `exit-code:<n>` —— 不允许既写 OKAY 又只写 `exit-code`。
4. **不改变任何 wire 行为**：帧、参数、超时、重试顺序、返回值、异常类型、控制流全部保持；本 lane 只加日志与显式事件。
5. 服务层带 `OperationScope? scope = null` 的方法：调用方有 plan 级 op 时传进来（step 落到那个文件），
   没有时服务自己开一个单动作 op（见 §2 的 `flash.image` / `backup.dump` / 单分区 `flash.rescue`）。

## 2. 路径 → op 映射

| # | 路径（生产代码） | op 名 | 关键事件（`phase=step`） | `verdictSource` 取值 | 状态 | 测试 |
|---|---|---|---|---|---|---|
| 1 | `Services/FastbootFlasherService.FlashPartitionAsync` | `flash.image` | 分区名 / 文件名 / 字节数 / 红线段标记 / exitCode / OKAY 是否出现 | `exit-code`、`exit-code:<n>`、`exception:<Type>` | ✅ 已接入 | OpLogging §9（成功 + 失败） |
| 2 | `Cli/Commands/WriteCommands.cs`（FlashXmlCommand 刷写循环） | `flash.xml` | 计划摘要；每 image 一条（identifier→device 名、文件字节、exitCode、OKAY）；`--exclude`/未解析名写 `skipped:` | `output-contains:OKAY`、`output-missing:OKAY`、`exit-code:<n>`、`partition-outcomes:all-ok` / `:<n>/<m>-failed` | ✅ 已接入 | WriteGates（计划/门禁）+ 本表 §5（op 断言需设备会话） |
| 3 | `Cli/Commands/OtaFlashRunner.ExecuteAsync`（flash-ota） | `flash.ota` | 计划摘要；每分区一条（目标名、字节、exitCode、OKAY）；策略/白名单排除的条目写 `skipped:`；super 合并与暂存目录清理写步骤 | `partition-outcomes:all-ok`、`partition-outcomes:<n>/<m>-failed`、`partition-outcomes:none` | ✅ 已接入 | OtaPlan + FlashPolicy（计划/解包）+ §5 |
| 4 | `Cli/Commands/DeviceWriteCommands.cs`（reboot-usb-update）+ `Services/WriteOperations/DeviceWriteOps.FlashRescuePartitionAsync` | `flash.rescue` | rescue 计划；每目标一条（目标分区/镜像字节/exitCode）；包内缺失写 `skipped:`；模式入口读回结果 | `partition-outcomes:all-ok`、`partition-outcomes:none`、`exit-code:<n>`、`output-contains:OKAY` / `output-missing:OKAY` | ✅ 已接入 | ApprovalGate（门禁）+ §5 |
| 5 | `Services/PreOpBackup.RunAsync` | `backup.preop` | 备份目录、设备身份、每分区 dump（状态/字节/sha256/原因）、manifest 写入 | `partition-status:all-ok`、`partition-status:<ok>/<total>-ok`、`exception:<Type>` | ✅ 已接入 | **PreOpBackup (f)** + OpLogging §8 |
| 6 | `Services/FastbootFlasherService.DumpPartitionAsync` | `backup.dump` | 两个 dump 动词各一条（是否落盘、output 是否干净） | `file-written`、`file-missing`、`loader-capability:invalid-command`、`timeout`、`exception:<Type>` | ✅ 已接入 | OpLogging §9 |
| 7 | `Services/USBUpdate/USBUpdateFlasherService.FlashPartitions` | `usbupdate.flash` | dload 清理前后、逐包解包摘要、super 合并、发现的分区清单、redline 判定、逐分区传输结果 | `usb-transfer:ok`、`usb-transfer:failed`、`cancelled`、`redline-gate`、`files-written:0`、`exception:<Type>` | ✅ 已接入 | 需真机（§5） |
| 8 | 同上（每个包一次） | `usbupdate.unpack` | 包路径/标签、输出目录、分区过滤、逐镜像（名称+字节）、unlockcode 提取或缺省降级 | `files-written:<n>`、`files-written:0` | ✅ 已接入 | 需真机（§5） |
| 9 | `Services/USBUpdate/USBUpdateFlasherService` + `Services/USBUpdate/SoftwareTestpointService`（dload 目录删除） | `usbupdate.dload-cleanup` | **删除前**记录文件数、总字节数与路径清单；删除结果 | `directory-deleted`、`exception:<Type>` | ✅ 已接入 | OpLogging §11 |
| 10 | `Services/OemInfoService.ConvertOemInfo`（`--pull` 分支） | `oeminfo.read` | 拉取目标路径、dump 结果的 exitCode/OKAY、落盘字节数 | `file-written`、`file-missing`、`exit-code:<n>` | ✅ 已接入 | 需真机（§5） |
| 11 | 同上 + `Services/OemInfoEditor`（转换） | `oeminfo.convert` | 源（设备/本地）、输入字节、model/vendor、输出字节、红线段判定、拒绝原因 | `conversion:success`、`conversion:refused`、`conversion:unsupported`、`arg-validation`、`exception:<Type>` | ✅ 已接入 | OpLogging §10 |
| 12 | `Services/OemInfoService.FlashConvertedOemInfo` | `oeminfo.write` | 镜像路径/字节、红线段判定、exitCode/OKAY | `exit-code`、`exit-code:<n>`、`exception:<Type>` | ✅ 已接入 | OpLogging §10 |

## 3. `verdictSource` 词汇表

| 值 | 判定依据 |
|---|---|
| `exit-code` / `exit-code:<n>` | 子进程退出码（fastboot 单命令、flasher 单镜像、oeminfo 写） |
| `output-contains:OKAY` | 退出码 0 **且** 输出含 `OKAY`（plan 级 flash-ota / flash-xml / rescue 模式入口） |
| `output-missing:OKAY` | 退出码 0 但没看到 `OKAY`（设备没确认，按失败记） |
| `partition-outcomes:all-ok` / `partition-outcomes:<failed>/<total>-failed` / `partition-outcomes:none` | 逐分区结果汇总（plan 级 flash.ota / flash.rescue / flash.xml） |
| `partition-status:all-ok` / `partition-status:<ok>/<total>-ok` | 备份逐分区状态汇总（backup.preop） |
| `file-written` / `file-missing` / `file-empty` | dump 是否真的落了非空文件（stock fastboot 会回 OKAY 但不落盘） |
| `loader-capability:invalid-command` / `loader-capability:dump-verb` | loader 能力缺失 → 记 unsupported/degraded 而不是 failed |
| `files-written:<n>` / `files-written:0` | 解包产出的镜像数 |
| `directory-deleted` | dload 目录清理完成 |
| `usb-transfer:ok` / `usb-transfer:failed` / `cancelled` | USB Update 传输结果 |
| `redline-gate` | 红线段闸门拒绝（未写任何字节） |
| `conversion:success` / `conversion:refused` / `conversion:unsupported` | oeminfo 转换结论 |
| `timeout` | 超时（dump / 子进程） |
| `arg-validation` | 参数校验拦下，**未接触设备** |
| `exception:<Type>` | 未预期异常 |
| `unknown:no-result` | scope 未写 result 就 Dispose（logger 兜底，永远是失败） |

## 4. 冻结边界说明

`tests/StableParity/boundary.json` 冻结了 13 个文件。本 lane 只改其中 **1** 个：
`Services/USBUpdate/SoftwareTestpointService.cs` —— 两处「删除 dload 目录」的 3 行调用点换成 `OpLog.CleanDirectory`，
控制流与原来完全相同（仍是「存在则递归删除、失败吞掉继续、调用方随后重建目录」），只是删除前的清单/字节数与失败原因现在被记录。
其余 12 个文件（含 `MainWindow.axaml.cs`、`Services/OemInfoEditor.cs`、`Services/USBUpdate/USBUpdateApp.cs`、
`UsbDownloadService.cs`、`XloaderPatcher.cs`）**一字未动**：GUI 侧的面级 op 由服务层（`DeviceWriteOps`、
`FastbootFlasherService`）承担，不需要改 GUI 文件。哈希按「行为未变、仅日志」逐条刷新，理由写在 `boundary.json` 的 reference 里。

## 5. 未断言的行（诚实清单）

| 行 | 为什么没有硬件无关的断言 | 已有什么覆盖 |
|---|---|---|
| 2 `flash.xml`、3 `flash.ota`、4 `flash.rescue`、7 `usbupdate.flash`、8 `usbupdate.unpack`、10 `oeminfo.read` | 需要 fastboot 会话 / USB Update 通道 / 真机 dump；仓库的硬件无关套件按设计不碰设备（`VcomOpenProbe`/`UsbUpdateProbe` 已被 `suites.txt` 排除） | 计划/门禁/解包逻辑由 `WriteGates`、`OtaPlan`、`FlashPolicy`、`ApprovalGate` 覆盖；op 的字段级断言在 `tests/OpLogging` 用同类服务（flash.image / backup.dump / backup.preop / oeminfo.* / dload-cleanup）钉住 |
| GUI 的 Full-OTA 循环（`MainWindow.axaml.cs`，冻结文件） | 不动 GUI 文件 | 日志里是「逐分区 `fastboot.flash` op + `backup.preop`」，没有 `flash.ota` plan 级 op |
| GUI 的 XML 刷写循环 | 走 `FastbootFlasherService.FlashPartitionAsync`，逐镜像一次调用 | 逐镜像 `flash.image` op；CLI 的 `flash-xml` 是 plan 级 `flash.xml` op |
| `SoftwareTestpointService` 的 USB 传输本身（`UsbDownloadService`，冻结文件） | 不动冻结文件 | 传输前后的 dload 清理（`usbupdate.dload-cleanup`）与 `fastboot.flash` 均有记录 |
