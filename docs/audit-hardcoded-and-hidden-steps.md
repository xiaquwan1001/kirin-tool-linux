# 审计 lane B：硬编码与隐藏步骤（kirin-tool-linux @ 35c6f6b）

> 只读审计。快照 `~/src/wt/ktl-audit`，detached 于 `35c6f6b`。
> 每条记录带 `文件:行号`；不贴大段源码。行号以该 commit 为准。

## 方法

1. 全仓 `grep -n` 扫 magic 常量（十六进制字面量、超时/延迟数值、`0x` 地址、`Task.Delay`、`TimeSpan`、`const`）与路径假设（`AppContext.BaseDirectory`、`AppDomain.CurrentDomain.BaseDirectory`、`/usr/bin`、`COM`、`loaders`、`fastboot`）。
2. 单点通读关键链路源码：`Services/FirmwareUnlocker.cs`、`Services/VcomFlasher.cs`、`Services/FastbootClient.cs`、`Utils/ProcessRunner.cs`、`Cli/BinaryLocator.cs`、`Services/ChipCapabilities.cs`，以及 GUI/CLI 调用点。
3. 静默降级：`catch {}`、`catch (Exception) {}`、`?? false`、`return null` 的失败吞噬点；被注释掉的校验；`Skipped` 状态；`Task.Delay` 掩盖竞态。
4. 假成功：以「返回值/文案是否能被设备侧独立确认」为判据，逐条判 `IsSuccess/true` 的语义。
5. 平台假设：`OperatingSystem.IsWindows()/IsLinux()` 分支、COM 命名、`option` 驱动特判、外部二进制版本假设。
6. 优先级：按「会不会让 AI/用户误判成功或误操作」排序 TOP 15。

判据约定（本报告统一使用）：
- **硬编码（H）**：字面量数值/路径/名单写死在代码里，改动需改源码。
- **静默降级（S）**：失败被吞、没有上报给上层或用户，流程继续。
- **假成功（F）**：返回成功/弹成功文案，但没有设备侧独立确认。
- **平台假设（P）**：仅在某一 OS/驱动/二进制版本组合下成立。

（本节 DONE-1）

## 2. 硬编码表

分级：`来源` 列 —— **厂商**=厂商表/协议文档导出，**协议**=BootROM/UPDATE.APP 线格式固定值，**策略**=本仓库自定阈值，**路径**=部署布局假设。
`可外置` 列 —— **Y**=可移到配置文件/参数，**Y(需证据)**=能外置但必须先有厂商表出处，**N**=协议固定值（外置反而危险）。

### 2.1 FirmwareUnlocker（SoC 地址表 / loader 布局）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H01 | `Services/FirmwareUnlocker.cs:41-61` | 17 个 SoC key（hisi65x_a…hisi990）+ 每 SoC 的 2–5 段 loader 名/加载地址/Pwn 标志 | BootROM 下载地址表；`PwnFlag` 决定是否走漏洞利用分支 | 厂商 | Y(需证据)：外置为 `loaders/<chip>/addresses.json`，代码只读表 |
| H02 | `FirmwareUnlocker.cs:52-60` | stage 名 `"null"`（对应文件名 `null.ktl`） | 真正的「null 段」占位，同时是「等用户拔插线」的触发条件 | 厂商 | Y |
| H03 | `FirmwareUnlocker.cs:51` | `0x6A908000`(hisi960 UCE) | UCE 段加载地址 | 厂商 | Y(需证据) |
| H04 | `FirmwareUnlocker.cs:53` | `unchecked((int)0x1A400000)` | hisi980 FASTBOOT 地址（>0x7FFFFFFF 用 int 承载，易被误写回负数） | 厂商 | Y |
| H05 | `FirmwareUnlocker.cs:54` | `0xF8000000`(hisik3v2 USBLOADER) | Kirin K3v2 USB 加载地址 | 厂商 | Y |
| H06 | `FirmwareUnlocker.cs:67` | `AppContext.BaseDirectory/loaders/<cpu>` | loader 目录布局 | 路径 | Y（加 `KIRIN_LOADERS_DIR`） |
| H07 | `FirmwareUnlocker.cs:86` | `${Name.ToLower()}.ktl` | 载荷文件命名约定（大写段名→小写文件） | 本仓 | Y |
| H08 | `FirmwareUnlocker.cs:89` | `fastbootf.ktl` | 「快速刷写」变体文件名（仅 hisi980 可选） | 本仓 | Y |
| H09 | `FirmwareUnlocker.cs:118`、`:138`、`:141` | `hisi980/hisi810/hisi820/hisi985/hisi990` 名单（出现 3 次） | 需要中途拔插线 / 需要换标准线 的 SoC 白名单 | 厂商 | Y：与 H01 合并成每 SoC 一个 `RequiresReplug` 字段 |
| H10 | `FirmwareUnlocker.cs:121` | `Task.Delay(1000)` | 「等待线操作」前的固定等待 | 策略 | Y |
| H11 | `FirmwareUnlocker.cs:130` | 文案 `"Waiting for COM 1.0 to reconnect..."` | Linux 分支复用 Windows 端口名文案 | — | Y（文案） |

### 2.2 VcomFlasher（BootROM 串口协议）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H12 | `Services/VcomFlasher.cs:35-36` | `IDT_VID=0x12D1`、`IDT_PID=0x3609` | VCOM(BootROM) USB 身份 | 协议 | N |
| H13 | `VcomFlasher.cs:37` | `MAX_DATA_LEN=0x400` | 单帧数据上限 1024 B | 协议 | Y（可作参数，需冒烟） |
| H14 | `VcomFlasher.cs:60` | `new SerialPort(portName, 115200)` | 波特率 115200（bulk 端点其实无关） | 协议 | Y |
| H15 | `VcomFlasher.cs:62-64` | `Handshake.None/DtrEnable=false` 仅当 `option` 驱动 | 见 §5 P02 | 平台 | Y（做成能力探测而非驱动名） |
| H16 | `VcomFlasher.cs:66-67` | `ReadTimeout/WriteTimeout=10000` | 串口层超时 10 s | 策略 | Y |
| H17 | `VcomFlasher.cs:88`、`:95` | `15` 秒重连截止、`250` ms 轮询 | 拔插后等 12d1:3609 重新枚举 | 策略 | Y |
| H18 | `VcomFlasher.cs:102` | `FE 00 FF 01 00 00 00 04 00 00 02 01 1D 0F` | START 帧（14 B，含 CRC） | 协议 | N |
| H19 | `VcomFlasher.cs:104` | `Task.Delay(50)` | START 后 50 ms | 策略 | Y |
| H20 | `VcomFlasher.cs:126-137` | `xloaderStart`：hisi710/970/980=`0x2316D`，hisi710a/810/820/985/990=`0x23155` | pwn 后跳转的 xloader 入口 | 厂商 | Y(需证据) |
| H21 | `VcomFlasher.cs:144` | `Task.Delay(500)` | 每段上传后 500 ms | 策略 | Y |
| H22 | `VcomFlasher.cs:149` | `BuildHeadCmd(0x22000, 4)` | 利用前把 4 B 写到 `0x22000` | 厂商 | Y(需证据) |
| H23 | `VcomFlasher.cs:152-161` | `returnAddr`：970/980/810=`0x4DBC8`，710/710a=`0x49BC8`，820/985/990=`0x673c8` | 漏洞返回地址（最高危字面量：错一个字节=变砖） | 厂商 | **Y 优先**：与 H01 同表 |
| H24 | `VcomFlasher.cs:169` | `CancellationTokenSource(50 ms)` | 排空残留响应 | 策略 | Y |
| H25 | `VcomFlasher.cs:180`、`:183` | `10` ms、`100` ms | 时序窗口 | 策略 | Y |
| H26 | `VcomFlasher.cs:193` | `CancellationTokenSource(10 s)` | 等 ACK 的超时 | 策略 | Y |
| H27 | `VcomFlasher.cs:196-199` | **整段 ACK 校验被注释**（`0xAA`/`0x55` 判断） | 见 §3 S01 | — | — |
| H28 | `VcomFlasher.cs:205`、`:216`、`:226` | 命令字 `0xFE`(head)/`0xDA`(data)/`0xED`(tail) | BootROM 命令字 | 协议 | N |
| H29 | `VcomFlasher.cs:251`、`:259` | WMI 查询串 `VID_%04X&PID_%04X`、`Split('(', ')')[1]` | Windows 端口名派生（`Split` 结果下标硬取，名字格式一变就返回 null） | 平台 | Y |

