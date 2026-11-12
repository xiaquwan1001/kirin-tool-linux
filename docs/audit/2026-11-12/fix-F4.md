# fix-F4：硬件无关测试 + 双 CI 门禁收口（进行中）

状态：骨架已建，逐项追加。未跑 dotnet build/test（编排者统一跑），以下只记编辑与静态核对。

## 0. 前置事实核对

- OtaFlashRunner.BuildPlan / OtaPlan / OtaFlashRunner 均为 `internal`（Cli/Commands/OtaFlashRunner.cs:49,66,76），
  仓库无 InternalsVisibleTo；约束禁止为可见性改生产代码 → OtaPlan harness 走 CLI `flash-ota --json` 的 plan 输出断言。
- **别名表断言不可行（待编排者决策，见 §OtaPlan）**：BuildPlan 里 `Partition = partition.Name` 用的是归档原始名，
  HISIUFS_GPT→ptable 等别名只在 ResolveNamesAsync（需真机 ptable）里生效，硬件无关环境下 plan JSON 观测不到别名。
- GatedWriteCommand（vcom-unlock/oeminfo/downgrade-enable）--json 时 exit=4、applied=false、
  gate.status 为 "plan-only"（无 --yes）或 "confirmed-but-write-path-not-implemented"（有 --yes）（Cli/Commands/WriteCommands.cs:14-81）。
- flash-xml：missing 图检查先于红线门，返回 ExitCodes.FileOrVerify=5（WriteCommands.cs:385-390 附近）。
- MCP：未知 tool → isError=true、_meta.exitCode=1（Cli/Mcp/McpServer.cs ToolResult）；exit 4 当前 isError=false、
  confirm 传字符串 "true" 当前会抛 InvalidOperationException → -32603。(c)(d)(f) 三条按任务书预期写成 F1 修复后成立的断言。

## 1. tests/suites.txt
- [ ] 新建：16 个硬件无关 harness 名单（含新增 WriteGates/McpProtocol/OtaPlan），两套 CI 单一事实源。

## 1. tests/suites.txt — 完成
- tests/suites.txt：16 行（FlashPolicy…Kirin9000 13 个既有 + WriteGates/McpProtocol/OtaPlan），
  头注释写明排除项（VcomOpenProbe/UsbUpdateProbe/VisualParity）。

## 2. tests/WriteGates — 完成（WriteGates.csproj + Program.cs，123 行）
- 三个 csproj 同构：net10.0 exe + ProjectReference ../../Kirin-Tool.csproj（与 FlashPolicy/StableParity 一致）。
- CLI 二进制定位逻辑沿用 tests/FlashPolicy 的 4 路径探测；找不到直接 throw（硬失败，无 Skip）。
- 断言清单（每条 PASS 打印，失败 throw）：
  1. `vcom-unlock --cpu hisi990 --yes --json` → exit 4；JSON applied=false、gate 字符串 ∈ {plan-only, confirmed}
     （F1 已把 gate 改为机器可读字符串：--yes 前是 plan-only、确认后是 confirmed，均等于"没写"）。
  2. `oeminfo --model ELS-TN00 --region cn --input <合成块> --yes --json` → 同上；合成 OEMINFO 为单个 0x400 块：
     "OEM_INFO" 魔数 + 版本 6 + 条目 id 0x5b（OemInfoEditor v6 目标 id，保证本地内存转换成功）。
  3. `downgrade-enable --yes --json` → 同上。
  4. flash-xml：合成 flashing.xml（boot + oeminfo 两条 image，镜像文件都不存在），无 --yes 无 --allow-redline
     → exit 5（先于红线门）、stderr 含 "missing" 且不含 "redline"、JSON applied=false、missingImages==2。
- 预计耗时：<3 s（4 次 CLI 进程启动）。

## 3. tests/McpProtocol — 完成（McpProtocol.csproj + Program.cs，158 行）
- 一次 `cli mcp` stdio 会话，6 个请求按 id 配对；stdin 关闭后进程应 exit 0。
- 断言：(a) 无 initialize 直接 tools/list 返回含 flash_ota 的 tools；(b) 未知 tool isError=true、_meta.exitCode=1；
  (c) confirm 传字符串 "true" → error.code=-32602；(d) allow-redline 参数 → -32602（redline override is CLI-only）；
  (e) flash_ota 无 confirm（合成纯 BOOT 包）→ isError=false、_meta.exitCode=4、structuredContent.applied=false、
  gate=="plan-only"；(f) 合成含 OEMINFO 包 + confirm:true、无 allow-redline → isError=true、
  structuredContent.gate=="refused"、applied=false。
