# Lane C — 行为对照（社区/上游/本机经验）与文档漂移

日期：2026-11-12。范围：代码/文档/实际行为三方不一致、社区已知坑对照、华为 fastboot/USB Update 行为预检、打包/CI 风险。只读审计，未改任何源码。

---

### [P1] 文档声称 flash-ota/flash-xml "永不真正写入、一律 exit 4"，实际 --yes 后会真实刷写

- 证据: docs/cli-mcp.md 第 25-27 行：「写操作（第二批，仅骨架 + 闸门，永不真正写入）：…即使给了 --yes 也只打印 confirmed-but-write-path-not-implemented，一律 exit 4」。而 Cli/Commands/WriteCommands.cs:196-252（flash-ota）在 confirmed 后调用 OtaFlashRunner.ExecuteAsync 真实下发 fastboot flash；WriteCommands.cs:382-417（flash-xml）同样调用 FlashPartition 真写。README.md:19-22 却已明说「a real Full-OTA write path (flash-ota, verified end to end on P40 Pro / ELS-AN00: 46/46 partitions)」。另 WriteCommands.cs:12 顶部注释「intentionally not implemented in this build, so they still exit 4」已过期。
- 影响: 用户/自动化按 cli-mcp.md 或 MCP instructions（Cli/Mcp/McpServer.cs:118 "are gate-only in this build, exit 4, nothing is written"、:153 "true still does not write in this build"）误以为安全，脚本化调用 confirm:true 后真机被整包刷写。对设备：不可逆分区写入；对用户：数据丢失；对 CI：若 CI 真有设备则可能误刷。
- 建议修复: 更新 docs/cli-mcp.md 与 McpServer.cs:118/153 的说明（改文档侧，标注 flash-ota/flash-xml 已实装真写，仅 vcom-unlock/oeminfo/downgrade-enable 仍 gate-only——需逐一核实后三项）；同步删除 WriteCommands.cs:12 的过期注释。不需要真机验证（纯文档）。
- 置信度: 高

### [P2] flash-xml 对 Huawei loader 的大小写处理与 flash-ota 不一致，可能刷错名

- 证据: WriteCommands.cs:403-405 flash-xml 写死 `(partition.Identifier ?? "").ToLowerInvariant()` 并注释 "Huawei loaders match partition names case-sensitively; GPT names are lower case"；flash-ota（同文件 :222-230）则先经 ResolveNamesAsync 用 getvar/all 解析、loader 未暴露的分区默认跳过（:224 注释 temporary loader 分区表可窄于 GPT）。flash-xml 无此解析/跳过逻辑。
- 影响: 若 XML 里的 identifier 与临时 loader 暴露名不完全一致，fastboot 直接返回 FAILED，单分区失败但前面已写入的分区不回滚——部分刷写状态。触发条件：XML 分区集超出 loader 暴露表。设备：处于混合版本状态；用户：需重刷；CI：无。
- 建议修复: 让 flash-xml 复用 OtaFlashRunner.ResolveNamesAsync 的解析与 skip/strict 语义（改 Cli/Commands/WriteCommands.cs）。建议真机验证（ELS loader + 含 loader 未暴露分区的 XML）。
- 置信度: 中（代码路径明确，但未实机复现失败样例；可用 getvar all 对照测试 XML 验证）

### [P2] cli-mcp.md 的 fastboot 解析顺序与 KIRIN_FASTBOOT 优先级待核对 BinaryLocator 实现

- 证据: docs/cli-mcp.md 第 29 行声称解析顺序 "KIRIN_FASTBOOT → 程序目录及 fastboot/ → PATH → /usr/bin、/usr/local/bin"。（待核对 Cli/BinaryLocator 实现）
- 影响: 若实现与文档不符，用户设置 KIRIN_FASTBOOT 指向 Huawei-parity fastboot 却被别的二进制接手，导致 getvar partition-size 等全部失败（Google fastboot 37 已知问题），用户误判设备故障。
- 建议修复: 核对 Cli/ 下 BinaryLocator；不一致时改文档或改实现，并保证 doctor 输出实际选中的 fastboot 路径与版本。不需真机验证。
- 置信度: 低（待读 BinaryLocator 源码确认；验证方法：KIRIN_FASTBOOT=/bin/false KirinTool.Cli doctor 看报错来源）