### 2.3 FastbootClient / FastbootReadResponses（外部二进制契约）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H30 | `Services/FastbootClient.cs:37` | `UnlockTimeoutMinutes=300` | `oem unlock` 最多等 5 小时 | 策略 | Y |
| H31 | `FastbootClient.cs:67`、`:72` | `BaseDirectory/fastboot/fastboot.exe` / `fastboot` | 随包 fastboot 布局（仓库 `fastboot/` 里**只有 payload，没有 fastboot 二进制**） | 路径 | Y |
| H32 | `FastbootClient.cs:78-79` | `/usr/bin/fastboot` → PATH 的 `fastboot` | 静默回落到系统 fastboot（Google platform-tools 对 Kirin `getvar` 全失败，见 §5 P08） | 平台 | Y：应显式拒绝或打警告 |
| H33 | `FastbootClient.cs:97`、`:102`、`:169` | `timeoutSeconds: 10` | 单次 getvar / get-psid 超时 | 策略 | Y |
| H34 | `FastbootClient.cs:106` | `getvar nve:<var>@<value>` | **写** NVE 变量用的是 `getvar` 语法 | 厂商 | N（但应在 schema 里注明是写） |
| H35 | `FastbootClient.cs:118/122/126` | `erase frp`、`erase config`、`oem frp-erase` | FRP 三步序列 | 厂商 | Y |
| H36 | `FastbootClient.cs:138-150` | `oem oeminfoerase-amssver/basever/custver/preloadver` | 降级使能四步 | 厂商 | Y |
| H37 | `FastbootClient.cs:159` | `oem dump-emmc oeminfo "<path>"` | oeminfo 拉取法 | 厂商 | Y |
| H38 | `FastbootClient.cs:173`、`:179` | `timeoutMinutes: 300` 默认 | 所有 OEM/裸命令默认 5 h（比 dump 的 60 s 预算大 300 倍） | 策略 | Y |
| H39 | `FastbootClient.cs:195` | `oem unlock UUUUUUUUUUUUUUUU` | 16×`U` 解锁口令（厂商固定 magic） | 厂商 | N |
| H40 | `FastbootClient.cs:289` | `(process.ExitCode == 0, output)` | 解锁判据只看退出码 | 策略 | — |
| H41 | `Services/FastbootReadResponses.cs:47` | `^(SN\|IMEI\|IMEI1\|MEID\|PSID)\s*:\s*(\S+)$` | get-psid 解析白名单（少一个键=丢一个标识符） | 厂商 | Y |
| H42 | `FastbootReadResponses.cs:83` | `\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z` | 分区名合法字符集 | 协议 | Y |
| H43 | `FastbootReadResponses.cs:15`、`:20-21` | `"(bootloader)"` 前缀、`"FAILED"` 标记、`"invalid command"` | 输出格式白名单（客户端变体一变就全挂） | 协议 | Y（改成可配置 shim） |

### 2.4 ChipCapabilities / Rescue（能力矩阵是否硬编码）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H44 | `Services/ChipCapabilities.cs:6` | `Kirin9000="hisi9000"` | 唯一被特殊对待的 SoC | 策略 | Y（应改能力表） |
| H45 | `ChipCapabilities.cs:8-9` | 允许操作白名单 `read-info/read-sn/read-psid/reboot-usb-update` | **能力矩阵本身是硬编码的「不是 9000 就全允许」**（默认放行） | 策略 | **Y 优先**：改显式 capability 表，未知 SoC 默认拒绝 |
| H46 | `ChipCapabilities.cs:15-17` | `ERECOVERY`/`ERECOVERY_KERNEL`/`ERECOVERY_RAMDISK`/`ERECOVERY_VENDOR` → `rescue_recovery*` | 救援镜像→分区名映射（按 SoC 二分） | 厂商 | Y |
| H47 | `Services/OemInfoEditor.cs:37-42` | 版本 6/8/9 → model id `0x5b,0x61` / `0x5ee,0x5ef` / `0x301,0x300`，region id `0x12`/`0x5de`/`0x2f0` | OEMINFO 条目 ID 表 | 厂商 | Y(需证据) |
| H48 | `OemInfoEditor.cs:30-32` | `BlockSize=0x400`、`DataPayloadOffsetInBlock=0x200`、magic `"OEM_INFO"` | OEMINFO 块布局 | 厂商 | Y(需证据) |
| H49 | `Cli/Commands/WriteCommands.cs:711-712` | 再次硬编码 `"OEM_INFO"` 与 `0x400`（复制自 H48） | 版本探测的重复实现（未复用 OemInfoEditor） | 本仓 | Y（去重） |

