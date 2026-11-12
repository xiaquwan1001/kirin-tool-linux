# F2 修复报告：GUI 安全与崩溃兜底

日期：2026-11-12　范围：MainWindow.axaml.cs / MainWindow.Extractor.cs / App.axaml.cs / Services/FastbootFlasherService.cs
状态：仅静态编辑，未跑构建/测试（编排者负责）。

## 已完成

1. fastboot 写入 OKAY 判定（exit 0 不再等于写入成功）：
   - MainWindow.axaml.cs:649（FlashPartitionsWithProgress，XML 刷写路径）：result.IsSuccess 且输出含 OKAY；失败文案改用 FlashFailureDescription(result.Message)，lastError 保留原始 Message。
   - MainWindow.axaml.cs:1288（FlashFullOtaPartitions）：同样加 OKAY 判定；失败项用 FlashFailureDescription(flashResult.Output)，lastError 仍带完整命令与输出。
   - MainWindow.axaml.cs:1887（rescue 刷写，HandleRebootToUsbUpdate）：同样加 OKAY 判定（失败文案原本已用 FlashFailureDescription）。
   - Services/FastbootFlasherService.cs:118：FlashPartitionAsync 的 catch 由返回 "Failed" 改为 "Flash failed: {ex.Message}"；成功判定逻辑未动。
   （未做：无。FlashFailureDescription 复用 MainWindow.ChipCapabilities.cs:7 的现有 helper。）


2. HandleWriteSerialNumber 确认框（MainWindow.axaml.cs:1980 附近）：写入前 ReadNveVariable("SN")（失败显示 "(read failed)"），对照显示新旧 SN，文案写明 PERMANENTLY / NOT recoverable；拒绝则提示已中止并返回。
3. HandleFrpRemove 确认框（MainWindow.axaml.cs:1657 附近）：第一条 erase 之前弹确认，列出 erase frp → erase config → oem frp-erase 三步且不可逆；拒绝则提示并中止，不创建对话框、不发命令。
4. 全局异常兜底（App.axaml.cs 重写）：OnFrameworkInitializationCompleted 注册 AppDomain.CurrentDomain.UnhandledException 与 Dispatcher.UIThread.UnhandledException，均经 ProcessRunner.WriteDiagnostic（Utils/ProcessRunner.cs:100，现有诊断日志通道）落盘；UI 线程异常 args.Handled = true 并弹只读错误框（ShowGlobalErrorDialog，内部全 try/catch，防止兜底自身崩溃）。非 UI 线程异常仅记日志（进程默认终止行为未改）。
5. 危险 async void 包 try/catch（记日志经 ProcessRunner.WriteDiagnostic + ShowMessageBox，业务流程未改）：
   - MainWindow.Extractor.cs:64 BrowseExtractionSource_Click（另加 using Kirin_Tool.Utils）
   - MainWindow.Extractor.cs:87 BrowseExtractionDestination_Click
   - MainWindow.axaml.cs:2475 BrowseOemInfoFile_Click
   - MainWindow.axaml.cs:2772 RebootDevice_Click
6. USB Update 红线差集再确认：已做。OnPartitionsDiscovered 回调（MainWindow.axaml.cs:999 附近）能拿到解包后全量名单（USBUpdateFlasherService 在写闸门前触发，见 USBUpdateFlasherService.cs:248），故对 RedlineHits(discovered) 中未被 pre-scan（redlinePreScan，:963）确认过的差集再弹 ConfirmRedlineWriteAsync。实现方式：新增 ConfirmRedlineDeltaOnBackground（:1680）——发现回调跑在 Task.Run 工作线程上，通过 Dispatcher.UIThread.Post 弹确认并用 TaskCompletionSource + 10 分钟上界等待答案；拒绝/超时则 cancellationTokenSource.Cancel()，服务在 :259 的 ThrowIfCancellationRequested 会发生在任何 flash 写入之前，故拒绝时不会写任何分区。注意：pre-scan 已确认且差集为空时不重复打扰用户。
7. 电量闸门未实现（按指示）。仅加 TODO(battery-gate) 注释，插入点：
   - MainWindow.axaml.cs:597 FlashPartitionsWithProgress（XML 刷写）
   - MainWindow.axaml.cs:916 StartUsbUpdateFlash（USB Update 入口）
   - MainWindow.axaml.cs:1204 FlashFullOtaPartitions（Full OTA）
   - MainWindow.axaml.cs:1833 HandleRebootToUsbUpdate（rescue）

## 静态核对

- 4 个文件花括号配对平衡（axaml.cs 548/548、Extractor 39/39、App 14/14、Service 36/36）。
- 复核方式：每批编辑后 grep/read 确认 hunk 落位（OKAY 判定 ×3、catch 带出 ex.Message、SN/FRP 确认框、App 双钩子、4 处 try/catch、TODO ×4）。
- 未跑 dotnet build / test（按约束，由编排者执行）。未执行任何 git 操作。
- 需人决策/后续：① ConfirmRedlineDeltaOnBackground 阻塞工作线程最久 10 分钟，若产品上想改为「非阻塞取消」需再议；② FlashPartitionAsync 现在把 ex.Message 带出，若某调用方把 Message 直接当用户文案展示，措辞含原始异常（属预期改进）；③ 电量闸门字段名待真机确认。
## 未做 / 需要人决策

- 电量闸门按指示未实现，仅加 TODO(battery-gate) 注释（华为 loader getvar 字段名待真机确认）。