- 注：本 lane 期间核对到 F1 的 McpServer/WriteCommands 改动已在同一工作树上落地（-32602 参数校验、
  gate 字符串、refused → isError=true + 结构化 JSON），上述 (c)(d)(f) 与当前代码契约一致，不再是预期性断言。
- 合成 UPDATE.APP 用与 tests/Extractor 相同的最小容器写法（12 字节前缀 + 102 字节头 + 55 AA 5A A5）。
- 预计耗时：<3 s（1 次 CLI 进程）。

## 4. tests/OtaPlan — 完成（OtaPlan.csproj + Program.cs，182 行）
- 可见性：OtaFlashRunner/OtaPlan 均 internal 且无 InternalsVisibleTo，禁止改生产代码 → 全部经 CLI
  `flash-ota --json` 的 plan 输出断言（不直接引用类型，csproj 仅挂 Kirin-Tool.csproj 保持风格）。
- 断言：--partitions BOOT 只留 BOOT 且其余进 skipped；--exclude CUST,VBMETA 只留 BOOT；两个 SUPER 条目
  合并为单条（size=片段和、source=="Merged super"、顺序 BOOT,CUST 保留、warnings 含 "super is split across 2"）；
  gzip 魔数 payload：plan size==压缩后长度，且 `extract` 落盘 GZBOOT.img 经 GZipStream 解压与原始 5000 字节随机数据一致。
- **别名表断言撤销（误判/不可观测）**：BuildPlan 直接用归档原始名（OtaFlashRunner.cs `Partition = partition.Name`），
  HISIUFS_GPT→ptable/UFSFW→ufs_fw 等映射只在 ResolveNamesAsync（需真机 fastboot getvar ptable）生效，
  硬件无关环境无法断言。 harness 头注释已写明。需要人决策：若要硬件无关覆盖别名，需把别名解析前移到 BuildPlan
  或加 InternalsVisibleTo（两者都改生产行为，超出本 lane 权限）。
- 预计耗时：<5 s（5 次 CLI 进程，含一次 extract）。

## 5. CI — 完成
- .github/workflows/cli-mcp-ci.yml：
  - 顶部注释改为"build.yml 在本 fork 是 workflow_dispatch-only，无分支/标签触发"（原文称它触发 v* 标签，已过期）。
  - on.push.branches: main + 'feat/**' + 'fix/**'（pull_request 保持 main，防噪声）。
  - 三个分散的 Test 步骤（FlashPolicy、StableParity、硬件无关循环）合并为一步：while read -r t 读
    tests/suites.txt（跳过空行/# 注释），跑在两个 build 之后，WriteGates/McpProtocol/OtaPlan 用上面产出的 CLI 二进制。
- .github/workflows/release-linux.yml：release job（原先唯一没有 timeout 的 job）加 timeout-minutes: 45；
  build job 原有 120 不动。
- .gitlab-ci.yml：
  - build 步骤 staging 前 rm -rf artifact dist publish-fake（共享 checkout 的上次失败残留）。
  - tests job 改为 while read -r t 读 tests/suites.txt，与 GitHub 同源；注释更新。
  - smoke job 开头 rm -f /tmp/help.txt /tmp/doctor.json /tmp/mcp.out /tmp/mcp.err（清自身输出；
    stub 目录本就在 mktemp 下）。
- packaging job 已有 rm -rf "$P" dist，未动。

## 6. 未做 / 需要人决策
1. **别名表断言（OtaPlan 任务书第 1 条）撤销**：BuildPlan 不做 HISIUFS_GPT→ptable 等别名映射
   （OtaFlashRunner.cs BuildPlan 用 partition.Name 原始名；别名在 ResolveNamesAsync，需真机）。
   硬件无关环境不可观测，未硬造断言。如需覆盖：把别名前移进 BuildPlan 或加 InternalsVisibleTo，都改生产代码，需决策。
2. 无（原记的 (c)(d)(f) F1 依赖已消除：F1 改动已在树上，断言与当前契约一致）。WriteGates 的 gate 断言
   也已同步为 F1 后的字符串 gate（plan-only/confirmed），不再读 gate.exitCode/gate.status。
3. oeminfo 测试用合成 OEMINFO（v6 + 条目 id 0x5b）依赖 OemInfoEditor 的本地内存转换路径，无设备/无 fastboot 调用
   （FastbootClient 只构造不连接）。静态核对成立，未实跑。
4. 未跑 dotnet build/test（按约束，由编排者统一跑）；三份 Program.cs/csproj 与三份 yml 只做了逐 hunk 回读核对，
   语法与缩进静态检查通过。三套新增 csproj 引用 ../../Kirin-Tool.csproj，dotnet run --project 会顺带重建主工程——
   CI 顺序为 build→test，无影响；本地手跑单套件会多花一次主工程构建时间。
