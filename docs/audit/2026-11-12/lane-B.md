# Lane B — 静态质量 / 资源与进程管理 审计报告

日期：2026-11-12 ｜ 范围：全仓 *.cs（排除 bin/、obj/、fastboot-src/、tests/、loaders/、fastboot/ payload）
方法：只读 rg + 人工读码；未跑 dotnet build（遵 lane 约束）。

## 待办骨架
- [x] 空 catch 全量清点（156 处 catch，逐类判定）
- [ ] async void 清点（17 处）
- [ ] Process/ProcessRunner 专项
- [ ] 资源释放（native/SerialPort/FileStream/temp/Process.Dispose）
- [ ] 警告按文件聚合 + TreatWarningsAsErrors 分档
- [ ] 其它（重复代码/热路径/Path.Combine/编码/Thread.Sleep）

---

### [P2] USBUpdateApp.cs 两处空 catch 仅包住临时文件删除，属无害清理
- 证据: `Services/USBUpdate/USBUpdateApp.cs:84`、`:276`：`try { File.Delete(imgPath); File.Delete(headerPath); } catch { }`（仅当提取出 0 字节镜像时清理）。已通读上下文，catch 不覆盖镜像读取/解析（那些错误会向上抛）。
- 影响: 删除失败只留残留临时文件，无功能影响；对用户/设备/CI 均无风险。
- 建议修复: 无需修复；若清警告可写 `catch (IOException) { /* best-effort cleanup */ }`。不需要真机验证。
- 置信度: 高

### [P2] ProcessRunner 超时 Kill 单进程、不等待退出，且合并 stdout/stderr 丢失分流信息
- 证据: `Utils/ProcessRunner.cs:87-94`（正确使用 `using var process`，先前「未 Dispose」判断有误，已更正）：
```csharp
catch (OperationCanceledException)
{
    process.Kill();
    WriteLog(...); throw new TimeoutException(...);
}
```
Kill 未用 `Kill(entireProcessTree:true)`，也无 WaitForExit；`Output = output + error` 把两路流拼接无法区分。
- 影响: 触发条件：fastboot 卡死触发超时。后果：残留 fastboot 进程短暂持有 USB 句柄，紧接着的 device 扫描可能报 `device busy`；对 CI 影响：并行任务互踩概率低但存在。
- 建议修复: `Utils/ProcessRunner.cs` 超时分支改 `process.Kill(true); process.WaitForExit(3000);`（ProcessResult 增加 Error 字段分流，可选）。不需要真机验证。
- 置信度: 高

### [P2] FastbootClient 桥接进程轮询路径三处空 catch，读失败被静默忽略
- 证据: `Services/FastbootClient.cs:233`、`:260`（`catch (Exception ex)` 后块体为空，ex 未用）、`:282`（`catch { }` 包裹整个进程等待）
- 影响: 触发条件：fastboot 子进程在长操作（erase/format）中崩溃或 stdout 提前关闭。后果：客户端把异常当作「无新输出」继续轮询直至超时，用户看到的是笼统超时而非真实错误；对设备无直接风险；对 CI 无影响。
- 建议修复: `Services/FastbootClient.cs` :233/:260 改为 `catch (Exception) { /* 保留 */ }` 消除 CS0168 并加注释；:282 在 catch 中记录进程 ExitCode。无需真机验证。
- 置信度: 高

---
（后续条目随审计进度追加）

### [P2] VcomFlasher.Connect 重复调用或 Open 抛异常时泄漏 SerialPort 句柄
- 证据: `Services/VcomFlasher.cs:46-64`：`_serialPort = new SerialPort(...); _serialPort.Open();` 无 try/finally、无先 Dispose 旧实例。`ReconnectAfterCableAsync` 有先 Dispose，但若上一次 Open 抛 IOException（端口被占用/被拔出），new 出的 SerialPort 对象无人释放。
- 影响: 触发条件：VCOM 流程异常后重试。后果：Linux 上 /dev/ttyACM 句柄滞留，下次 Connect 可能 Access denied，用户需重启应用。对设备无风险；对 CI 无影响（FindWindowsPortByVidPid :238 有 OperatingSystem.IsWindows() 守卫，Linux 上不可达，CA1416 仅为平台标注噪声）。
- 建议修复: `Services/VcomFlasher.cs` Connect 开头调用 Dispose()（幂等，已置 null），并把 new SerialPort 移入局部变量、Open 成功后才赋给字段。需要真机验证（插拔 12d1:3609 重试路径）。
- 置信度: 高

