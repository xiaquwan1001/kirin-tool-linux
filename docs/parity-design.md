# GUI / CLI / MCP 三面一致（parity）设计

> 状态：lane C 落地中（分支 `feat/mcp-parity`）。本文是**契约**：新增能力必须先改
> `Services/Capabilities/CapabilityRegistry.cs` 再写实现，否则 `tests/SurfaceParity` 红。
> 相关文档：[`cli-mcp.md`](cli-mcp.md)（命令与闸门语义）、`README.zh-CN.md`（MCP 工具清单）。

---

## 1. 现状差异（基线 35c6f6b 实测）

三面共享同一份实现的前提已经成立：**MCP 工具表是从 `CliRunner.Commands` 生成的**
（`McpServer.BuildTools()` 遍历 `ICliCommand`，用 `McpName` 作为工具名），所以 CLI↔MCP
不存在第二份代码。真正的漂移点在 **GUI ↔ CLI/MCP**：GUI 的按钮回调直接调 service 或
直接调 `fastboot`，与 CLI 命令表没有任何结构性联系。

| 面 | 规模（基线） |
| :--- | :--- |
| CLI 命令 | 11 条（`info doctor getvar extract dump backup flash-ota flash-xml vcom-unlock oeminfo downgrade-enable`） |
| MCP 工具 | 11 个，1:1 由上面的命令表生成（`flash_ota`…） |
| GUI 设备能力入口 | 22 个（`Handle*` 10 个 + 设备动作 `*_Click` 12 个） |
| 注册表（本 lane 新增） | 见 `CapabilityRegistry.All` |
| 无 CLI/MCP 映射的 GUI 能力 | 10 个（下表） |
| 无 GUI 入口的 CLI/MCP 能力 | 6 个（下表） |

### 1.1 GUI 有、CLI/MCP 没有（补/登记前）

| GUI handler | 语义 | 写类型 | 处理 |
| :--- | :--- | :--- | :--- |
| `ReadPsid_Click` | 读 PSID（`oem get-psid`） | 只读 | **本 lane 补 CLI+MCP `psid`** |
| `CheckUdevRules_Click` | 宿主机 udev 规则覆盖检查 | 只读 | **本 lane 补 CLI+MCP `udev`** |
| （无按钮，`DumpPartitions_Click` 的第一步） | 设备分区表（`getvar ptable`） | 只读 | **本 lane 补 CLI+MCP `partitions`** |
| `HandleReadSerialNumber` | 读 NVE SN | 只读 | 登记为 `read-sn`，映射到既有 `getvar`（`nve:SN`） |
| `HandleWriteSerialNumber` | 永久改写 NVE SN | 写 | 登记为声明债务（红线段，见 §4） |
| `HandleFrpRemove` | FRP 擦除三步 | 写 | 同上 |
| `HandleUnlockBootloader` | `oem unlock`（需按电源键） | 写 | 同上（不可自动化：需要人工按键） |
| `HandleUnlockFastboot` | BootROM 引导链注入 | 写 | 已有 `vcom-unlock`（PlanOnly）对应 |
| `HandleSoftwareTestpoint` | 软测试点进入/退出 | 写 | 声明债务（需真实 VCOM/工程线） |
| `HandleRebootToUsbUpdate` | 重启到 USB Update（含救援分区刷写） | 写 | 声明债务 |
| `RebootDevice_Click` | 重启设备 | 写 | 声明债务（下一步补 `reboot`） |

### 1.2 CLI/MCP 有、GUI 没有

| CLI/MCP | 语义 | 说明 |
| :--- | :--- | :--- |
| `doctor` | 宿主机工具链诊断 | GUI 无按钮；刷卡失败时用户看不到同一份自检 |
| `backup` | 身份分区备份（带 manifest） | GUI 在刷写流程内部自动备份（`PreOpBackup`），没有独立入口 |
| `getvar` | 任意 fastboot 变量 | GUI 只读固定几个变量 |
| `capabilities`（本 lane） | 芯片能力矩阵 | GUI 用 `ChipCapabilities.Allows` 内部判定，不展示 |
| `extract` 的 outdir/选择子集 | 解包子集 | GUI 有完整选择器（`MainWindow.Extractor.cs`），CLI 用 `--partitions` 语义未对齐（见 §4 迁移 3） |
| `dump` 的 `--output` | 单分区落盘 | GUI 用文件夹选择器 |