---

**更正第 3 条**：已核对 Cli/BinaryLocator.cs:21-67，实现顺序与 docs/cli-mcp.md 完全一致（env → AppContext.BaseDirectory 及子目录 → /usr/bin、/usr/local/bin → PATH），且 override 值若不是文件路径还会在 PATH 上解析。该条**撤销**，不构成问题。

### [P2] dump 命令文档只写 oem dump-emmc，实际服务先 emmc 后 storage 回退，且在临时 loader 上两者都不可用但无预检提示

- 证据: docs/cli-mcp.md:22 与 Cli/Commands/ReadCommands.cs:458 都只说 `oem dump-emmc`；Services/FastbootFlasherService.cs:58-82 实际先 dump-emmc、失败后自动改 dump-storage。P40 Pro 实测（本机项目记忆）：临时 loader v0.5 上 dump-emmc、dump-storage、getvar all 全部 invalid command。
- 影响: 用户拿到 18d1:d00d 临时 loader 后跑 CLI dump，得到晦涩的 "The Huawei fastboot client normally writes ... on the host..." 失败文案，误以为 fastboot 客户端坏了；doc 未提示该命令依赖机内 bootloader 或支持 dump 的 loader 代次。
- 建议修复: 改 docs/cli-mcp.md:22 与 ReadCommands.cs:458 Summary，注明"先 dump-emmc 后 dump-storage 回退；部分临时 loader（如 hisi990 v0.5）两者均不支持"；可在 dump 失败文案里加一句"若当前是 VCOM 临时 loader，属预期失败"。不需真机验证。
- 置信度: 高

### [P2] udev 规则 GROUP="adbusers" 是 Arch 惯例，deb 的目标发行版（Debian/Ubuntu）默认无此组

- 证据: packaging/51-kirin-tool-fastboot.rules:4 `GROUP="adbusers"`（tty 规则 GROUP="uucp"，uucp 在 Debian 存在）；PKGBUILD（Arch）配套合理；build-deb.sh:69 同一规则装进 deb，postinst 只打印"未发现 adbusers 组"提示。docs/fork-release.md:35 也写 "add yourself to adbusers"。
- 影响: Ubuntu/Debian 上 adbusers 组不存在，非 logind 桌面会话（ssh/容器）下 fastboot 节点不可读写；uaccess 在桌面会话兜底所以桌面用户无感，但无头场景（CI、服务器接真机）会莫名 permission denied。
- 建议修复: build-deb.sh 打包时对规则做 sed，把 deb 内的 GROUP=adbusers 改成 plugdev（或在 postinst 里 getent adbusers 失败时改用 plugdev）；同步改 docs/fork-release.md。不需真机验证，装 deb 后 ls -l /dev/bus/usb 即可核对。
- 置信度: 高

### [P2] docs/stable-release.md 仍是 2.4.8 上游文档，安装命令指向 fork release 页不存在的 2.4.8 产物

- 证据: docs/stable-release.md:1 标题 "Kirin Tool Linux 2.4.8"；:89 pacman -U ./kirin-tool-linux-2.4.8-1-x86_64.pkg.tar.zst、:99-100 AppImage 同为 2.4.8 文件名。实际 fork release（v2.4.12）只有 AppImage/deb/tar.gz/CLI 四件，无 pacman 包；README:18 已写 "current release v2.4.12"。
- 影响: 用户照抄 stable-release.md 的 pacman/2.4.8 文件名下载会 404；fork 里 pacman 路径不存在（build.yml 的 pacman job 是 dispatch-only，fork 发布走 release-linux.yml）。
- 建议修复: 在 stable-release.md 顶部加 fork 说明（本文档描述继承的上游 2.4.8 基线；fork 发布产物见 fork-release.md，2.4.9 起无 pacman 包），或把示例版本号改为 2.4.12 并去掉 pacman 一节。纯文档。
- 置信度: 高