### 2.5 loader / fastboot / 路径与外部二进制

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H50 | `Cli/CommandSupport.cs:198` | `BaseDirectory/loaders` | CLI 侧 loader 目录（与 H06 重复定义） | 路径 | Y |
| H51 | `CommandSupport.cs:216-229` | `<stage>.ktl` / `fastbootf.ktl` | 计划阶段文件命名（与 H07/H08 重复） | 路径 | Y |
| H52 | `Cli/BinaryLocator.cs:9-10` | `KIRIN_FASTBOOT`、`KIRIN_ADB` | 唯一的二进制覆盖入口（GUI 不认这两个变量） | 本仓 | — |
| H53 | `BinaryLocator.cs:38-48` | 候选顺序：BaseDir → BaseDir/<name> → BaseDir/tools → `../../../../../` → `../Kirin-Tool` → `/usr/bin` → `/usr/local/bin` | **5 级相对路径回溯**是典型的「开发机可达、打包后不可达」假设 | 路径 | Y |
| H54 | `BinaryLocator.cs:27` | `.exe` 后缀按 OS 拼 | 平台分支 | 平台 | N |
| H55 | `Services/USBUpdate/XloaderPatcher.cs:31` | `AppDomain.CurrentDomain.BaseDirectory/fastboot` | payload 目录 | 路径 | Y |
| H56 | `XloaderPatcher.cs:45` | 文件名 `"payload"` | CRC 修正块（仓库内 `fastboot/payload` = 32768 B） | 厂商 | Y |
| H57 | `XloaderPatcher.cs:55`、`:60`、`:65`、`:71`、`:112-113` | `0x8000`（出现 6 次） | XLOADER 头部 32 KB 是 XOR/patch 的作用域 | 厂商 | Y(需证据) |
| H58 | `XloaderPatcher.cs:92` | `headerData.Length < 92` | img.header 最小长度 | 厂商 | Y |
| H59 | `XloaderPatcher.cs:97-105` | 分区名改写成 `"PRELOADER"`、偏移 `60`、抹 `32` B | 把 XLOADER 伪装成 PRELOADER（软件测试点关键动作） | 厂商 | Y(需证据) |
| H60 | `Utils/ProcessRunner.cs:36,43,62` | 默认 `timeoutMinutes: 300` | 全局默认超时；`timeoutSeconds>0` 才覆盖 | 策略 | Y |
| H61 | `ProcessRunner.cs:98` | `executablePath.Contains("fastboot")` 才写日志 | 诊断日志按文件名启发式开关 | 策略 | Y |
| H62 | `ProcessRunner.cs:106`、`:114-120` | `kirintool_log_yyyy_MM_dd.log`、`<exe>/log` → `$XDG_STATE_HOME/Kirin-Tool/log` | 日志位置 | 路径 | Y |
| H63 | `Services/PreOpBackup.cs:70`、`:77` | 总预算 60 s、单分区 10 s | 备份预算（比 H38 的 300 min 小 3 个数量级） | 策略 | Y |
| H64 | `PreOpBackup.cs:95-99` | 默认身份分区集 `ptable,oeminfo,nvm,modemst1,modemst2,fsg,persist,modemnvm_1,modemnvm_2` | 备份集合（与 FlashPolicy 红名单不同源） | 策略 | Y |
| H65 | `PreOpBackup.cs:143-144` | `LocalApplicationData/kirin-tool/backups`；`KIRIN_BACKUP_DIR` | 备份根目录 | 路径 | — |
| H66 | `PreOpBackup.cs:216` | 连续 `2` 次失败即放弃剩余分区 | loader 能力探测阈值 | 策略 | Y |

### 2.6 FlashPolicy / UPDATE.APP 解析（厂商包格式）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H67 | `Utils/FlashPolicy.cs:28-33` | 跳过子串 `sha256rsa, crc, curver, verlist, package_type, base_verlist, base_ver, ptable_cust, cust_verlist, cust_ver, preload_verlist, preload_ver, ptable_preload` | 包内元数据条目 | 厂商 | Y |
| H68 | `FlashPolicy.cs:46-50` | 红名单子串 `oeminfo,nvme,modem_secure,persist,modemnvm,certification,hisee_encos,veritykey` | 设备身份/校准/密钥分区 | 策略 | Y |
| H69 | `FlashPolicy.cs:56-59` | 精确名 `nvm,modemst1,modemst2,fsg` | 同上（精确匹配以免误伤 `nvme`） | 策略 | Y |
| H70 | `Utils/UpdateApp.cs:84-88` | magic `55 AA 5A A5`、扫描块 1 MB | UPDATE.APP 头 magic | 协议 | N |
| H71 | `UpdateApp.cs:140` | `headerSize < 98||>` | 头长度下界 | 厂商 | Y(需证据) |
| H72 | `UpdateApp.cs:174` | 分区名合法字符集 | 同 H42 | 协议 | Y |
| H73 | `UpdateApp.cs:229-246` | 名字别名表 `hisiufs_gpt→ptable, efi→ptable, ufsfw→ufs_fw, erecovery_ramdis→…, recovery_ramdis→…, vbmeta_hw_produc→…` | **截断名**（16 B 字段截断）映射 | 厂商 | Y(需证据) |
| H74 | `Cli/Commands/OtaFlashRunner.cs:51-59` | 同一别名表**第二份拷贝**（+ 注释「mirrors Utils/UpdateApp」） | 重复定义，改动易失配 | 本仓 | Y（去重） |
| H75 | `OtaFlashRunner.cs:338` | gzip magic `0x1f 0x8b` | BD 包 gz 载荷判定 | 协议 | N |
| H76 | `Utils/SparseDisk.cs:51-52` | `0xED26FF3A`、chunk header 12 B | sparse 镜像格式 | 协议 | N |

### 2.7 USB Update（用户态 bulk 协议）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H77 | `Services/USBUpdate/UsbDownloadService.cs:132-134` | 端口 `9600`、读写超时 `5000` | Windows 串口参数 | 协议 | Y |
| H78 | `UsbDownloadService.cs:153` | `vendorId: 0x12D1` | USB Update 端口 VID | 协议 | N |
| H79 | `UsbDownloadService.cs:189-190` | 握手重试 `3` 次、期望前缀 `7E 26 00 00 25 A7` | 握手判定 | 协议 | Y(需证据) |
| H80 | `UsbDownloadService.cs:331` | `tailTimeout = clamp(15 + MB/10, 35, 180)` s | 按镜像大小线性推的超时公式（经验式） | 策略 | Y |
| H81 | `UsbDownloadService.cs:347` | `blockSize = 0x20000` | 128 KB/块 | 协议 | Y |
| H82 | `UsbDownloadService.cs:412` | `timeout = clamp(MB*1.5, 1, 8)` s | 每块压缩后超时 | 策略 | Y |
| H83 | `UsbDownloadService.cs:481` | `Math.Min(0x10000, …)` | 单次写 64 KB | 协议 | Y |
| H84 | `UsbDownloadService.cs:587-595` | 转义 `0x7E`/`0x7D` → `0x7D 0x5E`/`0x7D 0x5D` | HDLC 风格转义 | 协议 | N |
| H85 | `UsbDownloadService.cs:610` | 19 B 握手帧 `26 00 00 25 A7 …01 00` | 握手命令字面量 | 协议 | N |
| H86 | `UsbDownloadService.cs:103,196,208,541,740` | `Sleep(50/200/10/50)` | 帧间固定延时 | 策略 | Y |
| H87 | `UsbDownloadService.cs:224,451,457,516,703,708` | 命令字节 `0x0B/0x41/0x43/0x0F/0x0A/0x32` 与期望应答 `7E 02 6A D3 7E` | 各命令字 | 协议 | N |
| H88 | `Services/USBUpdate/Crc16X25.cs:27,36` | 初值 `0xFFFF`、多项式 `0x8408` | CRC16-X25 | 协议 | N |

