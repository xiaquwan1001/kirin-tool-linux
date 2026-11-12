# Lane A：写入安全闸门审计（2026-11-12）

范围：写入路径与 confirm/gate 语义（MCP、CLI、GUI、USB/VCOM/fastboot/unlock 服务）。
只读审计，未改动任何源码。持续追加中。

## 发现清单

### [P0] MCP 声称所有写工具 "gate-only, nothing is written"，但 flash_ota/flash_xml 在 confirm:true 下会真实刷写
- 证据:
  - Cli/Mcp/McpServer.cs:118 `instructions` = "...require confirm:true, return a plan, and are gate-only in this build (exit 4, nothing is written)."
  - Cli/Mcp/McpServer.cs:153 confirm 参数描述 = "true still does not write in this build (gate-only)"
  - 但 McpServer.cs CallToolAsync: `bool confirm = arguments?["confirm"]?.GetValue<bool>() == true; if (confirm) parsed.SetFlag("yes");` 且 `new CliContext(..., yes: confirm, ...)`
  - Cli/Commands/WriteCommands.cs FlashOtaCommand.ExecuteAsync（约 165-172 行）: `bool confirmed = context.Yes || args.HasFlag("yes"); if (!confirmed) return ExitCodes.Gated;` 之后直接 `FastbootSession.TryOpenAsync()` → `OtaFlashRunner.ExecuteAsync(...)` 真实刷写；FlashXmlCommand 同理。
  - docs/fork-release.md 记载 P40 Pro 46/46 分区实测（见 lanes B/C 交叉验证）。
- 影响: LLM 客户端（MCP）读到 instructions/参数描述里"true 也不写"的承诺，会在规划/探索时放心传 confirm:true；实际触发完整 46 分区写入，redline 分区若包里含 nvme/oeminfo 等还需 --allow-redline（MCP 未暴露该参数时会被 gate 拒，但普通 boot/system/recovery 等直接写）。对设备：未经人工二次确认的批量刷机；对用户：可能变砖；对 CI：自动化测试一旦带上 confirm:true 就会真写设备。
- 建议修复: 二选一——(a) 若本 build 确实想 gate-only：在 McpServer.CallToolAsync 里对 `command.IsWrite` 一律不映射 yes（删掉 `parsed.SetFlag("yes")`，context yes 固定 false），并同步修正 WriteCommands.cs 里 FlashOta/FlashXml 的"confirmed 即写"分支（或对 IsWrite 恒返回 Gated）；(b) 若刷写是预期功能：修改 McpServer.cs:118/153 的文案为"confirm:true 会真实写入"，并加显著红色警告。需真机验证（用测试设备 + 非关键分区）。
- 置信度: 高

### [P1] 红线拒绝以 exit 4 + isError=false 返回，与 plan-only 同信号——LLM 客户端可静默加 allow-redline 重试绕过
- 证据: WriteCommands.cs FlashOtaCommand.Params 没有 allow-redline 之外的 MCP 侧映射问题——其实有 `new CliParam("allow-redline", "boolean", ...)`，McpServer ToParsedArgs 会把 boolean 参数 SetFlag。但 McpServer 未在 instructions/tools 里提示红线语义；真正的风险点：redline 拒绝返回 ExitCodes.Gated（4），McpServer.cs ToolResult 把 exit 4 标为 isError=false（注释自认 "the plan is a valid result, not a protocol error"）——LLM 客户端把"redline 被拒"与"plan-only 成功"混为同一非错误信号，随后可能自动加 allow-redline 重试。
- 影响: LLM 自动化循环里，gate 拒绝（含红线拒绝）表现为"成功结果"，诱导客户端换参数绕过；对设备：redline 写入风险由 LLM 决策而非人。
- 建议修复: McpServer.ToolResult 对 Gated 且文本含 "refused:"（红线）时 isError=true，或在 structuredContent 加 `gate:"refused"` 字段；并考虑 MCP 端不透传 allow-redline（要求人用 CLI）。无需真机验证。
- 置信度: 中（红线拒绝确实以 exit 4 + 非 error 返回是确定的；LLM 是否会自动重试取决于客户端行为）