### [P2] OemInfoService 用共享固定临时路径 temp_oeminfo.img，双实例互踩
- 证据: `Services/OemInfoService.cs:31` `Path.Combine(Path.GetTempPath(), "temp_oeminfo.img")`（非 Guid/每实例）。两个 Kirin-Tool 实例（或 GUI+CLI 同时 pull OEMInfo）会写同一文件。
- 影响: 触发条件：并行两份工具同时从设备 pull。后果：A 实例读到 B 实例写了一半的镜像，转换出损坏 OEMInfo（可能被刷回坏 OEM 分区）。对 CI 无影响。
- 建议修复: `Services/OemInfoService.cs` 路径改为 `Path.Combine(Path.GetTempPath(), $"temp_oeminfo_{Guid.NewGuid():N}.img")`，CleanupTempFiles 按实例路径删。不需要真机验证。
- 置信度: 高

### [P2] SuperMerger.ReadStruct 对截断文件会越界读取，ReadBytes 短读未校验
- 证据: `Utils/SuperMerger.cs:132-137`：
```csharp
byte[] bytes = reader.ReadBytes(Marshal.SizeOf<T>());
GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
```
文件末尾不足一个结构体时 bytes 短于 Marshal.SizeOf<T>()，PtrToStructure 按完整结构体长度解引用 → 越界读（AccessViolationException 或垃圾 header 值）。
- 影响: 触发条件：用户选了截断/非 sparse 的 super 分片合并。后果：进程崩溃或抛难懂异常；对设备无直接风险（写入在校验之后）。
- 建议修复: `Utils/SuperMerger.cs` ReadStruct 开头加 `if (bytes.Length < Marshal.SizeOf<T>()) throw new EndOfStreamException(...)`。不需要真机验证。
- 置信度: 高

### [P2] FastbootClient 解锁轮询无锁读 combinedBuilder + Task.Delay 硬编码时序
- 证据: `Services/FastbootClient.cs:277-287`：
```csharp
await Task.Delay(500);
try { await Task.WhenAny(outputTask, errorTask, Task.Delay(2000)); } catch { }
string output = combinedBuilder.ToString();
```
outputTask/errorTask 在锁内 append，主线程此处无锁读 combinedBuilder.ToString()，读的时机靠 500ms+2s 猜——长输出（oem unlock 的 INFO 流）可能未读完即截断。
- 影响: 触发条件：解锁输出量大或进程退出慢。后果：返回给 UI 的解锁日志不完整（误导用户判断解锁结果）；无锁读理论上可读到撕裂状态。对设备无风险。
- 建议修复: `Services/FastbootClient.cs`：await 进程退出后 `await Task.WhenAll(outputTask, errorTask)`（EOF 必然到达，无需 WhenAny+Delay），读取放锁内快照。不需要真机验证。
- 置信度: 高

### [P2] ProcessDialogUapp 三处 `catch (Exception ex)` 空体（CS0168），均属 UI best-effort
- 证据: `UI/ProcessDialogUapp.axaml.cs:382`、`:407`、`:424`（滚动估算、Cancel、Close 按钮的空 catch）。语义可接受。
- 影响: 无功能风险；仅编译警告 CS0168 与调试期静默。
- 建议修复: 改 `catch (Exception) { /* UI best-effort */ }` 清警告，不改控制流。不需要真机验证。
- 置信度: 高


### [P1] 全程序无全局异常处理器，async void 中未捕获异常直接崩进程
- 证据: `Program.cs`/`App.axaml.cs` 均未注册 `AppDomain.CurrentDomain.UnhandledException` 或 Avalonia 的 `DispatcherUnhandledException`（已全文核查）。而仓库有 17 处 `async void`（见下条），其中多处在 try 之外还有 await：`MainWindow.Extractor.cs:63` BrowseExtractionSource_Click（先 `await ShowOpenFileDialog`，再 `await LoadExtractionSourceAsync`，均无 try）、`MainWindow.Extractor.cs:77`、`MainWindow.axaml.cs:2430` BrowseOemInfoFile_Click、`MainWindow.axaml.cs:2718` RebootDevice_Click（try 只包各子读取，ShowMessageBox 之外裸奔）。
- 影响: 触发条件：任一 async void 处理器抛出未被捕获的异常（如 StorageProvider 对话框在异常桌面环境抛 IOException）。后果：Avalonia 默认把 async void 异常上抛至 UnhandledException → 进程崩溃；若正在刷写中，进度对话框消失、fastboot 中途断开（对设备：半刷状态；对用户：无保存无提示；对 CI：headless 跑 CLI 不受影响）。
- 建议修复: ① `App.axaml.cs` OnFrameworkInitializationCompleted 注册 `AppDomain.CurrentDomain.UnhandledException` + `Dispatcher.UIThread.UnhandledException`，记日志（复用 ProcessRunner.WriteDiagnostic）并弹错；② 逐个 async void 包 try/catch。先做①兜底，不需要真机验证。
- 置信度: 高