### 2.8 GUI 时序（`Task.Delay` 常量）

| # | 文件:行 | 值 | 语义 | 来源 | 可外置 |
|---|---|---|---|---|---|
| H89 | `MainWindow.axaml.cs:1758` | `Task.Delay(10 min)` | 交互弹窗等待上限（超时后静默走「否」分支） | 策略 | Y |
| H90 | `MainWindow.axaml.cs:1788-1796`、`:1836-1848` | 4–6 × `500` ms | FRP/降级步骤间的「演出」延时 | 策略 | Y |
| H91 | `MainWindow.axaml.cs:1939` | `for i<30` × `1000` ms | reboot-bootloader 后等设备 30 s | 策略 | Y |
| H92 | `MainWindow.axaml.cs:2295`、`:2317` | `2000`/`5000` ms | 解锁输出竞速窗口 | 策略 | Y |
| H93 | `MainWindow.axaml.cs:2376-2380` | `hisi980/810/820/985/990` | CPU 名单**第 4 份拷贝**（先于 `oeminfoerase-disablewp`） | 厂商 | Y（与 H01 合并） |
| H94 | `MainWindow.axaml.cs:2791-2831` | 6 × `300` ms | 设备信息逐项刷新之间 | 策略 | Y |
| H95 | `MainWindow.axaml.cs:2125`、`:2384` | `oeminfoerase-disablewp` 结果**未检查** | 见 §3 S07 | — | — |

合计 **95 条硬编码/布局假设**（其中 `来源=策略` 的可外置项占多数；厂商表项 H01/H20/H23/H47/H48/H57/H59 优先外置）。

（本节 DONE-2）


## 3. 静默降级 / 隐藏步骤

判据：失败不抛、不上报、不影响返回值，调用方或用户无法从输出察觉。

### 3.1 传输层：ACK 与握手（最高危）

| # | 文件:行 | 现象 | 后果 |
|---|---|---|---|
| S01 | `Services/VcomFlasher.cs:196-199` | **ACK 校验整段被注释**：读出 1 字节后既不判 `0xAA`/`0x55` 也不判 0 字节，直接返回 | BootROM 拒收/掉线时上位机继续灌下一帧，表现为「上传完成」 |
| S02 | `VcomFlasher.cs:186-201` | 方法名/参数 `expectAck` 假装校验，实际永不抛；`cts` 10 s 到期只抛 `OperationCanceledException`（被 §3.4 的 catch 混同） | 「验证」只存在于函数签名里 |
| S03 | `Services/USBUpdate/UsbDownloadService.cs:187-212` | `DoHandshake` 3 次都拿不到 `7E 26 00 00 25 A7` 时只 `_log(...continuing...)`，`void` 返回 | 握手失败被降级为一行日志，后续流程照走 |
| S04 | `UsbDownloadService.cs:100-108` | `if (success) {...} else { }` —— 空 else 分支 | 失败路径没有任何收尾（不重试、不清状态） |
| S05 | `UsbDownloadService.cs:102-104` | `SendCommand(port, CreateRebootCommand(), 0.3)` 与 `CreateForceRebootCommand` 的返回值**未接收** | 重启命令发没发、设备认不认都未知，但调用方已认为「刷完」 |
| S06 | `UsbDownloadService.cs:308-325` | list.txt 里缺 `.img`、缺 `.img.header`、或文件 0 字节 → `continue` | 分区被跳过但**不计失败**，`FlashPartitions` 仍返回 true |
| S07 | `UsbDownloadService.cs:402-406` | `if (bytesRead == 0) break;` 后 `return null`（= 成功） | 读到磁盘尾部就提前结束，镜像被截断写入却报成功 |
| S08 | `UsbDownloadService.cs:250-262` | `partition_mapping.txt` 行数少于 `list.txt` 时用默认源目录，序号错位不报 | 可能把 A 包的镜像写到 B 分区上 |
| S09 | `UsbDownloadService.cs:289-303` | 源 `.img` 不存在 / 0 字节 → `continue`（映射分支内） | 同 S06 |

### 3.2 门禁层：Gate 形同虚设

| # | 文件:行 | 现象 | 后果 |
|---|---|---|---|
| S10 | `Cli/Commands/WriteCommands.cs:806-811` | `RefusalExit`：`!result.Attempted` 直接返回 Ok | 备份目录创建失败时 `--require-backup` **静默失效**，写操作照常开始 |
| S11 | `Services/PreOpBackup.cs:163-175` | 目录创建失败 → `Attempted=false, Error=...`；只有 `RunForWriteAsync` 打一条 warning | 与 S10 叠加：最需要拦截的场景正好放行 |
| S12 | `Cli/ArgParser.cs:79-83` | 未登记为取值的选项一律当 `flag` 收下，未知选项**不报错** | `--require-backup`/`--strict-names` 拼错一个字母就静默关闭门禁；值型选项拼错则把值丢进位置参数 |
| S13 | `MainWindow.axaml.cs:611-612` | GUI 写前备份「best effort，never aborts」（`RunPreOpBackupAsync` 注释 :694-698 亦然） | GUI 侧**不存在**严格备份闸门，CLI 的 `--require-backup` 在 GUI 无对应物 |
| S14 | `MainWindow.ChipCapabilities.cs:11-14`、`:84-99` | 电量 <50% 只是横幅；读不到电量也只写「unavailable」 | 与「电量闸门」的预期相反：**任何写路径都不因电量被拒** |
| S15 | `Services/FirmwareUnlocker.cs:93-99` | loader `.ktl` 缺失 → `StatusText="Skipped"` + 继续 | 缺关键段仍能走到 `IsSuccess=true`（S16） |

### 3.3 结果层：true 但没有设备侧确认

| # | 文件:行 | 现象 | 后果 |
|---|---|---|---|
| S16 | `Services/FirmwareUnlocker.cs:151-152` | 只要 `foreach` 跑完就 `IsSuccess=true` | 见 §4 F01 |
| S17 | `MainWindow.axaml.cs:621-628` | `skipSecure` 时 `CompletePartition(..., true, "Skipped")` 且 `successCount++` | 跳过 = 成功；末尾文案仍说「Successfully flashed all N」 |
| S18 | `MainWindow.axaml.cs:2121-2125` | `writeResult.IsSuccess`（= `ExitCode==0`）直接弹「Successfully rewrote the device's serial number!」 | 与同文件 :2391-2422 的 OKAY 分类标准自相矛盾 |
| S19 | `MainWindow.axaml.cs:2384` | `await _fastbootClient.OemCommandAsync("oeminfoerase-disablewp")` 结果丢弃 | 写保护没解除，后续两步的 FAILED 会被归因到别处 |
| S20 | `Cli/Commands/WriteCommands.cs:546-559`、`:289-302` | JSON 里 `applied = true` 在 `flashed 0/N`（exit 5）时同样输出 | 只读 `applied` 的 AI 会认为「已写入」 |
| S21 | `Models/FrpBypassResult.cs:34`、`Models/EnableDowngradeResult.cs:37` | `OverallSuccess => 任一 Step 成功`（OR），且全仓**无调用者** | 未来误用即假成功；当前是死代码陷阱 |
| S22 | `Services/FastbootFlasherService.cs:92-102` | `DumpFileWasWritten` 只判 `Length > 0` | 被截断/半截 dump 也算 dump 成功（无 size 对比） |