### 1.3 已经一致、但靠约定而非结构保证的部分

* 写命令的 `confirm`/`--yes`：MCP schema 由 `command.IsWrite` 生成，CLI 侧靠每个命令自己读
  `context.Yes`；**没有任何测试**保证"新加的写命令忘了读 `--yes`"。→ 门禁 C。
* 红线覆盖只有 CLI（`--allow-redline`）：MCP 侧在 `ValidateArguments` 里结构性拒绝。这条已经
  有 `tests/McpProtocol` 覆盖，本 lane 只补"注册表里写能力的 SideEffects 必须声明红线"。

---

## 2. 统一面设计

### 2.1 能力注册表是唯一事实源

`Services/Capabilities/CapabilityRegistry.cs`，一条能力一行：

```csharp
public sealed record Capability(
    string Id,                       // 稳定标识，kebab-case
    string NameZh,                   // 中文名（文档/报告用）
    string? CliCommand,              // CliRunner.Commands 里的 Name；null = 尚未自动化
    string? McpTool,                 // MCP 工具名（== ICliCommand.McpName）；null 同左
    string? GuiHandler,              // MainWindow*.cs 的方法名
    CapabilityWriteKind WriteKind,   // ReadOnly / PlanOnly / Write
    IReadOnlyList<string> Params,    // 该能力的参数名（必须是 CLI 参数的子集）
    IReadOnlyList<string> SideEffects, // 对设备/宿主机做了什么
    string? GuiOnlyReason = null);   // 只有 GUI 时：为什么还不能自动化
```

规则：

1. **只登记已有能力**，登记不改行为（本 lane 第 2 节）。
2. 一个 CLI 命令可以被多条能力引用（`getvar` ← `getvar` + `read-sn`），但**每条 CLI 命令
   至少被引用一次**，且所有引用者的 `Params` 并集必须等于该命令真实参数集。
3. `WriteKind` 与 `ICliCommand.WriteKind` 必须一致（`ReadOnly/PlanOnly/RealWrite` ↔
   `ReadOnly/PlanOnly/Write`）；不一致时以注册表为准并**测试失败**，不允许两处各写一套。
4. 只有 GUI 的能力必须给 `GuiOnlyReason`（为什么现在不能自动化）；`ReadOnly` 的 GUI-only
   能力**不允许**存在——只读能力必须补齐 CLI+MCP（这是本 lane 第 4 节的工作量来源）。

### 2.2 三面的映射方式

```
GUI 按钮 ──► service（Services/*.cs）           ◄── CLI 命令（Cli/Commands/*.cs）
                                                   │  McpName
                                                   ▼
                                              MCP 工具（McpServer 生成）
```

* **禁止**在 CLI/MCP 里复制 GUI 的业务逻辑：命令只做"参数解析 + 调 service + 输出/退出码"。
* 新增只读能力 = 注册表加一条 + 写一个 `ICliCommand`（MCP 自动获得）+ GUI 调同一个 service。
* 新增写能力 = 同上 + `WriteKind=RealWrite`/`PlanOnly` + `--yes`/`confirm:true` 门控 + 红线清单。

---

## 3. 门禁规则（`tests/SurfaceParity`）

硬件无关、走反射 + 进程内 schema，不碰 USB。失败信息必须可操作（逐条列出缺失项）。

