# F6：电量横幅警告（不拦截写入）+ flash-xml 对齐 flash-ota

日期：2026-11-12 ｜ lane：fix ｜ 状态：进行中（仅静态编辑，未构建/未运行）

范围：MainWindow.axaml.cs、MainWindow.ChipCapabilities.cs、Cli/Commands/WriteCommands.cs。
未动：loaders/、fastboot*/、Services/USBUpdate/*、README（留给 F8）。

## A. 电量横幅

- [x] helper TryReadBatteryPercentAsync（battery-soc → battery-capacity → battery-voltage，失败静默 null）
- [x] DeviceInfoPage 顶部非模态 WindowsInfoBar（复用现有控件，代码态 Insert(2)）
- [x] ReadDeviceInfo_Click 挂刷新
- [x] 4 处 TODO(battery-gate) 注释改为指向横幅说明（warning-only by decision 2026-11-12）

实现要点：
- helper 只接受 0-100 整数；battery-voltage 直接返回 null（mV 非百分比）；
  FAILED / invalid command / 异常一律静默 continue/null，绝不弹错。
- 横幅为 UI.WindowsInfoBar（IsClosable=false，只读提示），<50% 显示
  "Battery N% — flashing below 50%..."，≥50% IsOpen=false，读不到显示
  "Battery level unavailable on this loader"。
- 不改任何写入路径判定，不新增确认框，不拦按钮。

## B. flash-xml 对齐 flash-ota

- [x] 预检前移到 plan 阶段：FastbootSession 打开 + ResolveNamesAsync 提前到
      JSON/文本 plan 输出之前，未确认的 plan 即显示 skippedUnresolved
- [x] 默认改为跳过未解析 identifier（writeList 只含 loader 暴露的名字）+ warning；
      --strict-names 在任何写之前拒绝（ExitCodes.DeviceRejected = exit 3）
- [x] JSON 三种文档（plan-only / refused / applied）均含 skippedUnresolved；
      partitionCount = writeList.Count（实际会写的数量）
- [x] Usage 与 --strict-names 参数描述更新（"unresolved identifiers are skipped
      unless --strict-names (which refuses with exit 3)"）

行为变化（有意）：
- plan-only 阶段现在需要已连接的 fastboot 设备（预检要查 ptable）；无设备时
  plan 阶段即返回 exit 2 (NoDevice)，与此前"确认后才需要设备"不同。
- ptable 查询失败时 ResolveNamesAsync 保持原 fallback（lower-cased names，
  unresolved 为空 → 全部照写），与 flash-ota 一致。
- 进度输出 [i/N] 的 N 改为 writeList.Count。

## 核对

- 未构建/未运行（按派工约束，编排者统一构建验证）。
- 静态核对：三个改动文件花括号配对一致；TODO(battery-gate) 全仓无残留；
  未触碰 loaders/、fastboot/、fastboot-modern/、Services/USBUpdate/*；
  git status 仅 3 个改动文件 + 本报告（fix-F8.md 非本 lane 产物，未动）。
- 横幅仅在 ReadDeviceInfo_Click 末尾刷新（用户点 Read 后），不在写路径上；
  TryReadBatteryPercentAsync 不抛异常，QueryVariableAsync 失败静默。
- WriteCommands.cs 其余命令（flash-ota / GatedWriteCommand 等）零改动。
