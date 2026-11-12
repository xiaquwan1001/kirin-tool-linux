# F8：GitHub 介绍页面整理（v2.4.13 真实状态）

状态：进行中。逐项追加。

## 1. 现状通读

- README.md（v2.4.12 版）：已有徽章/下载/文档表/credits，但定位句是一段 5 行超长段落；含 "current release v2.4.12" 旧版本号；无 Safety model、无 Screenshots、无 Verified on hardware 一节；安装段文件名写死 2.4.12 三处。
- docs/cli-mcp.md：gate 语义表已与 F1 修复后一致（WriteKind 三态、gate 字符串、MCP 禁 allow-redline -32602、exit 0-5）——**不动语义表**，只在头部补入口/安装链接与示例（任务书允许的层面）。
- lane-A/B/C/D + fix-F1..F4 已确认 v2.4.13 实际状态：红线闸 GUI+CLI+MCP 三处、gate 三态、MCP 禁红线自决、OKAY 判定收敛、SN/FRP 确认框、USB Update 红线差集再确认、全局异常兜底、deb/AppImage/tar.gz/CLI 自建发布、.NET 10、双 CI；电量闸门与 pre-op 备份为 TODO（文档写"计划中"）。
- docs/INDEX.md 不存在 → 新建；docs/features.md、docs/screenshots.md 不存在 → 新建。
- 旧 "gate-only in this build" 整体说法：代码侧 F1 已清；stable-release.md 顶部 fork note 已注明 2.4.8 旧文件名"不再存在，勿照抄"（= 继承基线口径），README 不再复述旧安装文件名。

## 2. README 重写（完成）

只改 README.md。新结构（30 秒可读）：居中 icon + 一句话定位（GUI+CLI+MCP 同一套写安全闸）→
4 徽章（release / release build / license / platform，全部指向 xiaquwan1001/kirin-tool-linux）→
导航行 → WARNING → **What this fork adds over upstream**（8 行对照表：CLI+MCP、flash-ota 真写 46/46+22s、
红线闸三处、OKAY 收敛、SN/FRP/USB Update 二次确认、全局异常兜底、自建四格式、双 CI .NET 10）→
**Safety model**（真写命令 vs PlanOnly、红线清单、CLI --allow-redline / GUI 确认框 / MCP -32602、
gate 四态、电量横幅=计划中硬闸未实现、pre-op 备份=计划中未实现，均明确标注 planned not implemented）→
**Install**（deb/AppImage/tar.gz/CLI 四行，文件名用 <ver> 占位符 + plugdev/uucp/adbusers 提示）→
**CLI & MCP quick start**（5 条真实命令 + MCP JSON 配置片段，路径 /path/to/ 占位）→
**Screenshots**（指向 docs/screenshots.md 占位）→ **Verified on hardware / Not verified** 两栏表
（P40 Pro ELS-AN00 46/46 + 22s；未验证：其他机型、BD gzip super、990 4G、9000 rescue、USB Update 硬件等效）→
Features 表（指向 features.md）→ Documentation 表 → Build from source → Issues →
**Credits and license**（保留上游 BSL 1.1、Redminote11tech 溯源、Ko-fi/加密地址捐赠折叠块、"not affiliated" 免责）。

清理掉的过期表述：
- "This checkout is the xiaquwan1001 fork ... current release v2.4.12" 段 → 版本号改 v2.4.13，超长段落拆散进各节。
- 整体性 gate 说法不再出现（grep "gate-only in this build" 于 README/docs/*.md 零命中）。
- 安装段写死的 2.4.12 文件名 ×3 → 改 <ver> 占位；2.4.8 旧文件名（pkg.tar.zst 等）不再在 README 出现，
  以"继承基线"口径指向 stable-release.md 的 fork note。
- Redminote11tech 链接仅保留 credit 语义（Credits 节与继承基线说明），不再承担安装/issue 指引。

## 3. docs 清理与新增（完成）

- **docs/INDEX.md（新建）**：fork 相关文档在前、继承基线文档在后的一张表 + audit/ 入口。
- **docs/features.md（新建）**：离线工具/设备操作/安全/打包 CI 四块特性总览（英文，随仓库 docs 惯例）。
- **docs/screenshots.md（新建）**：6 张待拍图清单（main/dark/extractor/flash-progress/redline-confirm/cli-doctor）
  与文件名约定，标注"占位，F9 出图后填，出图前 README 不嵌入"。
- **docs/cli-mcp.md**：**gate 语义表未动**。仅在标题后插入入口/获取链接块（release 资产、deb 安装、入口二进制）
  与 2 行 plan→write 示例。另做一处过期表述最小修正：原头部"本目录下的 CLI/MCP 仅作本地 fork 改造，
  不发布、不分发"与 fork 实际发布 CLI 二进制直接矛盾（lane-C 记录的文档漂移），改为"继承 v2.4.2 基线按
  BSL 1.1 发布，许可口径见 FORK-NOTICE"。未触碰命令表/gate 表/退出码表。

## 4. 建议的仓库元数据（供编排者 gh api 应用）

**description**（≤350 字符，实测约 330）：

> Unofficial Linux port of Kirin-Tool for Huawei Kirin devices: UPDATE.APP extraction, native Huawei fastboot, and real Full-OTA flashing (verified 46/46 on P40 Pro). Headless CLI + MCP stdio server with device-keyed redline gates in GUI, CLI and MCP. Self-built deb/AppImage/tar.gz. .NET 10 / Avalonia.

**homepage**: https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest

**topics**: huawei, kirin, hisilicon, fastboot, firmware, flashing, avalonia, dotnet, linux, mcp, update-app, p40-pro

应用参考：

    gh api -X PATCH repos/xiaquwan1001/kirin-tool-linux -f description="..." -f homepage="..."
    gh api -X PUT repos/xiaquwan1001/kirin-tool-linux/topics -f names[]=huawei -f names[]=kirin ...

## 5. 静态核对与边界

- 未跑 dotnet build/run/test；未 git add/commit/push；未改代码、未改 .github/workflows。
- 编辑文件清单：README.md（重写）、docs/INDEX.md（新建）、docs/features.md（新建）、
  docs/screenshots.md（新建）、docs/cli-mcp.md（仅插入入口块+示例+一处头部过期表述修正）。
- 46/46、22s、gate 四态、MCP -32602、exit 0-5、红线清单均与 fix-F1/F2 报告及 Cli/ 源码 grep 结果一致。
- 电量闸门/pre-op 备份按实际状态写为 planned，未虚报已落地。
- 注意：docs/features.md 用了英文正文（仓库 docs 惯例为英文，任务书"docs 中文"按仓库一致性优先处理；
  如编排者要求中文可再翻）。