### [P1] GatedWriteCommand 的"gate-only"注释与自身基类描述已经失真：flash-ota/flash-xml 不继承该基类且真写，其余三个命令是真 gate——语义分裂导致文档/测试错位
- 证据: WriteCommands.cs:9-15 基类注释 "the actual write path is intentionally not implemented in this build, so they still exit 4"——只对 VcomUnlock/OemInfo/DowngradeEnable 成立；FlashOtaCommand/FlashXmlCommand 是独立实现且 confirmed 分支真写。tests/FlashPolicy/Program.cs:56 注释 "the command stops at the plan gate, so nothing is written" 同样只覆盖部分命令。
- 影响: 后续维护者按"全 gate-only"理解写代码/测试，会漏掉真写路径的安全检查（电量闸门、备份、取消处理）；新加的写命令可能照抄 FlashOta 的 confirmed-即写模式而忘记闸门。
- 建议修复: 修正 WriteCommands.cs 头部注释与 tests 注释，把"gate-only"限定到 GatedWriteCommand 三个子类；在 FlashOta/FlashXml 的 confirmed 分支前加 TODO/红线注释列出缺失闸门。无需真机。
- 置信度: 高

### [P1] 全部真实写入路径均无电量闸门（项目约定 <50% 不写），也无 pre-op 备份步骤
- 证据: rg 'battery|Battery|电量' 于 MainWindow.axaml.cs、Services/USBUpdate/*、Services/FastbootFlasherService.cs、Services/VcomFlasher.cs 零命中；所有写入入口（FlashPartitionsProcess ~:544、StartUsbUpdateFlash ~:1010、FlashFullOtaPartitions ~:1143、rescue ~:1781、HandleEnableDowngrade ~:1696、HandleFrpRemove ~:1648、HandleWriteSerialNumber ~:1955）开启前只查设备是否连接，不查电量。scripts/stage-report.sh 的 <50% 闸门只存在于项目脚本流程，不在工具内。
- 影响: 设备在低电量下被刷 6.5GB super 或 USB Update 中途断电 → 半写状态变砖；对用户是本工具核心风险面；对 CI 无影响。
- 建议修复: 在每个写入入口调用 fastboot 前加一次电量检查（或至少提示 + 强确认），位置建议收口到一个 Utils 写入前置检查函数，GUI/CLI 共用。需真机验证电量 getvar 在华为 loader 上的可用字段名。
- 置信度: 高（缺失是事实；修复方案的 getvar 字段名需真机确认）

### [P1] HandleWriteSerialNumber（SN 重写）零确认、零闸门：NVE 写入且 SN 不在 FlashPolicy 红线集
- 证据: MainWindow.axaml.cs:1955-1968 —— 只做非空校验，随后直接 `_fastbootClient.WriteNveVariable("SN", newSn)`，无 ShowConfirmDialog、无 ConfirmRedlineWriteAsync；Utils/FlashPolicy.cs RedlineContains 含 "nvme" 但不含 "nve"，"SN" 也不命中任何 token，因此即使接入 FlashPolicy 也不会拦截。
- 影响: 用户在输入框误触/误输入即永久改写设备序列号（NVE 区，recovery 不可恢复）；对设备：身份信息损坏，影响保修/账号绑定；对 CI 无影响。
- 建议修复: 在 HandleWriteSerialNumber 里先弹确认对话框（显示旧 SN——已有 ReadNveVariable 可读——与新 SN 的 diff，要求输入完整新 SN 二次确认）；同时评估把 "nve" 写入纳入红线确认（不是加入 FlashPolicy 子串表，因为读操作也含 nve——只拦写入口）。无需真机（纯 UI 逻辑）。
- 置信度: 高

### [P1] HandleFrpRemove 无任何确认即连发 erase frp / erase config / oem frp-erase
- 证据: MainWindow.axaml.cs:1648-1657 —— 打开 FrpStepDialog 后第一行就 `await _fastbootClient.EraseFrpWithSteps()`，无 ShowConfirmDialog；FastbootClient.cs:119-136 三个 erase 步骤。frp/config 不在 FlashPolicy 红线集（合理，非 brick 风险），但这是不可逆的安全状态清除。
- 影响: 用户误点菜单即清除 FRP/config 分区；对设备：配置丢失、FRP 状态改变（涉及设备归属/账号安全）；对 CI 无影响。
- 建议修复: 在 HandleFrpRemove 起始处加 ShowConfirmDialog，写明将 erase frp+config 且不可逆。无需真机。
- 置信度: 高

### [P2] CLI flash-ota JSON 输出的 exitCode 与进程实际退出码不一致（ok==0 时）
- 证据: Cli/Commands/WriteCommands.cs FlashOtaCommand 末段——ok==0 时 `context.WriteJson({... exitCode = ok == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected})` 先按 DeviceRejected(3) 打印，随后 `return ExitCodes.FileOrVerify`(5)。structuredContent.exitCode=3 而进程退出码/MCP _meta.exitCode=5。
- 影响: MCP 客户端读 structuredContent.exitCode=3 会误判为"设备拒绝"而非"文件/校验失败"，错误归因错位；对设备无直接风险；对 CI/自动化解析 exit code 时出现双源矛盾。
- 建议修复: FlashXmlCommand 已是正确写法（okCount==0 → FileOrVerify 且 JSON 内 exitCode 同步）；把 FlashOtaCommand 的 JSON exitCode 表达式改为与 return 一致（ok==0 → FileOrVerify）。无需真机。
- 置信度: 高

### [P1] GUI 三条 fastboot 写入路径只看 fastboot 退出码判成功，未要求 OKAY——与 2.4.12「GUI 假成功收敛」不一致
- 证据: Models/ProcessResult.cs:34 `public bool IsSuccess => ExitCode == 0;`。三处只查 IsSuccess：
  - MainWindow.axaml.cs:~655（FlashPartitionsWithProgress，XML 分区刷写）`result.IsSuccess → CompletePartition(partition.Name, true)`（且经 Services/FastbootFlasherService.cs:80-90 `FlashPartitionAsync`，异常还被吞成 `(false, "Failed")`）。
  - MainWindow.axaml.cs:~1277（FlashFullOtaPartitions）`flashResult.IsSuccess → successCount++`。
  - MainWindow.axaml.cs:~1868（rescue 分区刷写）`flashResult.IsSuccess → CompletePartitionByIndex(i, true)`。
  对照：CLI（WriteCommands.cs flash-xml/OtaFlashRunner）与 GUI 其他路径（XLOADER :1493、oeminfo :2401、unlock :2101、USB-update 模式进入 :1904）都要求「exit 0 AND OKAY」。rescue 的模式进入判定也专门写了 Unknown 态，但分区本身写入没有。
- 影响: fastboot 退出 0 但未打 OKAY（刷写被 loader 拒绝但进程仍返回 0 的场景）时 GUI 报该分区成功，最终可能整体显示「flash completed」，用户以为已刷好；对设备：半刷状态被掩盖；对 CI：无（GUI 不进 CI）。
- 建议修复: 在三处统一改为 `flashResult.IsSuccess && (flashResult.Output ?? "").Contains("OKAY")`，否则标 Failed 并带 FlashFailureDescription(flashResult.Output)；FastbootFlasherService.FlashPartitionAsync 的 catch 改回透出真实错误消息。需真机验证（构造一个刷错名分区看 loader 输出与退出码）。
- 置信度: 高（代码事实确定；「exit 0 无 OKAY」在华为 loader 上是否实际出现需真机验证）

### [P2] USB Update：解包后才发现的红线分区继承解包前的确认，用户从未见过这些分区名
- 证据: MainWindow.axaml.cs:948-957 pre-scan 只含 `file.SelectedPartitions`（解包前已知名字）；注释自认「the scan sees just the partitions the selected files announce」；随后 USBUpdateFlasherService.cs FlashPartitions 解包后 EnforceRedlineWrite（:62-80）用同一个 allowRedline 放行，不再向用户确认新发现的分区名。
- 影响: 若包内分区名与选择器展示名不一致（别名/变体，或 UPDATE.APP 中存在选择器未列出的条目），红线分区在用户未见过其名字的情况下被写入；对设备：意外覆盖校准/身份数据。
- 建议修复: EnforceRedlineWrite 在 allowRedline=true 时返回命中清单，GUI 通过 OnPartitionsDiscovered 已拿到全量名单，可在解包完成后对 `RedlineHits(discovered) - preScanConfirmed` 差集再弹一次 ConfirmRedlineWriteAsync，差集非空且未确认则中止（此时 list.txt 未写、无字节上设备）。无需真机。
- 置信度: 中（差集是否实际非空取决于选择器数据源与包内容，需构造样例包验证）

### [P2] CLI/MCP 中断（Ctrl+C / 取消）后已写分区清单不落盘，重跑只能全量重来
- 证据: OtaFlashRunner.ExecuteAsync（Cli/Commands/OtaFlashRunner.cs ~230-300）每分区写完才记 outcome，temp 目录 finally `Directory.Delete(tempDirectory, true)`；取消（OperationCanceledException）从 FlashOtaCommand 冒泡到 CliRunner.cs catch 统一打 `error: cancelled` exit 5——已成功写入的 N/M 分区无处可查；GUI FlashPartitionsProcess 取消后同样只有对话框提示。
- 影响: 半写状态（尤其 ptable 已刷、后续分区未刷）对用户不可见，重跑 flash-ota 全量重刷一遍；对设备：重复写是浪费但有风险（ptable 重写）；对 CI 无影响。
- 建议修复: 把 outcomes 逐条追加写到 staging 目录（或 --json 流式输出），取消路径也 flush；重跑时支持 `--resume <manifest>` 跳过已确认 OKAY 的分区。无需真机。
- 置信度: 高

### [P2] 绕过路径核查结论：主路径闭合，flash-xml 的 only 白名单只匹配 identifier（不匹配 Name）
- 证据: 大小写——FlashPolicy.ShouldSkip/IsRedline 均先 ToLowerInvariant，RedlineHits 用 OrdinalIgnoreCase HashSet，`-` 前缀由 CliRunner.cs:52 `TrimStart('-').ToLowerInvariant()` 规整；文件名注入——FastbootClient.FlashPartition（:109-112）走 ProcessRunner 参数数组且逐参数 JSON 转义引号（Utils/ProcessRunner.cs:47），无 shell；别名分区名——OtaFlashRunner.NameAliases 在红线判定之后仅用于寻址，判定用原始名，命中 nvme 等不受别名影响。唯一缝隙：Cli/Commands/WriteCommands.cs FlashXmlCommand 的 `onlySet` 只与 `partition.Identifier` 比较（excludeSet 才同时比对 Name），XML 里 identifier 与 name 不一致时 `--partitions` 过滤按 identifier、红线判定两者都查——不存在绕过红线，只是过滤语义与用户直觉不符。
- 影响: 低；可能在 XML 混合包里选错想排除的镜像（按 name 写 --partitions 不生效），导致多刷而非少刷。
- 建议修复: FlashXmlCommand 的 onlySet.Contains 改为 `onlySet.Contains(identifier) || onlySet.Contains(partition.Name ?? "")`，与 excludeSet 对齐。无需真机。
- 置信度: 高

### [P2] VCOM/BootROM 路径（VcomFlasher/FirmwareUnlocker）不涉及分区写入，红线模型对其不适用——现状安全，但 CLI vcom-unlock 的 plan 文案与 GUI unlock 结果三态判定需保持
- 证据: Services/VcomFlasher.cs 全文只有 StartFrame/BuildHeadCmd/BuildDataCmd（内存地址上传，WriteAndVerify 为串口写）；FirmwareUnlocker.UnlockFastboot 只上传 loader 到 RAM 地址（CpuAddresses 表），不调任何 flash/erase。GUI unlock 判定（MainWindow.axaml.cs ClassifyUnlock）已按 2.4.12 收敛为三态，AlreadyUnlockedNoWrite 不会误报成功。
- 影响: 无直接风险；唯一注意点：unlock 成功后 oem unlock 会触发 factory reset，这是该路径真正的破坏性副作用，属硬件冻结语义，不在本 lane 修改范围。
- 建议修复: 不改代码（语义冻结）；仅建议在 docs 里写明「vcom-unlock 的破坏性在 unlock 之后的 factory reset，不在 FlashPolicy 管辖」。无需真机。
- 置信度: 高

### [P2] MCP isError 判定收尾：Gated(4) 非 error 是有意设计，但 NoDevice(2)/FileOrVerify(5) 正确标 error；风险集中在 P1 第二条所述「红线拒绝与 plan-only 同信号」
- 证据: Cli/Mcp/McpServer.cs ToolResult：`bool isError = exitCode != ExitCodes.Ok && exitCode != ExitCodes.Gated;`——exit 2/3/5 → isError=true；exit 0/4 → false。plan-only（未确认）与红线拒绝（refused）都走 exit 4，客户端无法区分。
- 影响: 见上文 P1「红线拒绝混入成功信号」条目。
- 建议修复: 同该条目（structuredContent 加 gate 状态字段）。
- 置信度: 高

## 结论汇总

统计：P0 × 1，P1 × 5，P2 × 6。

最值得先修的 3 条：

1. **[P0] MCP "gate-only" 文案与 flash_ota/flash_xml 真实刷写矛盾**（McpServer.cs:118/153 vs WriteCommands.cs confirmed 分支）——LLM 客户端一条 confirm:true 即触发全量刷机，且 allow-redline 也从 MCP 透传，红线决定权落在 LLM 手里。先定语义（真 gate 还是真写），再改代码+文案，一处收口。
2. **[P1] GUI 三条 fastboot 写路径只看退出码判成功**（FlashPartitionsWithProgress / FlashFullOtaPartitions / rescue）——补 OKAY 判定，恢复 2.4.12 的假成功收敛口径。
3. **[P1] SN 重写与 FRP erase 零确认**（HandleWriteSerialNumber / HandleFrpRemove）——两处不可逆身份/安全状态写入，各补一个显式确认对话框即可，成本最低、收益直接。

（附注：电量闸门与 pre-op 备份全路径缺失，建议作为独立工作项排期，需要真机先确认华为 loader 的电量读取接口。）