### 3.4 catch 吞掉 / 靠默认值兜底

| # | 文件:行 | 现象 |
|---|---|---|
| S23 | `Utils/ProcessRunner.cs:96-140` | `WriteLog`/`WriteDiagnostic` 三层 catch 全空：诊断日志可能根本没写，且无人知道 |
| S24 | `Utils/ProcessRunner.cs:75-80` | `Output = output + error`：stdout/stderr 混流，`Contains("OKAY")` 无法区分是设备应答还是错误文本 |
| S25 | `Services/FastbootClient.cs:57-60` | `IsDeviceConnected` 捕获所有异常返回 false |
| S26 | `FastbootClient.cs:233-237`、`:262-265` | 两个读流线程的 `catch (Exception)` 空实现（注释称「退出码说了算」）→ 真正的输出被丢弃 |
| S27 | `Services/FastbootReadResponses.cs:18-28` | 只把 `FAILED` 当失败；非零退出但无 `FAILED` 时给的是泛化「检查连接」提示 |
| S28 | `Cli/CommandSupport.cs:57-93` | `TryReadAsync` 三连 catch → null：「变量不支持」「fastboot 坏了」「超时」三者不可区分 |
| S29 | `MainWindow.axaml.cs:2789,2797,2805,2813,2821,2829` | 6 处 `catch { ...Text = "Unknown"; }`：读取失败与「设备就是 Unknown」同形 |
| S30 | `MainWindow.axaml.cs:1658`、`:2076`、`:311`(`OtaFlashRunner.cs:311,318,348`) | 临时文件/目录清理 `catch { }`（可接受，但残留无提示） |
| S31 | `Services/UserSettings.cs:341-345`、`:348-359`、`:372-375` | 设置损坏 → 静默改名为 `settings.json.bad` 并用默认值；写盘失败静默 |
| S32 | `Services/OemInfoService.cs:107-119`、`App.axaml.cs:108-111`、`Services/USBUpdate/*.cs:95,48,123` | 清理/崩溃上报的 `catch { }` |
| S33 | `Utils/LinuxSerialPortFinder.cs:144-158` | sysfs 属性读取失败 → null（会把它当成「不是该设备」） |

### 3.5 用延时掩盖竞态

| # | 文件:行 | 现象 |
|---|---|---|
| S34 | `MainWindow.axaml.cs:1758` | `Task.WhenAny(answer.Task, Task.Delay(10min))`：超时后走 falsy 分支，等同用户点了「取消」，但界面没说是超时 |
| S35 | `MainWindow.axaml.cs:2295-2332` | 解锁前 2 s / 5 s 的 `Task.WhenAny` 竞速窗口：错过窗口就把部分输出当全量输出 |
| S36 | `MainWindow.axaml.cs:1939` / `MainWindow.axaml.cs:438,572,1113,1230,2062` | 轮询 `Task.Delay(1000)`/`(100)` 代替事件；`while (!dialogClosed) { await Task.Delay(100); ... }` |
| S37 | `Services/VcomFlasher.cs:104,144,150,180,183` / `UsbDownloadService.cs:103,196,208,541,740` | 帧间固定 `Delay/Sleep` 代替流控；在慢机/高负载下即为随机失败源 |
| S38 | `Services/PreOpBackup.cs:247-257` | `Task.WhenAny(dumpTask, Task.Delay(perPartitionTimeout))` 超时后**放弃但无法取消** `dumpTask`（孤儿 fastboot 子进程可能与后续写并发） |
| S39 | `Utils/ProcessRunner.cs:86-93` | 超时后 `Kill(entireProcessTree: true)` 再 `WaitForExit(3000)`；`WaitForExit` 返回值未检查 |

### 3.6 被注释掉 / 重复实现的校验

| # | 文件:行 | 现象 |
|---|---|---|
| S40 | `Services/VcomFlasher.cs:196-199` | 同 S01（唯一一处「校验代码被注释」） |
| S41 | `Services/FirmwareUnlocker.cs:146` | 交互文案被注释，逻辑分支仍在（§2 H09 的名单漂移来源） |
| S42 | `Cli/Commands/WriteCommands.cs:711-722` | `TryDetectOemInfoVersion` 自己再实现一遍 magic/0x400 扫描，`catch` 全吞返回 null（不复用 H48） |
| S43 | `Cli/Commands/OtaFlashRunner.cs:51-59` vs `Utils/UpdateApp.cs:229-246` | 别名表两份拷贝（S42 同类：知识重复而非共享） |

**未发现** `Environment.Exit` / `Application.Current.Shutdown`（全仓 0 处）。唯一的进程终止点是 `Services/FastbootClient.cs:275` `process.Kill()`（解锁超时后）。

（本节 DONE-3）


## 4. 假成功清单

**判据（本报告统一）**：返回 `true` / 打印成功文案，但**没有任何设备侧独立确认**（无 OKAY、无读回、无 size/CRC 对比、或确认标准低于同仓其他路径）。「设备侧事实导致的假象」单独标注，不算工具 bug。