### [P1] async void 17 处分类：13 处合法事件处理器（有兜底），4 处危险
- 证据（合法，均有 try/catch 或 OperationCancelled 兜底，异常已吞）：MainWindow.axaml.cs:241/316/705/815/858（858 内部 StartFullOtaFlash/StartUsbUpdateFlash 返回 (bool,string) 不抛）、1339（转 ExecuteCommand 返回 Task）、2187、2601；MainWindow.ChipCapabilities.cs:20；MainWindow.UdevNotice.cs:26/36；MainWindow.axaml.cs:798（await 均为不会抛的本地属性写）。危险：MainWindow.Extractor.cs:63、:77（无任何 try，见上条）、MainWindow.axaml.cs:2430、:2718（ShowMessageBox/属性赋值外仍有裸 await 路径）。
- 影响: 危险 4 处在对话框/设备 IO 抛异常时崩进程（同上条）；合法 13 处风险仅为「静默失败无日志」。
- 建议修复: 危险 4 处包 try/catch 并 Report；合法处保持（Avalonia 事件处理器 async void 是标准写法）。不需要真机验证。
- 置信度: 高（合法/危险划分基于逐个读方法体）

### [P1] CLI staging 目录 .kirintool-cli-tmp 从不删除，每次刷机泄漏数 GB 磁盘
- 证据: `Cli/Commands/WriteCommands.cs:198`（默认 `<源文件目录>/.kirintool-cli-tmp`）；全文件 grep 无任何对 workDirectory 的 Delete（OtaFlashRunner 只删自己的时间戳子目录 :318，父目录保留）。
- 影响: 触发条件：CLI 反复执行 flash/ota 流程。后果：每次残留 .kirintool-cli-tmp 下提取镜像（base/cust/preload super 合并中间产物可达 10+ GB），磁盘写满后后续刷写失败；对用户：需手动清理；对 CI：runner 本机磁盘被耗尽。
- 建议修复: `Cli/Commands/WriteCommands.cs` 流程结束（finally）若 workDirectory 是默认路径且为空则 `Directory.Delete(workDirectory, recursive: false)`；或在 OtaFlashRunner finally 里对默认 work-dir 一并清理。不需要真机验证。
- 置信度: 高

## 空 catch 全量分类（26 处空/等价空 catch，共 156 处 catch）

| 位置 | 判定 |
|---|---|
| USBUpdateApp.cs:84/:276 | P2 无害：仅删 0 字节临时镜像 |
| OtaFlashRunner.cs:311/:318/:348 | P2 无害：临时文件/目录删除，异常路径已 rethrow |
| MainWindow.axaml.cs:1301/:1821/:1939、SoftwareTestpointService:48/:123、USBUpdateFlasherService:95/:206、FastbootFlasherService:56、MainWindow.axaml.cs:1555/:1570 | P2 无害：清理后必 rethrow（1570 处上下文核实）|
| FastbootFlasherService.cs:99、MainWindow.axaml.cs:1325 | P2 无害：非取消异常记入 lastError 后继续 |
| UI/ProcessDialogUapp.axaml.cs:382/:407/:424、UI/FastbootPartitionSelector.axaml.cs:187/:200、MainWindow.UdevNotice.cs:34 | P2 无害：UI best-effort（滚动/关闭/剪贴板）|
| ProcessRunner.cs:131/:137 | P2 无害：日志双路径回退（exe 目录→XDG state）|
| FastbootClient.cs:233/:260/:273/:282 | P2 中风险：读线程异常被吞 → 日志可能不完整（另有 P2 条目）；不吞设备错误 |
| OemInfoService.cs:104（CleanupTempFiles）| P2 无害 |
| VcomFlasher.cs:158、MainWindow.axaml.cs:451/585/1163 | P2 无害：OperationCanceledException 预期路径 |
| UserSettings.cs ×6、MainWindow.Settings.cs:30、DialogService.cs:55 | P2 无害且必要：损坏设置/只读 home 不允许阻塞启动 |
| CommandSupport.cs ×10、Cli/Mcp/McpServer.cs、CliRunner.cs | P2 无害：只读探针语义「失败即返回默认值」为设计意图 |

**结论：未发现空 catch 吞掉写入/设备错误（P1=0）**；所有刷写路径的异常要么 rethrow 要么记入结果。风险集中在 FastbootClient 读线程（见前条）。

## 编译警告按文件聚合（基于 lane 线索 + 抽样核实）

