# 降级擦除的闸门 / downgrade-erase gate (D-13)

**一句话**：\`downgrade-enable\` 原先声称"只打印计划"，而 GUI 的同一个按钮会真擦
\`oem oeminfoerase-{amss,base,cust,preload}ver\` 四条、**且完全不过审批闸门**。现在拆成两条能力，
让"谁擦、擦什么、谁批准"在注册表里说得清楚，三个面走同一个 \`DeviceWriteOps\` 方法。

状态：**fixed-in-2.4.15**（分支 \`feat/downgrade-gate\`，基线 = integration2 \`a62fb6c\`）。

---

## 1. 问题（修前状态）

| 面 | 修前行为 | 证据 |
| :--- | :--- | :--- |
| GUI | \`HandleEnableDowngrade\` 直接 \`_fastbootClient.EnableDowngradeWithSteps()\`，中间只有 4 个装饰性 \`Task.Delay(500)\`；**没有任何 \`RequestGuiApprovalAsync\`** | \`MainWindow.axaml.cs\` 的 \`HandleEnableDowngrade\` |
| CLI | \`downgrade-enable\`（\`GatedWriteCommand\`）只打印 4 条命令，一律 exit 4 | \`Cli/Commands/WriteCommands.cs\` |
| MCP | \`downgrade_enable\` 同上，\`confirm:true\` 也只确认计划 | \`Cli/Mcp/McpServer.cs\`（工具表由 \`McpName\` 生成） |
| 注册表 | \`downgrade-enable\` 声明为 \`PlanOnly\` | \`Services/Capabilities/CapabilityRegistry.cs\` |

后果：

1. **三面契约与实际不符**：读注册表的人会以为这条能力"不写设备"，而 GUI 不可逆地擦掉了 oeminfo
   的四个版本字段（降级的前置条件，也是这条流程里唯一不可回退的一步）。
2. **绕闸门**：写 SN / FRP / oeminfo 都要手输确认串，唯独这条不可逆擦除连"普通确认"都没有 ——
   与 D-01（\`oeminfo\` 声明 PlanOnly、GUI 却真刷）是**同一个形状**。
3. 审计编号：BB-04（GUI 黑盒）/ H90（硬编码）/ D-13（债务登记）。

## 2. 修法（照抄 D-01 的形状）

D-01 的做法是把一条"说了谎"的能力拆成两条：一条只在本机干活（PlanOnly）、一条写设备并过闸门。

| D-01 先例 | 本次（D-13） |
| :--- | :--- |
| \`oeminfo-convert\`（PlanOnly，只在本机构造镜像） | \`downgrade-enable\`（PlanOnly，只打印 4 条计划） |
| \`oeminfo-flash\`（Write + 红线 + 不可逆 + \`RedlineTypedConfirm\`） | \`downgrade-erase\`（同上） |
| \`DeviceWriteOps.FlashOemInfoAsync(WriteTicket, path)\` | \`DeviceWriteOps.EnableDowngradeAsync(WriteTicket, CancellationToken)\` |
| GUI \`HandleOemInfoFlash\` → \`RequestGuiApprovalAsync\` → ticket → 写 | GUI \`HandleEnableDowngrade\` → \`RequestGuiApprovalAsync\` → ticket → 擦 |

刻意**不变**的部分：4 个 \`Task.Delay(500)\` 只是让步骤条逐行点亮（审计 BB-18 的展示性延迟），
不属于本次范围，代码里已加注释标明"presentation only"。

## 3. 能力拆分（注册表）

| 字段 | \`downgrade-enable\` | \`downgrade-erase\` |
| :--- | :--- | :--- |
| NameZh | 启用降级（OEMINFO 版本擦除）规划 | 擦除 OEMINFO 版本字段以启用降级 |
| WriteKind | \`PlanOnly\` | \`Write\` |
| CLI | \`downgrade-enable\`（exit 4，什么都不写） | \`downgrade-erase\` |
| MCP | \`downgrade_enable\` | \`downgrade_erase\` |
| GUI | 无（这段没有"只看计划"的按钮） | \`HandleEnableDowngrade\` |
| Approval | \`None\` | \`RedlineTypedConfirm\` |
| Reversibility | \`Reversible\`（未声明，按默认） | \`Irreversible\` |
| ServiceMethod | 无 | \`DeviceWriteOps.EnableDowngradeAsync\` |
| Params（CLI） | 无 | \`yes\`, \`allow-redline\`, \`json\` |
| Tool | \`fastboot\` | \`fastboot\` |
| SideEffects | 只打印 4 条 \`oem oeminfoerase-*\` 计划；真正的擦除是 \`downgrade-erase\` | 擦除 oeminfo 版本字段 amss/base/cust/preload（4 × \`oem oeminfoerase-*ver\`），不可逆；OKAY 只证明命令被执行，版本记录是否清空、降级是否可行都**没有读回验证** |

为什么 GUI 面只挂在 \`downgrade-erase\` 上：GUI 那个按钮（\`Tag=enable-downgrade\`）按下去就是擦，
没有"只看计划"的分支。把 \`HandleEnableDowngrade\` 同时挂在两条能力上会让 \`SurfaceParity\` 的
E / H3 检查变成"两条能力共用一个处理器"，读表的人会再次误判。

## 4. 三面接线

### 4.1 服务层（唯一写入口）

\`Services/WriteOperations/DeviceWriteOps.cs\`：

\`\`\`csharp
public async Task<EnableDowngradeResult> EnableDowngradeAsync(WriteTicket ticket, CancellationToken cancellationToken = default)
{
    Require(ticket, "downgrade-erase");          // 无票 / 票给的是别的能力 → InvalidOperationException
    using OperationScope op = OpLog.Begin(OpNames.DowngradeErase, ...);
    EnableDowngradeResult result = await Client.EnableDowngradeWithSteps();
    // 每条 oem oeminfoerase-* 一行 step（命令 + okay=yes/no + 输出尾）
    op.Result(okSteps == 4, ..., "step-outcomes:" + okSteps + "/4-okay", ...);
}
\`\`\`

- \`Require(ticket, "downgrade-erase")\` 与其它写方法同一个实现：ticket 必须由 \`ApprovalGuard\` 在闸门
  说 yes 之后签发，且**绑定能力 id** —— 一个 \`reboot\` 的票不能拿来授权擦除。
- 操作日志：op 名 \`downgrade.erase\`（新增 \`OpNames.DowngradeErase\`，见 \`Services/Diagnostics/OpLog.cs\`），
  工具类别 \`flash\`，argv = 4 条命令；逐条 step + 一条 \`result\`。
- **verdictSource** = \`step-outcomes:<n>/4-okay\`：判定依据是"每条命令是否 exit 0 且输出 OKAY"
  （\`FastbootClient.EnableDowngradeWithSteps\` 的规则），而不是"跑完了"。没有读回通道能证明版本记录
  已清空，所以 4/4 也只敢说"命令被执行"。

### 4.2 GUI

\`MainWindow.HandleEnableDowngrade\`：

1. \`RequestGuiApprovalAsync(ApprovalRequests.For("downgrade-erase", argv: [fastboot, oem, oeminfoerase-*...], targets: [oeminfo, 4 条命令名], summary: [...]))\`
2. 被拒 → \`ShowMessageBox("Downgrade Cancelled", ...)\` 并 return（**不弹步骤框、不发任何命令**）。
3. 批准 → \`_deviceWriteOps.EnableDowngradeAsync(admission.RequireTicket())\`；异常走
   \`ShowCloseButton(false)\` 并显示异常文案。
4. 之后沿用原来的步骤展示（4 × \`Task.Delay(500)\` + 每步 OKAY/no OKAY + 尾注
   "OKAY 不等于降级可行"）。

### 4.3 CLI

\`\`\`
kirin-tool-cli downgrade-enable --yes --json        # 只看计划：exit 4、applied=false、gate=plan-only|confirmed
kirin-tool-cli downgrade-erase --yes --allow-redline --json   # 真擦：红线 + 手输确认串
\`\`\`

- \`downgrade-erase\` 是 \`GatedDeviceWriteCommand\`：\`--yes\` 只是"确认请求"，红线 + 不可逆要求
  stdin 手输 \`I UNDERSTAND downgrade-erase\`；非交互 stdin 直接拒绝（gate=refused, exit 4），
  提示改用运行中的 GUI 带外批准。
- 结论走 \`WriteReport\`：4/4 OKAY → \`applied\`；1–3 条 OKAY → \`partial\`（exit 3）；0 条 → 
  \`failed\`（exit 3，\`applied=false\`）。JSON 里 \`result.verdictSource\` 指认判定依据。

### 4.4 MCP

- 工具名 \`downgrade_erase\`，由命令表的 \`McpName\` 生成；写工具自动要求 \`confirm: true\`，
  且不接受 \`allow-redline\`（协议层 -32602）。
- \`confirm:true\` **不是批准**：\`McpApprovalGate\` 要求运行中的 GUI 带外点头；没有 GUI 时
  \`approval_required_human\`，什么都没擦。

## 5. 测试计划与结果（硬件无关）

| 用途 | 位置 | 断言 |
| :--- | :--- | :--- |
| 三面齐备 + 闸门可达 | \`tests/SurfaceParity\` | A/A3/A4（注册表 ↔ CLI 参数与 WriteKind）、B/C（MCP 工具与 confirm）、E/H2（\`HandleEnableDowngrade\` 已注册、未进 \`gui-ui-only.txt\`）、H1（三面 + ApprovalKind + ServiceMethod）、H3（GUI 处理器 4 跳内到达 \`IApprovalGate\`）、H7（\`EnableDowngradeAsync\` 首参是 \`WriteTicket\`） |
| 闸门语义 | \`tests/ApprovalGate\` | \`downgrade-erase\` 声明 \`RedlineTypedConfirm\` / \`Irreversible\` / 确认串 \`I UNDERSTAND downgrade-erase\`；无票调用 \`EnableDowngradeAsync\` 抛异常；\`reboot\` 的票不能授权擦除；\`downgrade-enable\` 仍是 \`PlanOnly\` |
| 结论诚实 | \`tests/WriteGates\` | 桩 fastboot 全拒 → exit 3、\`applied=false\`、\`gate=failed\`、\`verdictSource=step-outcomes:0/4-okay\`；桩全 OKAY → exit 0、\`applied=true\`、\`gate=applied\`、4/4 |
| 注册表/Hash 边界 | \`tests/StableParity\` | \`MainWindow.axaml.cs\` 哈希按惯例刷新，reference 写明"仅审批接线，wire 命令与顺序未变" |

实测（本分支）：

\`\`\`
tests/SurfaceParity   413 checks passed   (25 capabilities / 24 CLI / 24 MCP / 0 GUI-only)
tests/ApprovalGate     88 checks passed
tests/WriteGates       70 checks passed
tests/StableParity     35 checks passed
全量 23 套 tests/suites.txt：全部 exit=0
\`\`\`

## 6. 对已发版本的影响

**行为变化（v2.4.14 → v2.4.15）**

- GUI「Flash ▸ Enable Downgrade」不再"按一下就擦"：先出审批对话框，红线 + 不可逆 → 必须输入
  \`I UNDERSTAND downgrade-erase\`。取消后**一条命令都不发**。
- 新增 CLI 命令 / MCP 工具 \`downgrade-erase\` / \`downgrade_erase\`（真实写入，exit 0 只在 4/4 OKAY 时）。
- \`downgrade-enable\` / \`downgrade_enable\` **语义不变**：仍然只打印 4 条计划、exit 4，
  注册表仍是 \`PlanOnly\`（没有 GUI 面）。

**兼容性**

- 设备侧命令、顺序、时序、重试**完全未变**（仍是 4 条 \`oem oeminfoerase-*ver\`，同序执行）。
- 已有的 CLI 调用 \`downgrade-enable --yes\` 不变；想把计划变成真写必须显式换成 \`downgrade-erase\`
  （红线 + 手输确认串），不会因为升级脚本而被误触发。
- MCP 客户端可见的工具数从 23 变 24；旧工具名与参数均未改动。

**计数（README 的 Checklist 同步）**：只读 11 / 真实写入 11 / 仅规划 2 = 24 个 CLI 命令与 24 个
MCP 工具（旧 README 的「真实写入 9 / 仅规划 3」把 \`vcom_unlock\` 错列进仅规划，本次一并按注册表
纠正为真实写入）。

**未修（不在本 lane）**：BB-18 的展示性延迟（保留）、\`EnableDowngradeResult.OverallSuccess\` 的 OR
语义（D-24，本 lane 不依赖它：调用点只用 \`SuccessfulStepsCount\`）、\`D-04\` USB Update 的成功判据。

## 附：改动文件清单

| 文件 | 改动 |
| :--- | :--- |
| \`Services/Capabilities/CapabilityRegistry.cs\` | 拆 \`downgrade-enable\` / 新增 \`downgrade-erase\` |
| \`Services/WriteOperations/DeviceWriteOps.cs\` | 新增 \`EnableDowngradeAsync(WriteTicket, …)\` + 逐 step op 日志 |
| \`Services/Diagnostics/OpLog.cs\` | 新增 \`OpNames.DowngradeErase = "downgrade.erase"\` |
| \`MainWindow.axaml.cs\` | \`HandleEnableDowngrade\` 先过闸门、再走 \`DeviceWriteOps\` |
| \`Cli/Commands/DeviceWriteCommands.cs\` | 新增 \`DowngradeEraseCommand\`（WriteReport 结论） |
| \`Cli/CliRunner.cs\` | 注册 \`downgrade-erase\` |
| \`tests/{SurfaceParity,ApprovalGate,WriteGates,StableParity}\` | 见 §5（StableParity 仅刷新哈希 + reference） |
| \`docs/{downgrade-gate.md,debt-register.md,cli-mcp.md,INDEX.md}\`、\`README*.md\` | 本文档 + 计数与状态同步 |