| # | 位置 | 假成功内容 | 实际证据 | 判据 / 说明 |
|---|---|---|---|---|
| F01 | `MainWindow.axaml.cs:1532` + `Services/FirmwareUnlocker.cs:151-152` | 进度窗标题 **"Unlock Complete"** | 只有「所有 `.ktl` 上传函数的 `WriteAndVerify` 没抛异常」 | 上传 ≠ 已运行；无 `getvar product` 读回，且 ACK 校验被注释（S01）。对比同文件 :2240 的解锁路径要求 OKAY。**最高危文案** |
| F02 | `FirmwareUnlocker.cs:93-99` | 缺 `.ktl` 的段显示 `Skipped`，整体仍 `IsSuccess=true` | 无 | 少一段 loader 也能进「Unlock Complete」 |
| F03 | `MainWindow.axaml.cs:621-628,686-689` | 「Successfully flashed all N partitions!」 | `skipSecure` 的条目被记成 `CompletePartition(..., true, "Skipped")` 且计入 `successCount` | 跳过被计为成功；`successCount==totalCount` 因此永远成立 |
| F04 | `Services/FastbootClient.cs:104-107` + `MainWindow.axaml.cs:2121-2125` | 「Successfully rewrote the device's serial number!」 | `ExitCode==0` 而已 | 无 OKAY 判定、无 `getvar nve:SN` 读回；同文件 :2391-2422 的 model/vendor 写入却要求 OKAY → 标准不一致 |
| F05 | `Services/OemInfoService.cs:74-84` + `Services/OemInfoEditor.cs:76-92` | `IsSuccess=true`（"Conversion completed successfully!"） | `EditEntries` 若没有任何块命中 `targets.ModelIds/RegionId`，返回**未修改的克隆** | 无「命中条目数」校验；无 diff/校验和对比 |
| F06 | `MainWindow.axaml.cs:2505,2512` | "Conversion Complete / No Write"、"Write Not Confirmed" | — | **反例**：这里是已修正的诚实文案，可作为 F01/F04 的改写样板 |
| F07 | `Cli/Commands/WriteCommands.cs:289-302`、`:546-559` | JSON `applied: true, gate: "applied"` | `flashed 0/N`（exit 5）时同样输出 `applied: true` | 只读 `applied`/`gate` 的 AI 会误判为已写入 |
| F08 | `Cli/Commands/WriteCommands.cs:699-701` | `oeminfo` 计划行「conversion: succeeded locally」 | 纯内存转换 | 与设备无关却在「写计划」里读作成功 |
| F09 | `Services/FastbootFlasherService.cs:59-62`、`:92-102` | dump 返回成功 | `FileInfo.Length > 0` 且输出不含 "fail"/"error" | 截断文件也通过；无「写出字节数 == 分区大小」对比 |
| F10 | `Services/USBUpdate/UsbDownloadService.cs:377` + `USBUpdateFlasherService.cs:286-289` + `MainWindow.axaml.cs:1092` | 「USB Update flash completed successfully!」 | `FlashPartitions` 无异常即 `true`；缺 img/header 的分区被 `continue`（S06）；读到 0 字节即当成功（S07） | 跳过与截断都不产生失败 |
| F11 | `Services/USBUpdate/XloaderPatcher.cs:184-192` | `FlashXloaderViaFastbootAsync` 只要 `IsSuccess` 就正常返回 | `ExitCode==0` | GUI 在 `:1571-1590` 补了 `getvar product` 读回，但**服务层返回值本身是假的**，其它调用者会中招 |
| F12 | `MainWindow.axaml.cs:2156-2167` `ClassifyCommandOutput` | 任意包含 `OKAY` 或 `SUCCESS` 的子串 ⇒ `Succeeded` | 子串匹配整段混合输出（stdout+stderr 已混流，S24） | 提示语里的 "successfully" 之类即可命中；反之 `NOT ALLOWED` 也被 `FAIL`/`ERROR` 之外的词漏掉 |
| F13 | `Services/USBUpdate/UsbDownloadService.cs:211`（S03） | 握手失败仍继续 | 一行日志 | 若后续步骤碰巧不再校验，整条链「绿灯」 |
| F14 | `MainWindow.axaml.cs:1577-1579` | "XLOADER was flashed back and the device answers fastboot getvar product again (OKAY)" | `getvar product` 的 OKAY | **反例（诚实）**：但 `getvar product` 只证明 loader 活着，不证明 XLOADER 内容正确；文案已限定「answers again」，可接受 |
| F15 | `MainWindow.axaml.cs:1812,1816`（FRP） | 「All 3 FRP erase commands returned OKAY」 | OKAY | **已在文案里声明**「FRP / Find My Phone state itself is not verified」。但 `:1820` `ShowCloseButton(SuccessfulStepsCount == 3)` 仍给出绿色「FRP Bypass Complete」外观 |
| F16 | `Services/USBUpdate/SoftwareTestpointService.cs:44-117` | 方法正常返回（无异常） | `FlashImages()` 的 `true` | GUI `:1554` 已诚实写成「Transfer Completed - Mode Unknown」；但服务契约「不抛=成功」对其它调用者仍是假成功 |
| F17 | `Cli/Commands/ReadCommands.cs:191` | `doctor` 的 `ok = fastboot 找到 && 能跑` | 无设备、getvar 全失败时仍是 `ok:true`、exit 0 | AI 常把 `ok:true` 当「环境健康」 |
| F18 | `Cli/Commands/ReadCommands.cs:27-36,61-65` | `info` 返回 exit 0 且 `connected:true` | `variables` 可能为空（全部 getvar 被拒） | 「连上了但一无所知」与「连上且正常」同为 exit 0 |
| F19 | `MainWindow.axaml.cs:1927`（`isFbUnlocked`）、`Cli/Commands/ReadCommands.cs:29,139`（`unlocked` 变量） | 工具原样展示 `unlocked: yes` / `FB LockState: UNLOCKED` | 临时 BootROM loader 的假象 | **设备侧事实**（非工具 bug）：`oem get-bootinfo` 在临时 loader 上必报 unlocked。已知免责声明只存在于 `MainWindow.axaml.cs:2235` 一处；`info`/`getvar` 输出里**没有**这条注解 |
| F20 | `MainWindow.axaml.cs:2224-2262` `ShowUnlockOutcome` | — | 分类覆盖 Success / AlreadyUnlockedNoWrite / Failed / Unknown 四态 | **反例（设计良好）**：可作 F01 的参照实现 |
| F21 | `Utils/UpdateApp.cs:229-232,176-177` | 无名条目变成分区名 `"UNKNOWN"` | — | 会被当成真实分区名参与 `flash unknown`；非 strict 模式下静默丢弃条目（`InvalidEntry` 返回 null） |

（本节 DONE-4）


## 5. 平台假设

### 5.1 Windows / Linux 双实现

