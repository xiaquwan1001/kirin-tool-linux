# GUI 黑盒审计（lane A，只读快照）

快照：`~/src/wt/ktl-audit` @ `35c6f6b`（main）。
审计范围：Avalonia GUI 全部用户可见动作 → 实际副作用（设备写 / 文件写 / 子进程 / 网络 / 系统设置），
判定标准见「方法」。CLI 侧对照 `Cli/Commands/*.cs`，MCP 侧对照 `Cli/Mcp/McpServer.cs`。

## 1. 方法

四类黑盒判据（满足任一即为黑盒）：

- **B1 no-cli / no-mcp**：副作用（设备写、文件写、进程启动、网络请求、系统设置改动）无法从 CLI/MCP 发起。
- **B2 hidden-step**：内部存在用户不会被告知的隐式步骤（固定/随机延迟、静默重试、自动跳转页面、自动选择文件/分区/CPU 型号）。
- **B3 ui-only-result**：成功/失败结论只体现在 UI（MessageBox / 进度条 / InfoBar），无退出码、无 JSON、无机读日志行。
- **B4 bypass-service**：直接调用 `Process.Start` / fastboot / adb，绕过 `Services/*` 抽象层（=> 无法被 CLI 复用、无法被测试桩替换）。

做法：grep `MainWindow*.cs` 的事件 handler + `*.axaml` 的 `Click=`/`Toggled=`/`SelectionChanged=`；
对每个 handler 读代码，记录「文案 → handler → Service/方法 → 4 类副作用 → 结论来源行」。
行号均取自上述快照（`git rev-parse HEAD` = 35c6f6b）。

DONE-1

## 2. GUI 动作总表

全部入口来自 `MainWindow.axaml`（1256 行）的 `Click=`/`Toggled=`/`SelectionChanged=` 绑定 +
4 个 partial 类（`MainWindow.axaml.cs` / `.Extractor.cs` / `.Settings.cs` / `.ChipCapabilities.cs`）。
UI 文案只有英文（`Assets/` 仅含 Fonts，无 zh 资源），下表给英文原文 + 中文释义。

列说明：**设备写** = 是否改变设备状态；**文件写** = 是否写本机磁盘；**子进程** = 是否 `Process.Start`/ProcessRunner；
**判据** = 决定成功/失败的那一行代码。

### 2.1 全局/导航类（无副作用，但影响后续行为）

| 文案 (中) | 位置 | handler | 实际行为 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| 左栏 9 个导航项 (Welcome…About) | axaml:82-133 | `HandleNavigation` 809 | 仅切换 `IsVisible`；进入 Security/OEMInfo 时调用 `UpdateSecurityUI`/`UpdateOemInfoUI` | 否 | 否 | 否 | — |
| CPU Model 下拉 (20 项) | axaml:54-73 | `CpuComboBox_SelectionChanged` 2611 | 写 `settings.json`；决定 ①VCOM loader 集 ②rescue 分区表 ③OEMInfo 走"直写"还是"文件转换" ④各按钮是否可见 | 否 | 是 | 否 | — |
| (隐藏) Use FastFlash loader | axaml:333-338 `IsVisible="False"` | 2611 里仅 hisi980 时置可见 | 只对 hisi980 可见，切换 loader 上传序列 | 后续 | 否 | 否 | — |
| (隐藏) Unlocking 面板 | axaml:948 `IsVisible="False"` | `UpdateSecurityUI` 2644 | 只对 hisi710/970/980/990/K9000 显示"解锁 bootloader" | 后续 | 否 | 否 | — |
| (隐藏) TogLivePull / FileSelectionPanel | axaml:1057-1062 | `UpdateOemInfoUI` 2624 / `TogLivePull_Toggled` 2605 | 切换 OEMInfo 输入源为设备或文件 | 后续 | 否 | 否 | — |
| udev notice「Copy commands / Check again」 | axaml:165-166 | `CopyUdevCommands_Click` / `CheckUdevRules_Click` (UdevNotice.cs:26,35) | 读 `/etc/udev/rules.d` 等 5 个目录 + 写剪贴板 | 否 | 否 | 否 | `UdevRuleNotice.Inspect` Services/UdevRuleNotice.cs:36 |

### 2.2 Device Information 页

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Read Info (读取设备信息) | axaml:286-291 | `ReadDeviceInfo_Click` 2766 | 8 条 fastboot：`oem lock-state info`、`getvar devicemodel`、`getvar vendorcountry`、`oem oeminforead-{BASE,CUSTOM,PRELOAD}_VERSION`、`oem get-build-number` | 否 | 日志 | 是 ×8 | 结果仅写入 8 个 TextBlock（2778-2836），无返回码 |
| Read SN / PSID (读标识) | axaml:292 | `ReadPsid_Click` ChipCapabilities.cs:120 | `oem get-psid` → `FastbootReadResponses.Psid` | 否 | 日志 | 是 | MessageBox 内容（133） |
| Reboot (重启设备) | axaml:294-298 | `RebootDevice_Click` 2886 | `fastboot reboot` FastbootClient.cs:294 | 是（重启） | 日志 | 是 | `result.IsSuccess` → MessageBox 2899-2906 |

