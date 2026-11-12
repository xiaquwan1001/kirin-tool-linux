# F7：写入前自动备份（pre-op backup）

状态：进行中（骨架）
日期：2026-11-12

## 目标

任何真实写入之前，自动把红线分区（`FlashPolicy.IsRedline`）+ `ptable` dump 到时间戳目录并写
manifest；不支持 dump 的 loader 上优雅降级（记录原因，不阻塞写入，但在日志/JSON/UI 明确告知）。

## 改动清单

- 新增 `Services/PreOpBackup.cs`：纯服务，无 UI 依赖；dump 行为可注入（测试用）。
- 新增 `tests/BackupPlan/`：硬件无关测试（csproj + Program.cs，真 throw，非零退出）。
- 修改 `Cli/Commands/WriteCommands.cs`：flash-ota / flash-xml 真实写入前调用；新增 `--no-backup`、`--backup-dir`（可选 `--require-backup`）。
- 修改 `Cli/Commands/ReadCommands.cs`：新增只读 `backup` 命令（McpName=`backup`）。
- 修改 `Cli/CliRunner.cs`：注册 `backup`。
- 修改 `MainWindow.axaml.cs`：三条 fastboot 写路径 + USB Update 入口接入。
- 修改 `tests/suites.txt`：加 BackupPlan。
- 修改 `docs/cli-mcp.md`、`README.md`。

## 语义

- 备份集 = 写入计划中的红线分区 + `ptable`（无红线也备份 ptable）。
- 目录：`--backup-dir` > `$KIRIN_BACKUP_DIR` > `~/.local/share/kirin-tool/backups/<yyyyMMdd-HHmmss>/`；
  同秒冲突追加序号，绝不覆盖已有目录。
- 每分区先 `oem dump-emmc`，失败回退 `oem dump-storage`（复用 `FastbootFlasherService.DumpPartitionAsync`）。
- `manifest.json`：先写 `.tmp` 再原子 rename；含工具/时间/fastboot/设备型号与 SN（读不到为 null）/
  触发命令/写入计划/每分区 `{name,file,size,sha256,status,reason}`。
- 降级：unsupported/failed 不中止写入；CLI stderr `warning: pre-op backup incomplete (n/m)`，
  JSON 带 `backup:{dir,complete,partitions}`；GUI 进度对话框加说明行。
- `--require-backup`：显式严格模式，不完整则中止（exit 4）；默认不中止。
- 同一进程内同分区只 dump 一次（结果缓存）。

## 待办 / 需人决策

- （见文末，随实现追加）

## 实现记录（2026-11-12，本 lane）

- `Services/PreOpBackup.cs`：`BackupSet()`（红线 + ptable，去重）、`ResolveBaseDirectory()`
  （`--backup-dir` > `$KIRIN_BACKUP_DIR` > LocalApplicationData）、`CreateTimestampDirectory()`
  （同秒加 `-2/-3` 序号，绝不覆盖）、`RunAsync()`（缓存、每分区 emmc→storage、
  SHA256、`manifest.json.tmp`→原子 rename、status ok/unsupported/failed + reason）。
  两个构造器：生产（FastbootFlasherService）与测试（可注入 dump 委托）。无 UI 依赖。
- `KeepAllPartitions` 选项：`backup` 命令显式点名时不过红线过滤。
- `WriteCommands.cs`：flash-ota / flash-xml 在设备就绪后、任何 staging/写入前调用
  `PreOpBackupCli.RunForWriteAsync`；stderr 打 `warning: pre-op backup incomplete (n/m)`；
  applied JSON 带 `backup:{dir,complete,skipped,manifest,error,partitions}`；
  新参数 `--no-backup`、`--backup-dir`、`--require-backup`（不完整 → exit 4 拒绝）。
- `ReadCommands.cs`：新增只读 `backup`（McpName=`backup`），默认集 =
  ptable+oeminfo+nvm+modemst1/2+fsg+persist+modemnvm_1/2；完整 exit 0，否则 5。
  CLI/MCP 自动注册（CliRunner + tools/list 均枚举 Commands）。
- `MainWindow.axaml.cs`：新增 `RunPreOpBackupAsync` 助手；四条写入路径接入：
  gui-xml（FlashPartitionsWithProgress）、gui-full-ota（FlashFullOtaPartitions）、
  gui-rescue（HandleRebootToUsbUpdate）、gui-usb-update（StartUsbUpdateFlash）；
  备份进度/结果写进现有进度对话框 `CurrentOperationText`，失败不阻塞写入。
- `tests/BackupPlan/`：5 组硬件无关断言（备份集选择/目录不覆盖/manifest 结构与状态/
  全 unsupported 仍可继续/原子 rename 无 .tmp 残留），失败 throw、非零退出。
- `tests/suites.txt`、`docs/cli-mcp.md`、`README.md` 已更新。
- 未动 loaders/、fastboot/、fastboot-modern/、UsbDownloadService/XloaderPatcher；未 build/commit。

## 需人决策

1. **默认身份分区集清单**（`backup` 命令无参数时）：当前取 ptable + 常见身份/校准分区
   9 项；是否要按芯片（Kirin9000 等）细分，需硬件确认各机型实际分区名。
2. **`--require-backup` 的严格程度**：当前 attempted 且不完整、或目录创建失败时拒绝；
   是否目录失败应单独给更明确退出码，待定。
3. **manifest 里的设备型号/SN**：靠 `getvar product/serialno`，锁定态可能读不到（null）。
   是否补充 PSID / `oem lock-state info` 等更多身份字段，留待后续。
4. **备份目录轮转/配额**：每次写入都新建目录，身份分区大时（nvme 可达数百 MB）会累积；
   是否需要自动清理旧备份（如保留 N 份），需产品决策，本单未实现。
5. **USB Update 路径的备份时机**：备份用的是解包前的预扫描分区名，F2 之后才发现的
   红线分区不在这份备份里（写入前已另有红线 delta 确认闸门兜底）。