| 门 | 断言 | 拦什么 |
| :--- | :--- | :--- |
| A | 注册表每条 `CliCommand` 在 `CliRunner.Commands` 里真实存在 | 注册表写错名/命令被删 |
| A2 | 每条 CLI 命令至少被注册表引用一次 | 新增命令漏登记 |
| A3 | 引用同一命令的所有能力 `Params` 并集 == 命令真实参数集 | 参数漂移（注册表只写了旧参数） |
| B | 注册表每条 `McpTool` 出现在 `McpServer` 生成的工具表里 | MCP 面漏工具 |
| B2 | MCP 工具表里每个工具都被注册表引用 | MCP 面多出未登记工具 |
| C | `Write`/`PlanOnly` 能力的 MCP schema 必须把 `confirm` 放进 `required`；`ReadOnly` 不得出现 `confirm` | 写工具可被无确认调用 |
| C2 | `ReadOnly` 能力不得声明 `yes`/`allow-redline` 参数，不得声明写型 SideEffects | 只读命令偷偷能写 |
| D | 反向：CLI 命令表 ↔ 注册表双向覆盖 | 新增命令/工具漏登记 |
| E | GUI 的 `Handle*` 方法必须被注册表某条 `GuiHandler` 认领，否则必须在白名单文件里带理由 | GUI 长出黑盒能力 |
| E2 | GUI 设备动作 `*_Click` 同样要求（纯 UI 管道放白名单） | 同上 |
| E3 | 白名单里的名字必须真实存在；且白名单条目不得同时出现在注册表（防"两边都写"） | 白名单腐烂/被当垃圾桶 |
| F | GUI-only 的写能力必须带非空 `GuiOnlyReason`；只读 GUI-only 能力直接失败 | 只读能力长期缺 CLI/MCP |
| G | MCP 端到端：跑一次 `mcp` 子进程 `tools/list`，工具名集合 == 注册表 `McpTool` 集合 | 反射看不到的生成路径漂移 |

白名单文件：`tests/SurfaceParity/gui-ui-only.txt`（`方法名  # 理由`，逐条在报告里列出）。

---

## 4. 迁移顺序

1. （本 lane）注册表 + 门禁 + 只读能力补齐（`partitions` / `psid` / `capabilities` / `udev`）。
2. 文档：README.zh-CN.md MCP 段列工具清单；`docs/cli-mcp.md` 补新命令；本文补实测差异表。
3. `extract --partitions` 与 GUI 的选择器语义对齐（GUI 选子集，CLI 只能全量）。
4. `reboot`（`fastboot reboot`）/ `reboot-usb-update` 纳入 CLI：非破坏性、可门控，是最容易
   消掉的两条 GUI-only 写债务。
5. 红线段（`write-sn` / `frp-remove` / `unlock-bootloader`）**保持 GUI-only 并写进注册表理由**：
   MCP 结构性拒绝 `allow-redline`（`McpServer.ValidateArguments`），把这些能力搬到 MCP 等于
   给 AI 客户端自授权；要开也只能走"CLI + 人手敲 `--allow-redline`"。
6. `usb-testpoint` / `reboot-usb-update` 依赖真实 VCOM/工程线，等 VCOM lane 的传输层稳定后再动。

## 5. 风险

| 风险 | 影响 | 缓解 |
| :--- | :--- | :--- |
| 注册表变成"第二份代码"，与实现各写一套 | 漂移点从 GUI 搬到注册表 | 门禁 A3/B/C 直接从 `CliRunner`/`McpServer` 反射取真值，注册表只是"声明"，对不上就红 |
| 白名单被当垃圾桶（什么东西都往里塞） | 门禁形同虚设 | 白名单只允许**纯 UI 管道**（浏览文件、勾选、跳转、外链），每条带理由；E3 检查白名单与注册表互斥 + 名字必须存在 |
| 新命令只加在 CLI 侧、GUI 忘了接 | 三面又漂 | 现在是反向：GUI-only 只读能力直接红，GUI-only 写能力必须写理由 |
| 反射访问 internal 类型（`CliRunner`/`McpServer`） | 重命名/可见性变更导致门禁静默失效 | 门禁显式断言"反射到的东西非空"，拿不到就 FAIL，而不是跳过 |
| 门禁跑进程内 MCP schema，误判真实协议 | 假绿 | G 门额外跑一次真实 `mcp` 子进程 `tools/list` 对齐（与 `tests/McpProtocol` 同风格） |
| 新补的只读命令跨 lane 冲突 | 合并冲突 | 只改 `Cli/Commands/ReadCommands.cs` + 新增 `Services/Capabilities/`，不碰 VCOM/logger |

---

## 6. 实测结果（本 lane 完成后，分支 `feat/mcp-parity`）

`dotnet run --project tests/SurfaceParity -c Release` 输出（同样的数字在 CI 里复现）：