| 文件 | 警告 | 定性 |
|---|---|---|
| UI/PartitionSelectorDialog.axaml.cs:33-34 等 | CS8618（_partitions/_statusText 等未初始化）| 噪声：仅绑定字段，构造后即赋值；但属真空指针潜势（赋值前 UI 绑定早触发会 NRE）→ 建议改 `= new()`/`??=` 一行修 |
| UI/ProcessDialogUapp.axaml.cs | CS8618 + CS0168×3（:382/:407/:424）| CS0168 真冗余，一行修；CS8618 噪声 |
| Utils/UpdateApp.cs | CS8625/CS8603/CS8604（_binaryReader/_fileStream 可空标注）| 半真：构造失败路径已 Dispose 并抛出，字段实际非空；改 `= null!` 或拆 nullable 标注 |
| Utils/SuperMerger.cs | CS8603/CS8625 | 噪声为主 |
| Utils/ProcessRunner.cs | CS8603/CS8604（WriteLog 参数可空）| 噪声 |
| Utils/LinuxSerialPortFinder.cs | CS8603/CS8625（ResolveSerialDevice 可空返回链）| 真语义：串口目录不存在返回 null 是设计；加 `?` 标注即修 |
| Services/VcomFlasher.cs:238 | CA1416（ManagementBaseObject 仅 Windows）| 噪声：:234 有 `OperatingSystem.IsWindows()` 运行时守卫，Linux 上不可达；按官方方式加 `[SupportedOSPlatform]` 特性消音 |
| MainWindow.axaml 等 | AVLN3001（Avalonia XAML 编译绑定）| 需逐条看 XAML 行；多为绑定路径拼写早发现，值得清 |

**TreatWarningsAsErrors 分档建议**：
- 第一档（可直接开）：CS0168、CS0219、CA1416（配平台标注后）——零风险，建议立即 `TreatWarningsAsErrors` + 逐条修（约 10 处）。
- 第二档（修完再开）：CS8618/CS8625/CS8603/CS8604 全仓约 20-30 处，多为一行 nullable 标注；先 `WarningsAsErrors: nullable` 单独跑一轮修完再开启。
- 第三档（暂不开）：AVLN3001 与 Avalonia 绑定类——依赖 XAML 上下文，等第一二档稳定后再评估。
- 禁止在 loaders/、fastboot/、Services/USBUpdate/{UsbDownloadService,XloaderPatcher}.cs 上为开警告而改语义；这三处只加标注（`null!`、`[SupportedOSPlatform]`），不改控制流。

## 其它低风险观察（P2，不单列条目）
- `Services/USBUpdate/UsbDownloadService.cs:103/196/208/541/740` 的 `Thread.Sleep`：该文件为冻结协议链路，仅备注——调用方均在 Task.Run/后台线程（已抽查 StartUsbUpdateFlash），未发现阻塞 UI 线程；如需优化须硬件侧联测，本 lane 不建议动。
- `MainWindow.axaml.cs` 2738 行、49 处 catch：超长分区类（FullOta/USB Update/OEM 三大流程内联），建议拆 partial（已有 ChipCapabilities/Extractor/Settings/UdevNotice 先例），纯结构重构不涉语义。
- `MainWindow.axaml.cs:2557/2563/2568` `Process.Start`（浏览器链接）未 Dispose：Windows 句柄级泄漏，Linux 无影响，P2 可忽略；顺手改 `using var`。
- 编码：`FastbootFlasherService.cs:146` 注册 CodePagesEncodingProvider 用于 flashing.xml 兼容 GBK——必要且正确；其余 Unicode 场景均显式 UTF-8/ASCII，无混用问题。
- Path 处理：抽查 `USBUpdateApp.cs`、`OtaFlashRunner.cs`、`WriteCommands.cs` 均用 Path.Combine；未发现字符串手拼路径分隔符。
- 热路径：进度回调已有 100ms 节流（MainWindow.Extractor.cs:99 段内 `now - lastUpdate < 100`）；`ProcessRunner.WriteDiagnostic` 每次调用 AppendAllText 打开关闭文件，高频刷写时（每分区一次）可接受，不建议动。
- `BufferedUsbTransport`（native libusb 桥）：Dispose 持 writeGate 锁内 Join 读线程 + 关句柄，读线程 100ms 轮询超时下 Join 上限约 100ms，无死锁路径；`NativeBulkDevice.Open` 后唯一的释放点是该 Dispose，异常路径由 `BufferedUsbTransport` 构造器兜底（reader.Start 失败即 Dispose），未发现句柄泄漏路径。

## 结论汇总

- **P0：0 条**
- **P1：3 条**
- **P2：7 条**

最值得先修的 3 条：
1. **[P1] 全程序无全局异常处理器**（App.axaml.cs 注册 UnhandledException 兜底）——一处改动覆盖 17 处 async void 的崩溃面，且不影响任何冻结文件。
2. **[P1] CLI .kirintool-cli-tmp 不清理**——磁盘泄漏随使用线性增长，最终导致刷写失败，修复只动 WriteCommands.cs finally。
3. **[P2] SuperMerger.ReadStruct 截断文件越界读**——用户拿到坏固件即崩进程，一行长度校验修复。
