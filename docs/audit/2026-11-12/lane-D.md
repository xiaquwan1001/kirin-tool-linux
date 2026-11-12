# Lane D — 测试覆盖与可验证性审计（2026-11-12）

范围：tests/ 下 16 个 harness、.github/workflows/*.yml、.gitlab-ci.yml。
方法：只读代码 + rg 取证，未跑构建，未改任何源码。

## 结论摘要（完成版，逐条见下）

1. [P1] FlashPolicy 的 CLI 子检查在二进制缺失时静默 SKIP——红线闸测试可整体蒸发而 CI 全绿（骨架期曾猜测两套 CI 名单不等价，逐行核对后撤回：同为 13 个 harness，等价）。
2. [P2] 16 个 harness 全部是自写 Check/throw 风格、失败确实非零退出——没有「只打印不校验」的假绿。
3. [P1] MCP confirm 错型（字符串）→ -32603 而非 -32602；schema additionalProperties 服务端不校验。
4. [P2] UserSettings 跨进程覆盖写 + 写盘失败静默，无测试锁定语义。

（本文件按进度追加，非一次性完成；结论摘要为完成版。）

---

### [P1] FlashPolicy 的 CLI 子检查在 CLI 二进制缺失时被静默 Skip
- 证据: tests/FlashPolicy/Program.cs:89-95（.github 与 .gitlab 两套 CI 名单经逐行核对均为同 13 个 harness、等价；风险点仅在 SKIP 分支）。tests/FlashPolicy/Program.cs:89-95：`if (cli == null) Skip("cli redline plan", ...)` —— CLI 二进制不存在时**打印 SKIP 并继续成功退出**。
- 影响: GitHub 上若 KirinTool.Cli 构建产物路径变化（如 net10.0→net11），FlashPolicy 的 redline 计划门（exit 4、--allow-redline 提示、plan JSON）整段静默不测，「看起来很绿但实际不校验」的正是最危险的红线写保护。对设备：flash-ota 红线闸回归不被 CI 拦截。对 CI：两套结果都可绿但覆盖不同。
- 建议修复: tests/FlashPolicy/Program.cs 在 cli==null 的 Skip 分支改为 `Environment.ExitCode = 1` 或用环境变量 `KIRIN_REQUIRE_CLI=1` 时 fail；或在 cli-mcp-ci.yml 的 FlashPolicy 步骤后加 `grep -c PASS` 级别的行数断言。无需真机。
- 置信度: 高（Skip 分支代码直接可读）。

### [P2] 16 个 harness 均为真 assert，无「打印-only」假绿；但 asserts 依赖顶层 throw→非零退出这一隐式机制
- 证据: 各 Program.cs 均用 `Check(...){if(!ok)throw new Exception(...);}` 或 `if(x!=y)throw`；顶层语句 throw 使 `dotnet run` 退出码非 0，CI 的 `set -euo pipefail` 会失败。FastbootReads/Program.cs:4-8、UsbTransport/Program.cs:3-4 同模式。例外：UsbUpdateProbe/VcomOpenProbe 是硬件探针（打印为主，返回码有语义），本就不在 CI。
- 影响: 无直接风险；但 harness 无测试框架，单个 Check 抛出即中止整个 harness，一个文件里后面的用例不再执行——失败报告定位靠人工读 stdout 的 PASS 行。
- 建议修复: 可选：在 CI 脚本里对每个 harness 统计 `PASS` 行数与最小阈值比较（防上游重构导致用例静默蒸发，例如某 Check 循环空转零次）。无需真机。
- 置信度: 高。

### [P1] MCP confirm 语义与 tools/call 错误映射没有硬件无关测试
- 证据: cli-mcp-ci.yml 的 MCP smoke 只测 initialize + tools/list（grep "result"/"flash_ota"），不调用 tools/call、不测 confirm 拒绝路径、不测错误→JSON-RPC error 映射。tests/ 下无 Mcp/McpConfirm harness。
- 影响: confirm 缺失/拒绝语义回归（例如把 confirm:false 当 true，或错误被吞成空 result）不会红 CI；flash_ota 通过 MCP 触发时闸门行为只有 FlashPolicy 的 CLI 侧覆盖，MCP 入口绕过闸门这类回归不可见。触发条件：任何 MCP 层改动。后果：对设备可能绕过写前确认。
- 建议修复: 新建 tests/McpProtocol（stdio 驱动 `KirinTool.Cli mcp`，与 cli-mcp-ci.yml smoke 相同方式）：(a) tools/call flash_ota 无 confirm 字段 → 期待 error object（断言 code/message 含 confirm/gate 字样）；(b) confirm:false → refused；(c) 未知 tool → JSON-RPC error；(d) 对 synthetic UPDATE.APP 走 plan-only 确认 gate=plan-only。全程无设备。预计 <20s。进 CI（GitHub+GitLab 双跑）。无需真机。
- 置信度: 高（smoke 内容可从 yml 直接读出）；具体 confirm 字段名待读 Cli/Mcp 源码后在该条下补。

### [P1] MCP tools/call 对 confirm 的非布尔值会变成 -32603 Internal error，且 inputSchema 的 additionalProperties:false 服务端不执行
- 证据: Cli/Mcp/McpServer.cs:191 `bool confirm = arguments?["confirm"]?.GetValue<bool>() == true;` —— 客户端传 "confirm":"true"（字符串）时 GetValue<bool> 抛 InvalidOperationException，落入外层 catch 回 -32603 而非 -32602 Invalid params。同文件 ToParsedArgs 对 schema 外的参数静默丢弃（additionalProperties:false 只写在 schema 里，服务端不校验）。
- 影响: MCP 客户端把 confirm 序列化成字符串是常见错误（LLM 工具调用尤其如此）；调用者得到 Internal error 会误判为服务端 bug 而不是参数错误。触发条件：任何 tools/call 带错型 confirm 或未知参数。对设备无直接风险（false 路径仍是 gate）。
- 建议修复: McpServer.cs CallToolAsync 开头显式校验 confirm 的 JsonValue 类型（非 bool → -32602），并对未知 argument 名返回 -32602；新增 tests/McpProtocol 用例覆盖（见清单 #1）。无需真机。
- 置信度: 高。

### [P1] GatedWriteCommand 三条命令的 confirmed 路径（--yes/confirm:true 仍不得写设备）完全没有测试
- 证据: Cli/Commands/WriteCommands.cs:14-80（GatedWriteCommand：confirmed 时 status=confirmed-but-write-path-not-implemented，恒 exit 4）。tests/ 下只有 FlashPolicy 覆盖 flash-ota 的**未确认**红线门；vcom_unlock / oeminfo / downgrade_enable 的 confirm:true 分支无任何 harness 触达。FlashXmlCommand 的 missing-images 门（exit 5 先于 redline 门）也无测试。
- 影响: 这是最敏感的安全不变量（「确认了也不许写」）。若未来有人把 GatedWriteCommand 的写路径接通或改掉 exit 4，CI 全绿。触发条件：任何 WriteCommands 改动。后果：对设备可能从「计划模式」意外变为真实写入。
- 建议修复: 新建 tests/WriteGates（进程外驱动 KirinTool.Cli，与 FlashPolicy 的 CLI 子检查同模式）：对 vcom-unlock --cpu hisi990 --yes --json、oeminfo --model A --region b/c --input <fake.img> --yes、downgrade_enable --yes 各断言 exit==4、JSON gate.status=="confirmed-but-write-path-not-implemented"、applied==false；另补 flash-xml 对缺图 XML 的 exit 5。全程无设备，<10s。进双 CI。无需真机。
- 置信度: 高。

### [P1] OtaFlashRunner.BuildPlan/ResolveNamesAsync/别名表/合并 super/gzip 解压零覆盖（纯函数，硬件无关却没测）
- 证据: Cli/Commands/OtaFlashRunner.cs:30-45（NameAliases：hisiufs_gpt→ptable、efi→ptable、ufsfw→ufs_fw、erecovery_ramdis→erecovery_ramdisk、vbmeta_hw_produc→vbmeta_hw_product）；BuildPlan（ShouldSkip/--partitions/--exclude/super 合并 warning）；DecompressIfGzipAsync（0x1f8b 魔数）。tests/ 无一处直接构造 OtaPlan 或断言别名映射；ResolveNamesAsync 也未测。
- 影响: 别名拼错会让分区解析成 unresolved 而被静默跳过（无 --strict-names 时只打 warning），用户以为刷全了实际漏分区。触发条件：改别名表/BuildPlan 过滤逻辑。后果：设备缺 ptable/erecovery 等关键分区，可能变砖级别。
- 建议修复: 新建 tests/OtaPlan：复用 tests/Extractor 同款合成 UPDATE.APP fixture（可提取为共享 helper），断言 (a) HISIUFS_GPT/EFI/UFSFW/ERECOVERY_RAMDIS/VBMETA_HW_PRODUC 的计划分区名；(b) --partitions/--exclude（含按源文件名 exclude）；(c) super×3 合并为单条 + warning；(d) gzip 魔数 payload 解压后字节一致；(e) ResolveNamesAsync 用 fake-fastboot 返回 ptable 列表断言大小写折叠与 unresolved 输出。<15s，进双 CI。无需真机。
- 置信度: 高。

### [P2] UserSettings：进程内有 lock + 原子 rename，但跨进程（GUI + CLI 同时改 settings.json）后写整文件覆盖、写盘失败被静默吞掉——无测试锁定该语义
- 证据: Services/UserSettings.cs:368-371（tmp + File.Move overwrite:true，进程内安全）；WriteToDiskLocked 的 catch(Exception){} 静默；无文件锁/合并。GuiQol harness 只测了单线程 roundtrip 与损坏隔离。
- 影响: GUI 与 kirintool CLI 并发运行时丢失对方写入（后写整文件赢）。后果轻（配置丢失），但「写失败静默」意味着只读 home 下设置永远不保存且无任何提示。
- 建议修复: WriteToDiskLocked 失败时向 Application log 写一行诊断（与 ProcessRunner.WriteDiagnostic 同通道）；可选加跨进程文件锁。测试：tests/UserSettings 并发 8 线程 × Update() 后断言 json 可解析且无 .tmp 残留；再模拟目录只读断言不抛异常。<10s。无需真机。
- 置信度: 高。


## Harness 逐个评估（16 个）

全部为「顶层语句 + Check 抛异常」风格，失败即非零退出，CI 用 set -euo pipefail 会红。**没有「只打印不校验」的假绿 harness。**

| Harness | 校验方式 | 真实强度 | 备注 |
|---|---|---|---|
| Extractor | throw | 强 | 覆盖截断/损坏/路径穿越/取消/竞态/4GiB+，是标杆 |
| FlashPolicy | throw + SKIP | 强但有洞 | CLI 子检查在二进制缺失时静默 SKIP（发现 #1） |
| StableParity | throw | 强 | 冻结哈希 + Windows 字节级 golden |
| FastbootReads | throw | 强 | 冲突值/失败响应拒绝 |
| GuiQol | throw | 中 | 无并发写用例（发现 #7） |
| Kirin9000 | throw | 强 | fake fastboot 记录 argv，断言不写 |
| OemInfoUi | throw | 中 | 只测输入校验对话框 |
| UdevNotice | throw | 强 | 含 bash -n 语法检查 |
| UsbDiagnostics | throw | 强 | Fake transport + 与 2.4.7 的 wire 字节对比（需传 baseline dll 参数才启用） |
| UsbRedlineGate | throw | 强 | allow-redline 三分支全测（写前拒绝有 list.txt 存在性证据） |
| UsbTransport | throw | 强 | 分片/超时/半写不重放 |
| UsbUpdateUi / VcomPromptUi / VisualParity / OemInfoUi | throw | 中 | UI 渲染/分类器；VisualParity 依赖字体，故意不进 CI |
| UsbUpdateProbe / VcomOpenProbe | 返回码 | 探针 | 需硬件，正确地不在 CI |

**看似绿但不校验的点**：FlashPolicy 的 SKIP 分支（#1）；CI 的 MCP smoke 用 grep 而非 schema 校验（只证明 "result" 字样存在，不证明 isError/exitCode 语义）。

## 可直接落地的硬件无关测试清单（按优先级）

1. **tests/McpProtocol**（P1，进双 CI）：stdio 驱动 KirinTool.Cli mcp；构造 (a) tools/call 未知 tool → isError=true、_meta.exitCode=1；(b) confirm:"true"（字符串）→ 期待 -32602（修 #4 后）；(c) confirm 缺省 → flash_ota 对合成 UPDATE.APP 返回 gate=plan-only、applied=false、exit 4；(d) flash_ota 对含 OEMINFO 的合成包（无 allow-redline）→ exit 4 且 plan.redlines==["OEMINFO"]。断言解析 JSON 而非 grep。<20s。
2. **tests/WriteGates**（P1，进双 CI）：见 #5。
3. **tests/OtaPlan**（P1，进双 CI）：见 #6。
4. **FlashPolicy CLI 子检查改硬失败**（P1，改现有 harness）：cli==null 时置 Environment.ExitCode=1 或要求 KIRIN_FLASHPOLICY_CLI 环境变量存在。<1s。
5. **tests/UserSettings**（P2，进双 CI）：见 #7。
6. **flash-ota 取消/并行 staging**（P2，进双 CI）：对大合成包发 CancellationToken，断言 .kirintool-cli-tmp 下无残留；两个进程同秒启动 work-dir 相同 → 断言互不覆盖（当前用时间戳目录，同秒即冲突——建议目录名加 PID/GUID，属行为修改需编排者批准）。<30s。
7. **CI 门禁等价断言**（P2，纯 CI 脚本）：在两套 CI 里各加一步，用同一份 tests/suites.txt 单一事实源循环跑 harness，杜绝两份名单漂移。

## CI 配置问题

1. **GitHub build.yml 是 dispatch-only**、release-linux.yml **不跑任何测试且无 timeout-minutes**：真正的门禁只有 cli-mcp-ci.yml。GitLab .gitlab-ci.yml 声称「与 cli-mcp-ci 等价」，列表当前确实同为 13 个 harness，但两份名单是复制粘贴维护的（漂移风险，见清单 #7）。
2. **谁的结果为准**：仓库内无任何机制声明权威（GitHub 无 required checks 证据、GitLab 无 merge request approval 配置可查）。建议在 README/docs/cli-mcp.md 明写「以 GitHub cli-mcp-ci 为准，GitLab 为快速副本」，并把 harness 名单收进单一文件。
3. **GitLab shell executor**：注释称「jobs 通过 stages/needs 串行」——只对同一 pipeline 成立；不同 pipeline 并发时 GitLab 按 concurrency-index 分目录，bin/obj 不冲突，但 **~/.nuget/packages 共享**（NuGet 并发 restore 通常安全）以及 `log/`、`artifact/`、`dist/` 若任务中途失败会残留到下次 checkout（packaging job 有 rm -rf，build/tests 没有；建议 build/tests 开头也清理）。GitLab tests job `dotnet run` 会重新增量编译 tests/*/obj——与编排者本地构建共享同一路径的风险在于 runner 的 checkout 若落在用户同一 clone 内需确认 $CI_BUILDS_DIR 与 ~/src/kirin-tool-linux 分离。
4. **artifact 体积/timeout**：两边都做了 25M 单文件裁剪 + loaders/fastboot 排除，合理；GitHub build-cli timeout 20m、packaging-smoke 10m、GitLab build 20m/tests 30m/smoke 15m/packaging 10m，均有界。release-linux.yml **无 timeout** 是唯一缺口（P2）。
5. **缓存**：两边都没有 NuGet 缓存配置（GitHub 无 actions/cache；GitLab 靠宿主 warm cache）。GitHub 每次冷 restore 慢但正确；不构成正确性问题（P2）。

## 结论汇总

- **P0：0 条**（未发现会立刻写坏设备或绿但完全失守的测试洞）。
- **P1：4 条** —— #1 FlashPolicy SKIP、#4 MCP confirm 错型、#5 GatedWriteCommand confirmed 路径零覆盖、#6 OtaFlashRunner 别名/计划零覆盖。
- **P2：3 条+** —— #7 UserSettings 并发/静默写失败、harness 无用例数下限、release-linux 无 timeout 等 CI 细节。

**最值得先修的 3 条**：
1. #1 FlashPolicy 的 SKIP 改硬失败——一行改动，堵住红线闸测试被静默跳过的通道。
2. #5 tests/WriteGates（confirmed 路径恒 gate-only）——守护「确认≠写入」这一最高危安全不变量。
3. #6 tests/OtaPlan（别名表/BuildPlan）——漏刷关键分区的静默失败最接近「变砖」后果。