### 2.3 VCOM Operations 页

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Unlock (解锁 fastboot) | axaml:339-345 Tag=unlock-fastboot | `HandleUnlockFastboot` 1494 | `FirmwareUnlocker.UnlockFastboot` → `VcomFlasher.UploadData`（BootROM 注入，真写入）；hisi810/985 先弹 ARB 警告 1504 | **是**（不可逆 ARB+1） | 读 `loaders/<cpu>` | 否（串口直连） | `UnlockResult.IsSuccess` → 进度框关闭按钮 1532 |
| Browse (选择 Base UPDATE) | axaml:374-377 Tag=SwTpUpdate | `BrowseOtaFile_Click` 854 | 文件对话框 → 写 settings.json → 后台 `new UpdateApp(...)` 解析 761 | 否 | 是 | 否 | `FullOtaFile_PropertyChanged` 失败弹 Parse Error 779 |
| Clear (清除选择) | axaml:380-383 | `ClearOtaFile_Click` 902 | 清路径+分区选择 | 否 | 是 | 否 | — |
| Enter (进入软件测试点) | axaml:388-393 Tag=sw-testpoint-enter | `HandleSoftwareTestpoint(true)` 1536 | `SoftwareTestpointService.EnterSoftwareTestpoint` → 解包+**改写 XLOADER.img**(XloaderPatcher) + USB Update 串口传输 | **是** | 是（`./dload`） | 否 | 服务只有"无异常"一种返回；文案 1556 明说模式未确认 |
| Exit (退出测试点) | axaml:394-398 Tag=sw-testpoint-exit | `HandleSoftwareTestpoint(false)` 1536 | `ExitSoftwareTestpoint` 回写 XLOADER 后 `getvar product` 回读 | **是** | 是 | 是 | `ClassifyCommandOutput` 2156 三分支 MessageBox 1570-1590 |

### 2.4 Flash Operations 页

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Fastboot Mode / USB Update Mode (单选) | axaml:426-429 | `IsUsbUpdateSelected` | 切换后所需文件数 4→6，决定 Start 走 `StartFullOtaFlash` 还是 `StartUsbUpdateFlash` | 否 | 是 | 否 | — |
| Browse ×6 (Base/Cust/Preload 的 PTABLE/UPDATE) | axaml:456,501,546,591,636,681 | `BrowseOtaFile_Click` 854 | 同上（含后台解析、写 settings.json） | 否 | 是 | 否 | 761-786 |
| Edit ×6 (选择分区) | axaml:463,508,553,598,643,688 | `EditOtaFile_Click` 871 | `PartitionSelectorDialog`，落回 `otaFile.SelectedPartitions` 881 | 否 | 否 | 否 | 仅对话框 |
| Clear ×6 | axaml:471,516,561,606,651,696 | `ClearOtaFile_Click` 902 | 清空 | 否 | 是 | 否 | — |
| Start (开始 Full OTA) | axaml:737-738 | `StartFullOtaButton_Click` 914 | 文件不全 → 确认框 928；→ `StartFullOtaFlash` 1119 / `StartUsbUpdateFlash` 967 | **是** | 是（临时目录+dload+backup） | 是 | `flashConfirmed` 1369 + `FlashFullOtaPartitions` 1421 聚合文案，弹窗关闭按钮 |
| Enable (允许降级) | axaml:770-778 Tag=enable-downgrade | `HandleEnableDowngrade` 1825 | `FastbootClient.EnableDowngradeWithSteps`：4 条 `oem oeminfoerase-*ver` | **是**（不可逆） | 日志 | 是 ×4 | `SuccessfulStepsCount == 4` 1879 → 仅对话框 |
| Browse (选 USB Update 用 Base UPDATE) | axaml:811-814 Tag=UsbUpdate | `BrowseOtaFile_Click` 854 | 同上 | 否 | 是 | 否 | — |
| Reboot (重启进 USB Update 模式) | axaml:825-832 Tag=reboot-usb-update | `HandleRebootToUsbUpdate` 1890 | `RescuePartitionPlan.ForChip(CPU下拉)` 1906 → 可选 `reboot-bootloader`+30s 轮询 → 解包 rescue 镜像到 `/tmp/kirintool_usb_update_<guid>` → `flash <target>` → `getvar rescue_ugs_port`/`rescue_enter_recovery` | **是**（rescue 分区写+模式切换） | 是（备份+/tmp） | 是 | `ClassifyUsbUpdateModeEntry` 2174 → 进度框 2020-2034 |
| Dump (导出分区) | axaml:869 | `DumpPartitions_Click` 241 | `GetPartitionTableAsync` → `FastbootPartitionSelector` → OpenFolderDialog → `oem dump-emmc` 逐分区到 `<dir>/fastbootimage/` + 生成 `flash.xml` 521 | 否 | 是 | 是 | `DumpPartitionsWithProgress` 508 计数 → 进度框 |
| Flash (按 XML 刷写) | axaml:892 | `FlashFromXml_Click` 316 | OpenFileDialog(xml) → `ParseFlashingXml` → 缺文件检查 343 → 选择器 → redline 确认 373 → `FlashPartitionsProcess` 544 | **是** | 是（backup） | 是 | `result.Message.Contains("OKAY")` 655 |

### 2.5 Security Tools 页

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Remove FRP (清除 FRP) | axaml:932-938 Tag=frp-remove | `HandleFrpRemove` 1762 | 确认框 → `EraseFrpWithSteps`：`erase frp` / `erase config` / `oem frp-erase` | **是**（不可逆） | 日志 | 是 ×3 | `SuccessfulStepsCount==3` 1800 → FrpStepDialog 1801 |
| Unlock (解 BL) | axaml:967-973 Tag=unlock-bootloader | `HandleUnlockBootloader` 2265 | `FastbootClient.UnlockBootloaderAsync` 190：**直接 Process.Start** `oem unlock UUUU…`（16×U） | **是** | 日志 | 是（绕过 ProcessRunner） | `ClassifyUnlock` 2193 + 退出码 → MessageBox 2224 |