| # | 位置 | 假设 | 风险 |
|---|---|---|---|
| P01 | `Services/VcomFlasher.cs:233-246`、`:248-259` | Windows 用 WMI `Win32_PnPEntity` 查 `VID_12D1&PID_3609`，再 `Split('(', ')')[1]` 取 `COMx` | 设备名不合 `…(COM12)` 格式即静默返回 null；msg 会说「没有 VCOM 设备」而不是「端口名解析失败」 |
| P02 | `VcomFlasher.cs:49-54` | **Linux 只有内核驱动名恰好是 `option` 时才关掉 RTS/DTR** | 若端口由 `cdc_acm`/`usbserial`/其它驱动提供，则仍发 `SET_CONTROL_LINE_STATE`，12d1:3609 会 stall（仓库注释已记录该现象）→ 表现为「连上但一发就断」。判定依据是驱动名而非设备能力 |
| P03 | `VcomFlasher.cs:240-245` | 非 Windows 非 Linux 返回 `null` | macOS/BSD 上错误信息误导为「设备不在 VCOM 模式」 |
| P04 | `Services/USBUpdate/UsbDownloadService.cs:124-146` | Linux 走 `BufferedUsbTransport.Open()`（`native/usb-update` 用户态 bridge），Windows 走 9600 波特率串口 + HDLC | **同一协议两套传输实现**；Linux 侧没有 `SerialPort` 语义（无波特率/RTS），行为差异（超时、缓冲、部分读）无法互相验证 |
| P05 | `Utils/LinuxSerialPortFinder.cs:71-76` | 只认 `/dev/ttyUSB*`、`/dev/ttyACM*`、`/dev/ttyGS*` | 其它命名（`/dev/serial/by-id` 直连、`/dev/ttyHS*`）不被发现 |
| P06 | `LinuxSerialPortFinder.cs:57-69` | USB Update 接口 = `bInterfaceClass=ff`/`SubClass=13`/`Protocol=23`（对齐 Windows `hw_cdcacm.inf`） | 别的华为固件接口描述符一变就找不到端口，且诊断信息只说「未找到」 |
| P07 | `LinuxSerialPortFinder.cs:95-114` | `/sys/class/tty` 布局 + 向上找 `idVendor` 边界 | 容器/非常规 sysfs 挂载下失效 |
| P08 | `Services/UdevRuleNotice.cs:17-18`、`:32-96` | 只**静态读** udev 规则文本判断「权限覆盖」 | 规则存在 ≠ 当前会话可用：`TAG+="uaccess"` 依赖 logind 会话（SSH/无桌面会话拿不到 uaccess）。检查结果 `Complete=true` 时用户仍可能 `open` 失败 |
| P09 | `Services/UdevRuleNotice.cs:20-23` vs `packaging/51-kirin-tool-fastboot.rules:5,10` | 内置文案用 `MODE=0660` 无 GROUP；随包规则用 `GROUP="adbusers"`/`"uucp"`（deb 再 `sed` 成 `plugdev`） | 三份规则措辞不一致，用户照哪份都能对、也都能错 |
| P10 | `Services/UdevRuleNotice.cs:15-16` | AppImage 判定 = 环境变量 `KIRIN_APPIMAGE=1`（由 `packaging/AppRun:3` 设） | 直接跑 `./Kirin-Tool` 时分支不生效 |

### 5.2 外部二进制（fastboot / adb）版本假设

