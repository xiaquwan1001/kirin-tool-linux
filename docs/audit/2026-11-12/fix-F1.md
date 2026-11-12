# F1 fix lane — CLI/MCP 写入闸门语义收口（2026-11-12）

状态：**编辑完成（静态核对通过）**。本 lane 未跑 dotnet build/run/test（编排者统一全量构建与两套 CI）；
未执行任何 git add/commit/push。只改了清单内文件。

## 已完成（文件:行为 → 改动）

1. **写类型枚举** `Cli/Commands/ICliCommand.cs`：新增 `enum CliWriteKind { ReadOnly, PlanOnly, RealWrite }`；
   接口默认实现 `CliWriteKind WriteKind => CliWriteKind.ReadOnly`；`IsWrite` 保留为兼容默认实现
   （= WriteKind != ReadOnly），未逐个改 ReadCommands 的 5 个只读命令类（它们自己的 `IsWrite => false` 与默认值等价，保留）。
2. **PlanOnly 归位** `Cli/Commands/WriteCommands.cs`（GatedWriteCommand）：`WriteKind => PlanOnly`（3 个子类继承）；
   类注释改为「plan-only 命令，确认也不写；真写命令在下面的 flash-ota/flash-xml」；confirmed 文案由
   `confirmed-but-write-path-not-implemented` 改为机器可读 gate 字符串 `"confirmed"`（JSON `gate` 由嵌套对象改为字符串，
   原 reason 移至 `gateReason` 字段）。**注意**：JSON 的 gate 字段从 object 变为 string，若有下游消费旧结构需人工确认。
3. **RealWrite 标记**：FlashOtaCommand / FlashXmlCommand `WriteKind => RealWrite`，Usage 注明「with --yes a real write happens」。
4. **gate 信号三态落地**（WriteCommands.cs）
   - flash-ota：计划 JSON 已有 `gate="plan-only"`；红线拒绝新增 JSON（`gate="refused"`，之前只写 stderr，:5 项）；
     真写完成 JSON 增加 `gate="applied"`。
   - flash-xml：计划 JSON 补 `gate="plan-only"`（原来没有）；红线拒绝补 JSON `gate="refused"`；写完补 `gate="applied"`。
5. **MCP 动态文案** `Cli/Mcp/McpServer.cs`：`instructions` 改为由命令对象按 WriteKind 动态生成
   （writes the device (…): confirm:true → real write, no dry-run / plan-only tools (…): confirmed still writes nothing）；
   confirm 参数 description 按 WriteKind 生成（RealWrite："true performs the real write; there is no dry-run in the MCP path"；
   PlanOnly："true only acknowledges the plan; nothing is written"）。代码中已无 "gate-only in this build" 残留（grep 复核）。
6. **MCP 禁红线自决**（McpServer.cs）：`BuildTool` 从 MCP schema 跳过 `allow-redline`；`ValidateArguments` 中
   arguments 含 `allow-redline` → JSON-RPC `-32602`，message "redline override is CLI-only ('allow-redline' is not accepted over MCP)"。
7. **MCP 参数校验**（McpServer.cs，新增 `ValidateArguments`，`CallToolAsync` 改为返回完整响应 envelope）：
   未知参数名 → `-32602`；`confirm`（及其它 boolean 型参数）非 bool（如字符串 "true"）→ `-32602`；
   `arguments` 为空对象/缺省仍可用。顺带修复：unknown-tool 与错误路径现在也包在 ResultResponse envelope 里（原来会裸返回 result）。
8. **gate 透传**（McpServer.cs `ToolResult`）：从 structuredContent 提取 `gate` 写入 `_meta.gate`；
   isError：`refused`→true，`plan-only`/`confirmed`→false，无 gate/其它值回退原 exit-code 规则。
9. **flash-ota exitCode 对齐**（:6 项）：JSON 与 return 统一为 ok==0→5(FileOrVerify)、部分→3、全成功→0；写入主体抽为
   `static async Task<int> FlashStagedAsync(...)` 局部函数。flash-xml 同样对齐（json 曾写 3/0 而 return 可能是 5）。
10. **flash-xml 写前名称解析**（:7 项）：确认后、写前用 `OtaFlashRunner.ResolveNamesAsync` 解析 identifier；
    默认 warning 继续写，`--strict-names`（新参数）→ 拒绝 exit 3；flashName 优先取 resolved 值。
    `OtaFlashRunner` 行为语义未动（仅 BuildPlan 白名单一行，见下）。
11. **--partitions 白名单口径**（:7 项）：`OtaFlashRunner.BuildPlan` 只命中条件由「仅分区名」改为「分区名或 source 文件名任一」
    （与 exclude 对齐）；flash-xml 的 onlySet 同样改为 identifier 或 Name 任一命中。
12. **默认 staging 清理**（:8 项）：flash-ota 写入段包 `try/finally`，非 `--work-dir` 时递归删除默认
    `.kirintool-cli-tmp`（含异常/取消路径），成功/失败都输出 diag；显式 --work-dir 保留。flash-xml 不创建 staging 目录，无需清理。
13. **测试不再假成功**（:9 项）`tests/FlashPolicy/Program.cs`：CLI 二进制缺失改为 FAIL + `Environment.ExitCode = 1`，
    输出提示先 `dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release`；删除已无引用的 `Skip` helper。
14. **文档**（:10 项）`docs/cli-mcp.md`：写命令章节改写为 RealWrite/PlanOnly 真实行为表 + gate 取值表 + 退出码表；
    MCP 节写明不接受 allow-redline、-32602 校验、confirm 语义按 WriteKind 生成；保留原「约定」小节（fastboot 解析顺序等）。
    `README.md:19-22` 本来就描述真实写入路径，无需改动，口径一致。

## 静态核对结果

- 6 个目标文件 brace 平衡、关键 hunk 逐条 grep/read 复核（gate 字符串、FlashStagedAsync、ValidateArguments、
  meta["gate"]、strict-names、staging 清理、ExitCode=1、RealWrite 表）均在位。
- 代码与文档中 "gate-only in this build" / `IsWrite => true`（写命令处）无残留。
- 未动 loaders/、fastboot/、fastboot-modern/、UsbDownloadService.cs、XloaderPatcher.cs。

## 未做 / 需人决策

- **未构建/未测试**（按约束）：C# 语法为静态核对，编译期错误（如 `is not JsonValue value` 模式、本地函数位置）需编排者全量构建确认。
- **GatedWriteCommand 的 JSON gate 结构变更**（object → string + gateReason）：脚本/自动化若解析旧嵌套结构会破，建议编排在 CHANGELOG 或发布说明标注。
- **GatedWriteCommand 确认路径仍恒 exit 4**：定案即 PlanOnly 语义，未改成 RealWrite（vcom/oeminfo/downgrade 的真写路径依旧不存在，`Summaries` 保留 gate-only 字样仅指这三个命令，属准确描述）。
- **flash-xml `--strict-names` 的 warning 路径仍会尝试写未暴露的 identifier**（保守口径：默认只 warning 不跳过，按任务要求实现）；是否默认跳过留给人决策。
- README.md 无需改动的判断请编排者复核（仅核对了 19-22 行表述）。