### 2.6 Extractor Tools 页（离线）

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Browse… ×2 (源 APP / 输出目录) | axaml:994,999 | Extractor.cs:64,87 | 文件夹/文件对话框 + `UserSettings.*Path` 77,95 | 否 | 是 | 否 | — |
| Select All / None | axaml:1003-1004 | Extractor.cs:106,111 | 勾选状态 | 否 | 否 | 否 | — |
| Extract Selected (导出选中分区) | axaml:985 | Extractor.cs:118 | `PartitionExtractor.ExtractAsync`（strict+usbMode），已存在文件 → 抛异常中止 | 否 | 是 | 否 | `count` → `ExtractorStatus` 文本 158 |
| Cancel | axaml:986 | Extractor.cs:114 | 取消 token，保留已完成 .img | 否 | 是 | 否 | — |

### 2.7 OEMInfo Tools / NVMe Tools / About

| 文案 (中) | 位置 | handler | 调用链 | 设备写 | 文件写 | 子进程 | 判据 |
|---|---|---|---|---|---|---|---|
| Browse (选 oeminfo.img) | axaml:1069 | `BrowseOemInfoFile_Click` 2586 | 文件对话框 + 写 `UserSettings.OemInfoFile` | 否 | 是 | 否 | — |
| Convert (转换 OEMInfo) | axaml:1084 | `BtnConvertOem_Click` 2343 | **按 CPU 下拉二选一**：①8 款芯片走直写 `oeminfoerase-disablewp`+`oeminfowrite-KTModel@`+`KTVendor@` 2374-2426；②其余走 `OemInfoService.ConvertOemInfo` → SaveFileDialog → `File.WriteAllBytesAsync` 2492 → 「Flash Back?」→ `FlashOemInfoToDevice` 2529 | **是** | 是 | 是 | 两条路径各自 `ClassifyCommandOutput`/OKAY 判定 2392,2547 |
| Read NVE SN (读序列号) | axaml:1133 Tag=read-sn | `HandleReadSerialNumber` 2080 | `getvar nve:SN` | 否 | 日志 | 是 | 文本/异常弹窗 2085-2091 |
| Write SN (写序列号) | axaml:1165 Tag=write-sn | `HandleWriteSerialNumber` 2093 | 确认框 → `WriteNveVariable`=`getvar nve:SN@<新值>`（**写操作伪装成 getvar**） | **是**（不可逆） | 日志 | 是 | `writeResult.IsSuccess` → MessageBox 2133 |
| Kirin-Tool Guide / Telegram Support / GitHub | axaml:1203-1205 | 2720,2726,2731 | `Process.Start(UseShellExecute=true)` 打开外链 | 否 | 否 | 是 | 无（外链） |

### 2.8 对话框与全局交互（无按钮但全是黑盒判据来源）