### [P2] 社区已知坑：gzip 压缩的 BD super.img 全仓无处理与提示

- 证据: 本机项目记忆（BD ELS-AN10 包 super.img 是 gzip，Kirin-Tool 不解压 → ultraflash.c:89 Unexpected end-of-file，GUI 刷显示 45/46）。rg 证实 Utils/、Services/、Cli/ 无 gzip 处理；SuperMerger 只处理两段 sparse 合并，Cli/Commands/OtaFlashRunner.cs:236-270 已覆盖多段 super 合并（该子坑 fork 已修）。
- 影响: 用户刷 BD（板级/Pro+）包时 GUI 显示 45/46 完成但 super 残缺，系统起不来；无任何预检警告"该 super 条目是 gzip 压缩，请先解压"。
- 建议修复: 在 Utils/UpdateApp 解析或 OtaFlashRunner/FlashPolicy 层加 gzip 魔数（1f 8b）检测：检测到时拒刷并提示先解压（不改 loaders/fastboot 语义）。需要真机（BD 包）验证。
- 置信度: 高（魔数检测简单；失败样例已有社区与本地记录）

### [P2] fork-release.md 的 CLI 体积声明已过期（~49 MB → 实测 52.3 MB）

- 证据: docs/fork-release.md:14 "~49 MB compressed bundle"；v2.4.12 实际资产 kirin-tool-cli-2.4.12-linux-x86_64 = 52,312,345 B（≈52.3 MB）。
- 影响: 仅文档轻微失真，用户校验预期偏差。
- 建议修复: 改为 "~50 MiB" 或去掉具体数字。纯文档。
- 置信度: 高

---

## 社区/上游已知问题对照

| 上游问题 | 状态 | 说明 |
|---|---|---|
| Redminote11tech/kirin-tool-linux #1/#3（进度框滚不到底、非模态对话框被遮、超时后 progressDialog 泄漏；CLI/MCP 提案） | 已修/已实现且超出 | UI/ProcessDialogUapp.axaml.cs:350 已用 ContainerFromIndex()+TranslatePoint()（保留算术兜底）；Services/DialogService.cs:80 已 dialog.Activate()；CLI/MCP 已落地且 flash-ota/flash-xml 从 gate-only 升级为真写（见第 1 条 P1） |
| kethily-daniel/Kirin-Tool #1（Kirin 990 4G 支持） | 部分适用 | fork 载有 loaders/hisi990 套件且 CLI 在 ELS-AN00 端到端验证；README 设备支持表已如实声明 Mate40 Pro 拒写等限制，无虚假声明 |
| kethily-daniel/Kirin-Tool #2（P40 Pro 用 Software TP 后变砖） | 不适用（仅日志改进） | fork 继承 2.4.8 的 TP 日志/错误保留改进（stable-release.md:9-14），TP 变砖风险本身不由此修复；README WARNING 已覆盖免责 |
| 华为 fastboot 已知行为：getvar partition-size 在 Google fastboot 上失败 | 已覆盖 | deb control 明文要求捆绑 fastboot（Google platform-tools 不实现该查询）；BinaryLocator 优先程序目录自带 fastboot |
| 临时 loader 内存态（reboot 即失效）、oem get-bootinfo=unlocked 假象 | 已覆盖 | MainWindow.axaml.cs:2032-2103 把临时 loader 的 already-unlocked 判为 no write 并解释非真解锁；README:23-25 同步声明 |
| 临时 loader 分区表（90 项工厂表）≠ 真实 GPT | 已覆盖 | WriteCommands.cs:222-230 flash-ota 默认跳过 loader 未暴露分区，--strict-names 回退全有全无（但 flash-xml 无此逻辑，见第 2 条） |
| 多段 sparse super（取大段会写零 2.1 GiB） | 已覆盖（CLI 与 GUI） | OtaFlashRunner.cs:236-270 调 SuperMerger.MergeSuperImages；GUI Full OTA 走 Utils/SuperMerger 同路径 |
| gzip BD super 未处理 | 未修 | 见上文 P2 |
| BD super 等大误判（XDA Dampen59 boot 47104KB 报错） | 不适用 | 属其他 BD 版本差异，本机实测 boot 25 MB 匹配；README 未做跨 BD 版本承诺 |

