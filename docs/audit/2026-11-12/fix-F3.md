# F3 修复 lane 报告（2026-11-12）

范围：工具层健壮性。只改白名单文件；**未跑构建/测试**（编排者统一构建与 CI），以下为实际编辑与静态核对（grep/read 复核）结果。

## 完成清单（全部完成）

1. **Utils/SuperMerger.cs:134-140** — ReadStruct 加短读校验：ReadBytes 后长度不足 Marshal.SizeOf<T>() 抛 EndOfStreamException，消息含要求/实际字节数；不再把短 buffer 传给 PtrToStructure。
2. **Utils/ProcessRunner.cs:86-93** — 超时分支改为 `process.Kill(entireProcessTree: true)` + `WaitForExit(3000)`，再抛 TimeoutException（消息含超时秒数）。
3. **Services/OemInfoService.cs:34-39 / :91-98** — 临时文件改为每实例唯一路径 `temp_oeminfo_{Guid:N}.img`（构造函数赋值，原为固定共享路径）；ConvertOemInfo 加 finally：pullFromDevice 时所有路径（成功/失败/异常）都调 CleanupTempFiles（只删本实例文件，天然满足）。
4. **Services/FastbootClient.cs**
   - :233/:262 读线程 `catch (Exception ex)` → `catch (Exception)` + 注释（消除 CS0168，不再空吞；吞异常有意保留——进程退出状态决定结果）。
   - :280-289 — UnlockBootloaderAsync 去掉 `Task.Delay(500)` + `Task.WhenAny(..., Task.Delay(2000))` 猜时序，改为进程退出后 `await Task.WhenAll(outputTask, errorTask)`。**无死锁风险**：读任务读至 EOF，进程已退出，管道必然关闭；combinedBuilder 读取改为锁内快照。未实测真机长操作，但 EOF 语义可靠。
5. **Services/VcomFlasher.cs:55-79** — Connect 先幂等 Dispose 旧实例；SerialPort 建局部变量，Open 成功后才赋给字段，Open 抛异常时释放局部实例再 rethrow。未改协议/时序。
6. **可空性/警告清理**
   - UI/ProcessDialogUapp.axaml.cs:382/:408/:426 — 3 处 `catch (Exception ex)` → `catch (Exception)`（CS0168）。
   - UI/PartitionSelectorDialog.axaml.cs:33-34 — `_partitions = new()`、`_statusText = string.Empty`（CS8618）。
   - UI/FastbootPartitionSelector.axaml.cs:34 — `_partitions = new()`；:187/:201 两处空 `catch (Exception ex)` → `catch (Exception)`（同为白名单文件内的 CS0168，顺手清理）。
   - Utils/LinuxSerialPortFinder.cs — `ResolveSerialDevice` 返回类型本已是 `DirectoryInfo?`（CS8603 源头是调用点）；将 `GetUsbDescriptor` 参数改为 `DirectoryInfo?` 消除 CS8604（循环本身已判空）；FindPort 中 `serialDevice!` 的 null-forgiving 保留。
   - Utils/UpdateApp.cs:130/:203 — `ReadPartition`/`InvalidEntry` 返回类型改 `PartitionInfo?`（CS8603/CS8625），调用点本就有 null 判断，控制流不变。
   - Services/VcomFlasher.cs:248 — `FindWindowsPortByVidPid` 加 `[SupportedOSPlatform("windows")]`（+ using System.Runtime.Versioning），调用方 FindIdtDevicePort 已有 `OperatingSystem.IsWindows()` 守卫，守卫未动。
7. **Services/USBUpdate/USBUpdateApp.cs:84-85/:277-278** — 两处空 `catch { }` 收窄为 `catch (IOException)` + `catch (UnauthorizedAccessException)`，各带一行注释；语义不变（清理失败继续提取）。**UsbDownloadService.cs 未动。**

## 剩余警告清单（刻意保留）

- **UI/ProcessDialogUapp.axaml.cs** — `_cancellationTokenSource` 等字段构造函数赋值但编译器视角可能 CS8618 未消（两构造函数均赋值，静态可过；未逐一核对全部警告码）。IsUsbUpdateMode `{ get; set; } = false` 无警告。
- **UI/ProgressDialog.axaml.cs** — 任务清单第 6 条提到它的 CS8618，但该文件**不在本 lane 白名单**里，未改动（见"需要人决策"）。
- **Services/FastbootClient.cs** — `_fastbootPath` 构造函数赋值；`executablePath` 参数无可空标注（public API，改签名超出最小编辑），可能残留 CS8625 级提示。
- **FastbootClient.IsDeviceConnected** `catch (Exception) { return false; }` — 有意保留（探测性查询）。
- 各文件剩余的"空 catch"凡未在任务清单内的（如 MainWindow、UserSettings 等）均未触碰。

## 未做 / 需要人决策

- **ProgressDialog.axaml.cs 的 CS8618**：不在"只改"文件清单，未改。若需要，请另开 lane 或扩白名单。
- FastbootClient 解锁超时路径（:270-275）原样保留（已有 Kill + WaitForExit(2000) + 返回超时消息），未按 item 2 的 Kill(entireProcessTree) 统一——任务只指定 ProcessRunner；建议后续统一。
- 未跑 dotnet build/test（按硬性约束），以上为静态核对。