| 组件 | 位置 | 作用 | 机器可读输出 |
|---|---|---|---|
| `ShowMessageBox` | 2677 → DialogService.cs:80 | 所有成功/失败结论的终点 | 无（仅屏幕） |
| `ShowGlobalInteractionPromptAsync` / `GlobalInteractionDone_Click` 2756 | 2735 + axaml:1215-1250 | VCOM/USB Update 需要人工按电源键/拔插线时阻塞等待 | 无（必须人点） |
| `ProcessDialogUapp` | UI/ProcessDialogUapp.axaml.cs:1-438 | 逐分区进度 + 关闭=取消 | 无（关闭即丢弃） |
| `FrpStepDialog`/`EnableDowngradeStepDialog`/`ProgressDialog` | UI/*.axaml.cs | 步骤状态展示 | 无 |

DONE-2

## 3. 黑盒清单

共 **36 项**（B1 no-cli/no-mcp 13 项、B2 hidden-step 16 项、B3 ui-only-result 4 项、B4 bypass-service 3 项；
个别项同时命中多类，按"最要紧的那类"归类）。每条一行：ID · 判据类 · 证据 · 一句话说明。

### 3.1 B1 —— 副作用在 CLI/MCP 完全不可达（最严重：不可达的**设备写**）

| ID | 动作 | 证据（文件:行） | 说明 |
|---|---|---|---|
| BB-01 | VCOM 解锁 fastboot（真上传 BootROM loader） | MainWindow.axaml.cs:1494-1532；Services/FirmwareUnlocker.cs:67-140；Cli/Commands/WriteCommands.cs:598 `"not invoked in this build"`、:19 `WriteKind => CliWriteKind.PlanOnly` | CLI/MCP 的 `vcom-unlock` 只打印计划，**永不上传**；真实注入只有 GUI 能发起 |
| BB-02 | 解锁 bootloader `oem unlock UUUU…`（16×U 硬编码） | Services/FastbootClient.cs:190-291；MainWindow.axaml.cs:2265-2338 | CLI 无任何 unlock 命令；且该调用连 `ProcessRunner` 都不走 |
| BB-03 | Remove FRP：`erase frp`+`erase config`+`oem frp-erase` | Services/FastbootClient.cs:115-131；MainWindow.axaml.cs:1762-1810 | CLI/MCP 无 frp 命令，破坏性擦除只有 GUI 可达 |
| BB-04 | Enable Downgrade：4×`oem oeminfoerase-*ver` | Services/FastbootClient.cs:133-155；MainWindow.axaml.cs:1825-1880；WriteCommands.cs:730-734 | `downgrade-enable` 为 plan-only |
| BB-05 | OEMInfo 直写：`oeminfoerase-disablewp`+`oeminfowrite-KTModel@/KTVendor@` | MainWindow.axaml.cs:2384、2391、2412 | 8 款芯片走这条道；`oeminfo` CLI 只做本地转换计划，连这条道都不提 |
| BB-06 | 写 NVE 序列号（`getvar nve:SN@<值>`，写伪装成读） | Services/FastbootClient.cs:104-107；MainWindow.axaml.cs:2093-2141 | 不可达：`getvar` 变量名白名单禁 `@`（ReadCommands.cs:324-326），无写 SN 命令 |
| BB-07 | Reboot to USB Update（rescue 分区写 + 模式切换） | MainWindow.axaml.cs:1890-2077 | CLI/MCP 零对应；含 `/tmp` 解包 + 30s 轮询 |
| BB-08 | Software Testpoint Enter/Exit（改写 XLOADER + 串口传输） | Services/USBUpdate/SoftwareTestpointService.cs:44-160；MainWindow.axaml.cs:1536-1596 | 零对应；`File.Copy/File.Delete` 就地改用户解包结果 |
| BB-09 | USB Update 模式刷写整包 | Services/USBUpdate/USBUpdateFlasherService.cs:91-260 | `flash-ota` 只走 fastboot，不覆盖 USB Update 通道 |
| BB-10 | 重启设备 `fastboot reboot` | Services/FastbootClient.cs:293-296；MainWindow.axaml.cs:2886 | CLI 无 reboot |
| BB-11 | 设备信息读取里的 oem 类查询 | MainWindow.axaml.cs:2783-2842（`lock-state info`/`oeminforead-*`/`get-build-number`）、ChipCapabilities.cs:130（`oem get-psid`） | CLI 只有 `getvar <name>`，没有 `oem <cmd>` 透传 → 这些事实读不到 |
| BB-12 | About 页三个外链按钮（发起浏览器进程+网络请求） | MainWindow.axaml.cs:2720-2734 | 零对应；`Process.Start{UseShellExecute=true}` |
| BB-13 | 写 `./dload`（**先递归删除**同名目录） | MainWindow.axaml.cs:151、157 传 `"dload"`；USBUpdateFlasherService.cs:93-97；SoftwareTestpointService.cs:46-50 | 相对 CWD 的固定路径，CLI 无等价物也无法指定 |

### 3.2 B2 —— 用户不会被告知的隐式步骤

| ID | 动作 | 证据 | 隐式步骤 |
|---|---|---|---|
| BB-14 | 所有 GUI 写操作前的 pre-op 备份 | 调用点 MainWindow.axaml.cs:612、1088、1270、1968；实现 Services/PreOpBackup.cs:164-345；ResolveBaseDirectory 见 :134-145 | 静默 dump ptable+redline 到 `~/.local/share/kirin-tool/backups/<ts>/`+manifest.json，结果只写进度框一行；失败不阻断；CLI 是显式参数（WriteCommands.cs:100-102） |
| BB-15 | Full OTA 在 UPDATE.APP **同目录**建 `temp_full_ota_<ts>` 全量解包 | MainWindow.axaml.cs:1276-1286、1411 | 可能数 GB 落在固件目录；正常结束 `Directory.Delete`，中断/崩溃则残留 |
| BB-16 | 启动目录下 `dload` 被静默删除 | USBUpdateFlasherService.cs:93-95；SoftwareTestpointService.cs:46-48 | 用户自己有个叫 dload 的目录就会被 `Directory.Delete(...,true)` |
| BB-17 | Read Info 的装饰性延迟 | MainWindow.axaml.cs:2791、2799、2807、2815、2823、2831（6×300ms） | 无功能理由的固定等待 |
| BB-18 | FRP/Downgrade 步骤动画延迟 | MainWindow.axaml.cs:1788、1792、1796；1836、1840、1844、1848（500ms×3/×4） | 让进度条看起来"在跑" |
| BB-19 | rescue 分区目标由 **CPU 下拉**决定，不读设备 | MainWindow.axaml.cs:1906 `RescuePartitionPlan.ForChip(GetSelectedCpu())` | 选错芯片即写错分区名，UI 不校验 |
| BB-20 | Convert 按钮按 CPU 静默二分支 | MainWindow.axaml.cs:2345-2348（判定）vs 2451+（文件转换道） | 同一按钮两套完全不同的副作用 |
| BB-21 | 解锁 BL 的 2s/5s 竞态 + "按 POWER 键"人工确认 | MainWindow.axaml.cs:2295、2299、2317 | 超时后弹窗要求人工按键，自动化无法完成 |
| BB-22 | 分区自动全选 | MainWindow.axaml.cs:267（dump）、362（flash）`SetSelectedPartitions()` | 用户未点选时默认全选，redline 靠后续确认兜底 |
| BB-23 | settings.json 静默读写；损坏即改名 | Services/UserSettings.cs:353（`settings.json.bad`）、366-370（tmp+rename）；写点 MainWindow.Settings.cs:48-51、67-69、77-86 | 无"保存设置"提示；CPU 选择跨会话沿用，直接改变后续写行为 |
| BB-24 | Browse 后后台解析并**覆盖**分区选择 | MainWindow.axaml.cs:761-786、223-234 | 静默重新计算 SelectedPartitions（经 `IsSkipPartition` 剔除） |
| BB-25 | USB Update 发现新 redline 分区时的第二次确认（后台阻塞 10 分钟） | MainWindow.axaml.cs:1743-1759 | 无人应答即静默取消整个刷写 |
| BB-26 | USB Update 连接重试 + **握手失败不算失败** | UsbDownloadService.cs:62-92（`while (port == null)` 无上限，只能靠人点 Cancel 退出）、:189-210（`DoHandshake` 3 次重试后**继续走协议**，原文 "continuing the existing protocol sequence"）、:721-740 | 无尝试上限、无超时；握手没拿到期望应答照样发刷写命令；成功后自动发 reboot 命令（:97-101），设备自己重启，UI 不预告 |
| BB-27 | hisi980/810/820/985/990 解锁中要求拔插线 | Services/FirmwareUnlocker.cs:116-140 | `Task.Delay(1000)` + 人工拔插 + `ReconnectAfterCableAsync` |
| BB-28 | 路径过长只在 >195 字符时 warn | MainWindow.axaml.cs:1127-1146 | 阈值 `dir.Length + 60 >= 255`，只 warn 不拦 |
| BB-29 | 电量 <50% 只提示不拦 | MainWindow.ChipCapabilities.cs:84-95（RefreshBatteryBannerAsync :75-101） | 注释明写 "never gates any write path"，靠人自觉 |

### 3.3 B3 —— 结论只活在 UI

| ID | 动作 | 证据 | 说明 |
|---|---|---|---|
| BB-30 | 所有写/读结论的落点 | MainWindow.axaml.cs:2677 `ShowMessageBox`、1532 `ShowCloseButton`、2224 `ShowUnlockOutcome`；无任何退出码/JSON | 唯一机读痕迹是 `log/kirintool_log_<date>.log`（Utils/ProcessRunner.cs:102-129），按天聚合、无 run id |
| BB-31 | USB Update 成功文案 | MainWindow.axaml.cs:1092；服务判据 USBUpdateFlasherService.cs:272-291 → UsbDownloadService.cs:94-127 | "成功" = `FlashImages()` 返回 true；没有 fastboot 那种 OKAY 回读，只有协议应答，且握手失败被吞（BB-26）→ 设备真实状态从未回读 |
| BB-32 | 离线 Extractor 结果 | MainWindow.Extractor.cs:155-160 → `ExtractorStatus` 文本 + 进度框 | 失败/取消只改文案 |
| BB-33 | 进度框"关闭=取消"语义 | UI/ProcessDialogUapp.axaml.cs（RequestClose 处理，MainWindow.axaml.cs:553、1023、1211、1921） | 关窗后 UI 不保留结果，无法二次读取 |

### 3.4 B4 —— 绕过 service 层

| ID | 动作 | 证据 | 说明 |
|---|---|---|---|
| BB-34 | `UnlockBootloaderAsync` 自建 Process | Services/FastbootClient.cs:190-291 | 不走 `ProcessRunner`，因此不写 `kirintool_log_*`（ProcessRunner.cs:96-99 只对含 "fastboot" 的可执行名写日志——这条恰好被绕过），是最无痕的设备写 |
| BB-35 | 外链 `Process.Start` 直接写在窗口类里 | MainWindow.axaml.cs:2722、2728、2733 | 无服务抽象，无法替换/记录 |
| BB-36 | GUI 直调底层（`FlashPartition`/`new UpdateApp`/`SuperMerger`/`File.Delete`） | MainWindow.axaml.cs:1365、2010、226、772、1305、1389、1411、1641 | 与 CLI 共享层（`OtaFlashRunner`）并行的第二套写路径，行为可能漂移 |

DONE-3

## 4. 副作用清单

按副作用落点归类。**"GUI-only"列 = 该副作用能否由 CLI/MCP 触发**。

### 4.1 设备写（按破坏性排序）

| 设备命令 | 由哪个 GUI 动作发出 | 发出点 | GUI-only? |
|---|---|---|---|
| `oem unlock UUUUUUUUUUUUUUUU` | Security▸Unlock | FastbootClient.cs:190-291 | **是** |
| `erase frp` / `erase config` / `oem frp-erase` | Security▸Remove FRP | FastbootClient.cs:115-131 | **是** |
| `oem oeminfoerase-{amssver,basever,custver,preloadver}` | Flash▸Enable Downgrade | FastbootClient.cs:133-155 | **是**（CLI plan-only） |
| `oem oeminfoerase-disablewp` | OEMInfo▸Convert（8 款芯片） | MainWindow.axaml.cs:2384 | **是** |
| `oem oeminfowrite-KTModel@…` / `-KTVendor@…` | OEMInfo▸Convert（8 款芯片） | MainWindow.axaml.cs:2391、2412 | **是** |
| `getvar nve:SN@<新值>` | NVMe▸Write SN | FastbootClient.cs:104-107 | **是** |
| `flash oeminfo …` | OEMInfo▸Convert → Flash Back | FastbootClient.cs:163-166 | **是**（CLI oeminfo 不刷） |
| VCOM BootROM loader 上传（`VcomFlasher.UploadData` 逐 stage） | VCOM▸Unlock | FirmwareUnlocker.cs:95-140 | **是**（CLI vcom-unlock 不上传） |
| USB Update 通道刷写（`list.txt` 驱动源码级协议） | Flash▸USB Update 模式 Start | USBUpdateFlasherService.cs:91-291 | **是** |
| XLOADER 补丁传输（进入软件测试点）/ 回写 | VCOM▸Software Testpoint Enter/Exit | SoftwareTestpointService.cs:44-160 | **是** |
| rescue 分区 `flash <target>` + `getvar rescue_*` | Flash▸Reboot to USB Update | MainWindow.axaml.cs:2007-2034 | **是** |
| `flash <partition> <img>`（XML 路径） | Flash▸Fastboot Flasher▸Flash | MainWindow.axaml.cs:1365（Full OTA）/ 651 附近（XML） | 否（`flash-xml`/`flash-ota` 覆盖） |
| `fastboot reboot` / `reboot-bootloader` | DeviceInfo▸Reboot；Reboot to USB Update | FastbootClient.cs:293-296、182-186 | 部分（`reboot-bootloader` 仅作为内部步骤；无独立命令） |
| 设备侧自动重启（USB Update 协议内建） | 每次 USB Update 刷写成功后 | UsbDownloadService.cs:97-101 | **是** |

### 4.2 本地文件写

| 路径 | 触发 | 证据 | 备注 |
|---|---|---|---|
| `~/.config/kirin-tool/settings.json`（+ `.tmp`、`.bad`） | 任何下拉/文件选择/文本框失焦 | UserSettings.cs:58-59、343-372；MainWindow.Settings.cs:44-90 | GUI-only（CLI 不读不写这份） |
| `~/.local/share/kirin-tool/backups/<yyyyMMdd-HHmmss>/manifest.json` + `*.img` | **每次** GUI 写操作前（4 条路径） | PreOpBackup.cs:134-145、164-345；MainWindow.axaml.cs:612、1088、1270、1968 | 隐式；CLI 需显式 `backup` 或 flash 的自动备份 |
| `./dload/`（相对 CWD，含 partitions/list.txt/partition_mapping.txt/unlockcode） | USB Update 刷写、软件测试点 | MainWindow.axaml.cs:151、157；USBUpdateFlasherService.cs:93-257；USBUpdateApp.cs:42-137 | **先递归删除**同名目录 |
| `<UPDATE.APP 目录>/temp_full_ota_<ts>/**` | Full OTA 刷写 | MainWindow.axaml.cs:1279-1285 | 全量解包（1276-1286），结束删除 |
| `<用户选定目录>/fastbootimage/*.img` + `flash.xml` | Fastboot Flasher▸Dump | MainWindow.axaml.cs:476-523 | dump 顺带生成 XML |
| `/tmp/kirintool_usb_update_<guid>/` | Reboot to USB Update | MainWindow.axaml.cs:1957 | 结束删除 |
| `<用户选定路径>`（转换后 oeminfo.img） | OEMInfo▸Convert 保存 | MainWindow.axaml.cs:2492 | 明文提示"未写设备" |
| `<选定目录>/<partition>.img` | Extractor▸Extract Selected | PartitionExtractor.cs:46-88 | 已存在则拒绝（:59-66），与 GUI 文案一致 |
| `log/kirintool_log_<yyyy_MM_dd>.log` 或 `$XDG_STATE_HOME/Kirin-Tool/log/` | 每次 `ProcessRunner` fastboot 调用 | ProcessRunner.cs:96-130 | GUI 与 CLI 共用；唯一事后可机读的痕迹 |
| `settings.json.bad`（损坏隔离） | 启动时解析失败 | UserSettings.cs:343-355 | 用户不会被告知 |

### 4.3 进程启动

| 进程 | 触发 | 证据 | 服务层? |
|---|---|---|---|
| bundled `fastboot/fastboot`（ProcessRunner，带超时+落日志） | 所有 fastboot 读写 | FastbootClient.cs:50-181；ProcessRunner.cs:36-93 | 是 |
| bundled `fastboot`（**裸 Process**，无日志、无统一超时上限） | Security▸Unlock BL | FastbootClient.cs:190-291 | **否** |
| 系统浏览器 / xdg-open | About▸3 个外链 | MainWindow.axaml.cs:2722、2728、2733 | **否** |
| 串口/USB 直连设备（不是子进程，但同样是外部 I/O） | VCOM Unlock、USB Update、软件测试点 | VcomFlasher.cs；UsbDownloadService.cs:130-160；LinuxSerialPortFinder.cs | 是（但 CLI 不可达） |

### 4.4 网络请求

| 请求 | 触发 | 证据 | 备注 |
|---|---|---|---|
| 打开 `kirintool.cfd` / `t.me/kirintoolsupport` / `github.com/Redminote11tech/kirin-tool-linux` | About 页 3 个按钮 | MainWindow.axaml.cs:2722、2728、2733 | 工具本身无 HTTP 客户端（`grep HttpClient` 无命中），网络只经外部浏览器 |
| （无）自动检查更新 / 遥测 | — | — | 确认：无自动网络行为，这一点是干净面 |

### 4.5 系统设置 / 外部状态

| 改动 | 触发 | 证据 | 备注 |
|---|---|---|---|
| 剪贴板写入（udev 命令） | Welcome▸Copy commands | MainWindow.UdevNotice.cs:26-34 | GUI-only |
| 读 `/etc/udev/rules.d` 等 5 个目录 | 打开/激活窗口（`Opened`/`Activated`） | MainWindow.axaml.cs:166-167；UdevRuleNotice.cs:12-48 | 只读 + 只判断 `KIRIN_APPIMAGE=1` 时显示 |
| udev 规则安装 | **不自动做**（只给命令让用户自己跑） | UdevRuleNotice.cs:24-28 | 干净点 |

DONE-4

## 5. GUI 动作 × CLI 命令 × MCP 工具 对照

CLI/MCP 现有全部命令（`Cli/CliRunner.cs:10` 注册，`Cli/Mcp/McpServer.cs:133-192` 逐个导出为 MCP 工具）：
`info`、`doctor`、`getvar`、`extract`、`dump`、`backup`、`flash-ota`/`flash_ota`、`flash-xml`/`flash_xml`、
`vcom-unlock`/`vcom_unlock`、`oeminfo`、`downgrade-enable`/`downgrade_enable`（共 11 个）。
其中后 3 个是 **plan-only 桩**：`GatedWriteCommand.WriteKind => CliWriteKind.PlanOnly`（WriteCommands.cs:19-24、:44-76），
acknowledged 也**什么都不写**。

gap 类型：`no-cli`（CLI 无此副作用；MCP 同样无）、`no-mcp`（CLI 有、MCP 被砍）、`cli-differs`（有命令但语义/副作用不同）、
`ui-only-result`（副作用可达但结论只在 UI）、`ok`。

| GUI 动作 | CLI 命令 | MCP 工具 | gap |
|---|---|---|---|
| DeviceInfo▸Read Info（8 条 fastboot 读） | `getvar <name>`（仅 getvar 类） | `getvar` | `no-cli`（`oem lock-state info`/`oeminforead-*`/`get-build-number` 无 `oem` 透传）+ `ui-only-result` |
| DeviceInfo▸Read SN / PSID | `getvar`（`oem get-psid` 无对应） | `getvar` | `no-cli` + `ui-only-result` |
| DeviceInfo▸Reboot | —（无 reboot） | — | `no-cli` + `ui-only-result` |
| VCOM▸Unlock（loader 注入） | `vcom-unlock --cpu`（**plan-only**） | `vcom_unlock`（plan-only） | `cli-differs`（桩 vs 真写）+ `ui-only-result` |
| VCOM▸Software Testpoint Enter/Exit | — | — | `no-cli` + `ui-only-result` |
| Flash▸Full OTA（fastboot 模式）Start | `flash-ota <files> --yes` | `flash_ota`（confirm:true） | `cli-differs`：GUI 的 pre-op backup 不可关闭；GUI 逐文件裁剪分区；暂存目录落点不同（GUI：UPDATE.APP 同目录；CLI：--work-dir 或 .kirintool-cli-tmp） |
| Flash▸USB Update 模式 Start | — | — | `no-cli` + `ui-only-result` |
| Flash▸Enable Downgrade | `downgrade-enable`（plan-only） | `downgrade_enable`（plan-only） | `cli-differs` + `ui-only-result` |
| Flash▸Reboot to USB Update | — | — | `no-cli` + `ui-only-result` |
| Flash▸Fastboot Flasher▸Dump | `dump <partition> [output]`（单分区、不生成 XML） | `dump` | `cli-differs`（GUI：整表多选 + fastbootimage/ 子目录 + 生成 flash.xml；CLI：单分区直落指定目录） |
| Flash▸Fastboot Flasher▸Flash（XML） | `flash-xml <path> --yes [--allow-redline]` | `flash_xml` | `cli-differs`：GUI 可写 redline（弹窗确认），**MCP 直接拒绝** allow-redline（McpServer.cs:147-148、:261-262）→ 该子集 `no-mcp` |
| Security▸Remove FRP | — | — | `no-cli` + `ui-only-result` |
| Security▸Unlock bootloader | — | — | `no-cli` + `ui-only-result` |
| Extractor▸Extract Selected | `extract <app> [outdir]` | `extract` | `ok`（同一 PartitionExtractor；GUI 多了逐分区勾选与"目标已存在即中止"的前置校验） |
| OEMInfo▸Convert（8 款芯片直写） | —（`oeminfo` 只覆盖文件转换计划） | `oeminfo`（plan-only） | `no-cli` + `cli-differs` |
| OEMInfo▸Convert（文件转换 + Flash Back） | `oeminfo --model --region (--input 或 --pull)` | `oeminfo` | `cli-differs`（CLI 转换后即止，永不 `flash oeminfo`；GUI 问一次就写） |
| NVMe▸Read NVE SN | `getvar nve:SN` | `getvar` | `ok`（GUI 侧结论仍 `ui-only-result`） |
| NVMe▸Write SN | —（`getvar` 的变量名白名单禁 `@`，读写通道被彻底切断，ReadCommands.cs:324-326） | — | `no-cli`（GUI 用 `getvar nve:SN@v` 这种写法绕过了"getvar 只读"的语义） |
| 所有 Browse / Clear（6+2 个按钮） | — | — | `no-cli`（settings.json 持久化仅 GUI） |
| 隐式 pre-op backup | `backup [partitions] --backup-dir`；flash 内置自动备份（--no-backup / --require-backup） | `backup` | `cli-differs`（GUI 恒开且不可关、结果只进进度框） |
| Welcome▸udev Copy / Check again | — | — | `no-cli`（只读检查 + 剪贴板） |
| About▸Guide / Telegram / GitHub | — | — | `no-cli`（外链/网络仅 GUI） |
| （所有写路径的结论） | 退出码 + `--json` | `_meta.gate` + structuredContent（McpServer.cs:300-345） | GUI 全部 `ui-only-result` |

**统计**：21 个动作族 —— `no-cli` 12、`cli-differs` 6、`ok` 2、`no-mcp` 1（作为 flash-xml 的子集缺口）。

DONE-5

## 6. 结论：TOP 10（按修复代价从低到高）

排序依据 = 改动行数 + 需要新抽象的程度。每条只给方向，**未实现**。

| # | 黑盒项 | 代价 | 一句话修复建议 |
|---|---|---|---|
| 1 | **BB-17/BB-18** 装饰性 `Task.Delay(300)`×6、`Task.Delay(500)`×3/×4（MainWindow.axaml.cs:2791-2831、1788-1796、1836-1848） | 极低 | 删掉固定等待，改为按"上一条命令已返回"驱动步骤状态——这些延迟现在纯粹让 GUI 与实际时序脱钩。 |
| 2 | **BB-34** 解锁 BL 的裸 `Process`（FastbootClient.cs:190-291） | 极低 | 让它走 `ProcessRunner`（拿统一超时 + `kirintool_log_*`），或至少自己调一次 `ProcessRunner.WriteDiagnostic`——当前这是唯一无日志的设备写。 |
| 3 | **BB-16/BB-13** `"dload"` 相对 CWD 且开跑先递归删除（MainWindow.axaml.cs:151、157；USBUpdateFlasherService.cs:93-95） | 低 | 换成 `$XDG_CACHE_HOME/kirin-tool/dload-<runid>`，删除前要求目录内有本工具写的 sentinel 文件。 |
| 4 | **BB-11** `oem <cmd>`/get-psid 类读取无 CLI 透传（MainWindow.axaml.cs:2783-2842、ChipCapabilities.cs:130） | 低 | 加一个只读 `oem` 命令（白名单子命令 + `--json`）并导出为 MCP `oem`，一次覆盖 Read Info / Read SN-PSID 的 no-cli。 |
| 5 | **BB-06** 写 SN 伪装成 `getvar nve:SN@v`（FastbootClient.cs:104-107） | 低 | 新增 `write-sn --value … --yes`（RealWrite + confirm），并在 `getvar` 帮助里明确它只读。 |
| 6 | **BB-02/BB-03/BB-10** reboot / remove-frp / unlock-bootloader 三个写动作零命令（FastbootClient.cs:115-131、190-291、293-296） | 低-中 | 各加一个 RealWrite 命令复用现有方法（`--yes`/confirm:true + `--json`），MCP 侧沿用 destructiveHint 即可，不必新写服务。 |
| 7 | **BB-14** 隐式 pre-op backup（MainWindow.axaml.cs:612、1088、1270、1968） | 中 | GUI 侧把它变成可见项：刷写前一条明确确认 + 结束后给备份目录可点路径 + 允许"跳过"（等价 CLI 的 --no-backup），别只埋在进度框一行小字。 |
| 8 | **BB-19** rescue 目标分区由 CPU 下拉决定（MainWindow.axaml.cs:1906） | 中 | 先用 `getvar product`/`oem lock-state info` 读回真实平台再选 rescue 表，读不出就拒绝而不是按用户下拉猜。 |
| 9 | **BB-01/BB-04/BB-05** 三个 CLI/MCP 桩（vcom-unlock、downgrade-enable、oeminfo）与 GUI 真写不一致（WriteCommands.cs:19-24、598、734；MainWindow.axaml.cs:1494、1825、2384-2412） | 中-高 | 把 `GatedWriteCommand` 的 PlanOnly 换成可执行的 RealWrite 分支，直接复用 `FirmwareUnlocker`/`EnableDowngradeWithSteps`/`OemInfoService`，先做"非交互"子集（无拔插线分支则明确报 unsupported）。 |
| 10 | **BB-07/BB-08/BB-09/BB-26/BB-31** USB Update / 软件测试点 / rescue 三条整通道（USBUpdateFlasherService、SoftwareTestpointService、MainWindow.axaml.cs:1890-2077） | 高 | 各补一个命令（`usb-update`/`sw-testpoint enter|exit`/`reboot-usb-update`），把 `OnRetryRequired` 的人机弹窗换成 `--retry N --timeout`，并把"握手失败仍继续"改成可失败项；这组是唯一连机读入口都没有的设备写。 |

补充两点（非 TOP10，但审计结论里必须写明）：

- **唯一机读痕迹**：`log/kirintool_log_<yyyy_MM_dd>.log`（ProcessRunner.cs:96-130）拿不到 run 边界与退出码，BB-30 才是最贵的总账项——它是上面 10 项之外、贯穿全部 GUI 动作的系统性缺陷，单独修要引入"每次操作一个 run 记录（含 gate/结果/exit code/JSON）"。
- **干净面（值得写进文档）**：工具本体无 HTTP 客户端（`grep HttpClient/WebClient` 零命中），不自动装 udev 规则、不自动检查更新、不写系统设置；网络只发生在用户点 About 外链时经系统浏览器。电量提示、Kirin9000 限制、路径过长、FRP/降级"OKAY≠成功"的措辞都已经写得很克制——黑盒集中在**动作可达性**（no-cli）与**结论可机读性**（ui-only-result），而不是在隐瞒风险。

DONE-6