## CI / 打包其余核对（无新增问题项）

- glibc 2.31：release-linux.yml 全程 ubuntu:20.04 容器 + build-appimage.sh:12-15 硬闸（host glibc > 2.31 直接退出）+ check-portable-elf.py 校验，闭环成立。
- deb Depends：ICU/OpenSSL 变体表（build-deb.sh:24-35）与 fork-release.md 说明一致，check-depends.sh 提供 apt 模拟预检，无漂移。
- USB Update userspace：docs/usb-update-userspace.md 与 native/usb-update + libusb 1.0.30 netlink 打包策略（build-appimage.sh）一致。
- artifact 大小：v2.4.12 实测 AppImage 170 MB / deb 157 MB / tar.gz 182 MB / CLI 52.3 MB；用户提及的"413"实为 SHA256SUMS-2.4.12.txt 的字节数（411–413），非异常。fork-release.md 未对前三个产物声明具体体积，无矛盾。

### [P2] cli-mcp-ci.yml 顶部注释与 build.yml 现状矛盾；触发分支列表落后于实际开发分支

- 证据: .github/workflows/cli-mcp-ci.yml:6-8 "the upstream release pipeline (build.yml) only triggers on v* tags"——而 build.yml:7-9 现为 `on: workflow_dispatch`（fork 已注释说明 dispatch-only）。cli-mcp-ci.yml:25-30 只在 main / feat/cli-and-mcp-entry 触发，README:29 提到的 feat/ota-flash、fix/gui-scroll-and-dialog-order 分支不在列表内，这些分支的 push/PR 不跑 CI。
- 影响: 开发者在 feat/ota-flash 等分支上以为有 CI 把关，实际零检查；注释误导新维护者对 build.yml 触发方式的理解。
- 建议修复: 改 cli-mcp-ci.yml 注释与 branches 列表（补 feat/ota-flash、fix/gui-scroll-and-dialog-order，或改成所有 feat/*）。纯 yml 文档级修改，不需真机验证。
- 置信度: 高

## 结论汇总

- P0：0 条（未发现会立即损坏设备或造成不可逆数据损失的代码缺陷；第 1 条 P1 因文档口径错误具有同样的用户误导后果，建议按 P0 处理优先级执行）。
- P1：1 条 —— cli-mcp.md 与 MCP instructions 声称写命令 gate-only，实际 flash-ota/flash-xml --yes 真写（文档侧修复即可）。
- P2：7 条 —— flash-xml 缺少 loader 分区名解析/跳过逻辑；dump 文档与回退行为不符且缺临时 loader 预检；udev GROUP=adbusers 在 Debian/Ubuntu 不存在；stable-release.md 2.4.8 产物名 404；gzip BD super 无检测；fork-release.md CLI 体积过期；cli-mcp-ci.yml 注释/触发分支过期。

最值得先修的 3 条：

1. **P1 flash-ota 文档口径**（docs/cli-mcp.md + McpServer.cs:118/153 + WriteCommands.cs:12 注释）：纯文档改动、五分钟完成，却是唯一会让用户/agent 在"以为不会写"的心智下真实刷机的条目。
2. **P2 flash-xml 复用 ResolveNamesAsync**：唯一需要改行为的条目，但只是把 flash-ota 已验证的解析/跳过逻辑对齐，消除部分刷写（混合版本）风险；建议真机回归。
3. **P2 deb udev GROUP=plugdev 替换**：一行 sed 级打包修复，直接影响无头/非桌面用户的可用性，且 fork-release.md 的指引要同步。