```text
faces: 22 capabilities, 15 CLI commands, 15 MCP tools, 34 GUI handlers (16 with CLI+MCP, 6 GUI-only)
268 surface parity checks passed.
```

| 指标 | 基线 35c6f6b | 本 lane 后 |
| :--- | :--- | :--- |
| CLI 命令 | 11 | **15**（+`partitions` `psid` `capabilities` `udev`） |
| MCP 工具 | 11 | **15**（自动跟随，0 行手写 schema） |
| 注册表登记能力 | — | **22**（16 有 CLI+MCP，6 仅 GUI 且带理由） |
| GUI 设备能力入口 | 22（无结构约束） | 22，**全部登记**；34 个 `Handle*`/`*_Click` 里 17 个纯 UI 管道进白名单 |
| 只读 GUI-only 能力 | 3（psid / udev / 分区表） | **0** |
| 写能力 GUI-only 债务 | 6（未登记，隐形） | 6，**逐条带理由登记**（可见、可门禁） |
| 门禁检查条数 | 0 | **268**（`tests/SurfaceParity`） |

### 6.1 门禁上线时实际拦下的缺口（TOP5，原样保留）

在第一版注册表（登记了 4 条尚无 CLI 命令的只读能力）之后、补齐实现之前，门禁报 8 条失败：

```text
A psid:         CLI command 'psid' exists         <- missing; add it to CliRunner.Commands or fix the registry row
A partitions:   CLI command 'partitions' exists   <- missing
A capabilities: CLI command 'capabilities' exists <- missing
A udev-check:   CLI command 'udev' exists         <- missing
B psid / partitions / capabilities / udev:        MCP tool '<name>' is generated <- missing
```

也就是说门禁在**实现补上之前就是红的**，不是事后补的断言。补齐 4 条只读命令后转绿，
其中 MCP 侧 4 个工具**没有写一行代码**（`McpServer` 由 `ICliCommand` 生成）。

### 6.2 白名单现状（逐条）

`tests/SurfaceParity/gui-ui-only.txt` 共 17 条，全部是**纯 UI 管道**（点开文件对话框、勾选、切页、
复制文本、外链、关弹窗）——它们不触 fastboot/adb/USB，因此不构成能力：

`HandleNavigation`、`BrowseExtractionSource_Click`、`BrowseExtractionDestination_Click`、
`BrowseOtaFile_Click`、`EditOtaFile_Click`、`ClearOtaFile_Click`、`BrowseOemInfoFile_Click`、
`SelectExtractionAll_Click`、`SelectExtractionNone_Click`、`CancelExtraction_Click`、
`CopyUdevCommands_Click`、`OperationButton_Click`、`GlobalInteractionDone_Click`、
`GlobalInteractionCancel_Click`、`HelpButton_Click`、`TelegramButton_Click`、`GithubButton_Click`。

没有一条「能自动化但懒得做」的项进白名单：能自动化的只读能力已经全部落成 CLI+MCP。

### 6.3 迁移计划落点（含验证方式）

| # | 事项 | 状态 | 验证 |
| :--- | :--- | :--- | :--- |
| 1 | 注册表 + 门禁 + 4 条只读能力 | **完成** | tests/SurfaceParity 268 条 + 全量 18 个 suite |
| 2 | 文档同步（README / cli-mcp.md / 本文） | **完成** | 工具清单 15 个与注册表一致 |
| 3 | `extract --partitions` 与 GUI 选择器语义对齐 | 待做 | 门禁 A3 会在加参数后强制登记 |
| 4 | `reboot` / `reboot-usb-update` 纳入 CLI（消两条写债务） | 待做 | 门禁 F 会把债务数从 6 降到 4 |
| 5 | 红线段（write-sn / frp-remove / unlock-bootloader）保持 GUI-only | **决定** | 理由写进注册表 GuiOnlyReason；MCP 结构性拒绝 allow-redline |
| 6 | `usb-testpoint` 等 VCOM 传输层稳定后接 CLI | 待另一 lane | — |

### 6.4 复现

```bash
cd ~/src/wt/ktl-mcp
dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release
dotnet run --project tests/SurfaceParity -c Release          # 268 checks
```