| # | 位置 | 假设 | 风险 |
|---|---|---|---|
| P11 | `Services/FastbootClient.cs:63-80` | 找不到随包 fastboot 时**静默**退回 `/usr/bin/fastboot` → PATH | 这是已知必坏路径：`packaging/build-deb.sh:139-140` 明确写「Google platform-tools 的 fastboot 会做 `getvar partition-size`，华为临时 loader 不实现」→ 所有读取失败，但错误文案只会说「连接问题」（`FastbootReadResponses.cs:23-26`） |
| P12 | `Cli/BinaryLocator.cs:36-49` | 候选链含 `../../../../../`（5 级回溯）与 `/usr/bin`、`/usr/local/bin` | 与 P11 同源：CLI 同样可能选中 Google 版；`doctor` 的 `fastboot-found/runs` 检查**不区分是不是 parity 版** |
| P13 | `Cli/Commands/ReadCommands.cs:143-156`、`:191` | `ok = 找到 && 能跑` | 版本/血统不校验（无 `--version` 内容匹配、无 `getvar` 探针作为判据） |
| P14 | `fastboot-modern/build.sh:8-14` | 随包 fastboot = AOSP `android-tools-35.0.2` 源码 + 本地 `prepare.py`/`huawei_vendor.cpp` 补丁，需联网 `curl` + SHA256 校验 | 构建需要 GCC≥10/C++20/CMake/Ninja/Python3/curl；离线或未跑构建时**仓库里没有 `fastboot/fastboot`**（只有 `fastboot/payload`）→ 必然落到 P11 |
| P15 | `packaging/publish.sh:14-33`（全文无 `loaders` 拷贝） vs `packaging/AppRun:3`、`packaging/build-deb.sh:5` | 打包脚本注释假定发布目录「contains Kirin-Tool, fastboot/, loaders/」 | `publish.sh` 实际**只产出 fastboot/**；`loaders/` 需人工补。VCOM 解锁在打包产物里会直接报 `Loader directory for <cpu> not found`（`FirmwareUnlocker.cs:70`） |
| P16 | `Cli/BinaryLocator.cs:77-89` | Linux 用 `UnixFileMode` 判可执行位 | AppImage 挂载/只读介质上 exFAT/FUSE 时可能返回不可执行（`catch → false`），候选被静默跳过 |

### 5.3 线程/UI 模型假设

| # | 位置 | 假设 | 风险 |
|---|---|---|---|
| P17 | `MainWindow.axaml.cs:1758` | `Task.WhenAny(...).GetAwaiter().GetResult()` 在 UI 线程同步等 | Avalonia UI 线程上阻塞等一个需要 UI 线程续体的 Task = 经典死锁窗口（10 min 后由 P18 式超时解开） |
| P18 | `Services/USBUpdate/USBUpdateFlasherService.cs:262-265`、`SoftwareTestpointService.cs:56-59` | `_onRetryRequired(...).GetAwaiter().GetResult()` 同步等 UI 回调 | 同上；且调用发生在 `Task.Run` 内（`:275`），能否续体取决于 dispatcher 是否 pump |
| P19 | `MainWindow.axaml.cs:2060-2065` | `while (!dialogClosed) { await Task.Delay(100); if (dialogTask.IsCompleted) break; }` | 用轮询代替「对话框关闭」事件 |
| P20 | `Services/FastbootClient.cs:268-278` | 超时后 `process.Kill()`（非 `entireProcessTree`，与 `ProcessRunner.cs:89` 的做法不一致） | 解锁子进程若再 fork fastboot 助手会残留 |

### 5.4 协议/环境固定值

| # | 位置 | 假设 | 风险 |
|---|---|---|---|
| P21 | `Cli/Mcp/McpServer.cs:14` | MCP 协议版本固定 `"2024-11-05"` | 客户端协商新版时降级行为未定义（不校验 client 版本） |
| P22 | `Cli/Mcp/McpServer.cs:121-130`、`:163-174` | real-write 工具「无 dry-run」；`confirm:true` 即真写 | 与 CLI 的「默认只打印计划」相反；AI 侧极易把 CLI 习惯带进 MCP |
| P23 | `Cli/Commands/ReadCommands.cs:29`、`:139` | 可读变量名清单写死 | 新 loader 支持的变量读不到，且不报「不支持」只报 null |
| P24 | `Services/PreOpBackup.cs:143-144` | `SpecialFolder.LocalApplicationData` = `~/.local/share` | 只读 HOME / 容器内 HOME 不可写时备份静默降级（S11+S10 叠加） |
| P25 | `Utils/ProcessRunner.cs:114-120` | 日志目录 `<exe>/log` 可写，否则 `$XDG_STATE_HOME` | 两者都不可写时日志**完全丢失**且无提示（S23） |
| P26 | `Services/VcomFlasher.cs:60`、`UsbDownloadService.cs:132-135` | 波特率 115200 / 9600 有效 | Linux bulk bridge 下波特率无意义，Windows 下若驱动实际速率不同则收包截断 |
| P27 | `MainWindow.ChipCapabilities.cs:26`、`UpdateApp`/`FastbootClient` 各处 | 输出文本前缀 `(bootloader)`、关键字 `OKAY/FAILED/INFO/Finished` 均为**英文原样** | loader 改输出语言或加前缀即失配（`ClassifyCommandOutput` F12 会退化成 Unknown） |

（本节 DONE-5）


## 6. 优先级 TOP 15（按「会不会让 AI/用户误判成功或误操作」排序）

| 序 | 条目 | 位置 | 为什么排这里 | 一句话修复方案（不实现） |
|---|---|---|---|---|
| 1 | S01/S02 | `Services/VcomFlasher.cs:186-201` | ACK 校验整段被注释：设备拒收/掉线时上位机照灌下一帧，整条 VCOM 链的「成功」没有任何设备侧证据 | 恢复 `0xAA/0x55` 校验并让 `WriteAndVerify` 在字节数 0 或值不符时抛错，先在小样本上重跑解锁 |
| 2 | H20/H23 | `Services/VcomFlasher.cs:126-137`、`:152-161` | pwn 入口与返回地址是**变砖级**字面量，藏在 `switch` 里与 `CpuAddresses` 分离，无法核对、无法审查 | 把 per-SoC 的 `{stage, loadAddr, pwnFlag, xloaderStart, returnAddr}` 合并成一张外置表（JSON），加载时校验 SoC 名与字段齐全 |
| 3 | F01 | `MainWindow.axaml.cs:1532` + `Services/FirmwareUnlocker.cs:151-152` | 绿色标题「Unlock Complete」只代表 loader 上传函数没抛异常；用户/AI 会据此认为 bootloader 已解锁 | 改为「Loader uploaded — unlock unconfirmed」，并在其后强制 `getvar product` 读回，按 `ClassifyCommandOutput` 三态显示 |
| 4 | F02/S15 | `Services/FirmwareUnlocker.cs:93-99` | 缺关键 `.ktl` 只标 `Skipped` 就继续，最终仍 `IsSuccess=true` | 缺段即 `IsSuccess=false`（附缺失文件名），与 #3 的文案联动 |
| 5 | F10/S06/S07 | `Services/USBUpdate/UsbDownloadService.cs:308-325`、`:402-406`、`:377` | USB Update 链路里「缺图/零字节跳过」「读到 0 字节当结束」都不计失败，GUI 仍报 completed successfully | 跳过与短读改为 `RecordFailure` 并让 `FlashPartitions` 返回 false；短读额外对比 `写出字节 == header 声明长度` |
| 6 | S10/S11 | `Cli/Commands/WriteCommands.cs:806-811`、`Services/PreOpBackup.cs:163-175` | `--require-backup` 在「备份目录创建失败」这个最需要拦截的场景正好放行 | `RefusalExit` 把 `!Attempted` 也判为拒绝（除非显式 `--no-backup`），并输出原因 |
| 7 | S12 | `Cli/ArgParser.cs:79-83` | 未知选项静默当 flag：`--require-backup`/`--strict-names` 拼错即关闭门禁，且不报错 | 解析器对「未在任何 `CliParam` 中登记」的选项一律 `CliUsageException`（exit 1） |
| 8 | F04/S18 | `MainWindow.axaml.cs:2121-2125` + `Services/FastbootClient.cs:104-107` | 写 NVE SN 只看退出码就弹「Successfully rewrote」，与同仓 model/vendor 写入的 OKAY 标准不一致 | 复用 `ClassifyCommandOutput`，并在写后 `getvar nve:SN` 读回比对 |
| 9 | F03/S17 | `MainWindow.axaml.cs:621-628`、`:686-689` | 被 skip/blocked 的分区计入 `successCount`，于是「all N partitions」永远成立 | `Skipped` 单列一个计数，成功文案只在 `flashed+skipped+blocked` 明确分列后给出 |
| 10 | F07 | `Cli/Commands/WriteCommands.cs:289-302`、`:546-559` | `applied:true` 在 `flashed 0/N`（exit 5）时也输出，只读 `applied` 的 AI 会认为已写入 | `applied` 改为 `exitCode==0 || 部分成功` 的真值，并新增 `confirmedWrites` 字段 |
| 11 | S03/S04/S05 | `Services/USBUpdate/UsbDownloadService.cs:187-212`、`:100-108` | 握手 3 次失败只写日志继续；reboot/force-reboot 的返回值直接丢弃 | `DoHandshake` 返回 bool 并由 `FlashImages` 决定中止；reboot 命令至少记录其返回码 |
| 12 | P11/P12/P14/P13 | `Services/FastbootClient.cs:72-79`、`Cli/BinaryLocator.cs:36-49`、`Cli/Commands/ReadCommands.cs:191`、`fastboot-modern/build.sh:8-14` | 找不到随包 fastboot 时静默落到系统 fastboot（Google platform-tools 对 Huawei 临时 loader 的 `getvar` 全面失败），`doctor` 的 `ok` 也不区分血统 | 首选用 `KIRIN_FASTBOOT`/随包路径，回落到系统 fastboot 时打印醒目 warning；`doctor` 增加 `fastboot-lineage` 检查（`getvar partition-size` 探针） |
| 13 | F09/S22 | `Services/FastbootFlasherService.cs:92-102`、`:59-62` | dump 成功判据只有「文件存在且 >0 B」，截断的备份会被当成可回滚依据 | 记录 header 声明大小并与文件大小比对，不一致即 `failed`；manifest 里写 size 期望值 |
| 14 | S19 | `MainWindow.axaml.cs:2381-2385` | `oeminfoerase-disablewp` 的返回被丢弃，写保护未解除时后续 FAILED 会被归因到别处 | 用 `ClassifyCommandOutput` 检查 disablewp，非 Succeeded 时直接中止并说明 |
| 15 | F05 | `Services/OemInfoService.cs:74-84` + `Services/OemInfoEditor.cs:76-92` | oeminfo 转换在「没有任何块命中」时仍返回 `IsSuccess=true`，写之前无法区分「改成功」和「没改到」 | `EditEntries` 返回命中条目数与 diff 摘要，命中数为 0 视为失败 |

**次级但同类（不在 TOP15，修复成本低可顺手做）**：F19（`unlocked` 免责声明只存在于 GUI 一处）、S20/S21（`FrpBypassResult.OverallSuccess` 是 OR 语义的死代码）、S29/S30（GUI 六处 `catch { "Unknown" }`）、P15（`publish.sh` 不产出 `loaders/` 却假定它存在）、P02（`option` 驱动名硬判）、S43/H74（别名表两份拷贝）。

（本节 DONE-6，报告完）

