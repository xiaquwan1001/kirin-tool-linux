# 酷安帖（refresh 版，实用展示定位）

- 帖子：https://www.coolapk.com/feed/75025716
- 帖子 ID：75025716，发布 2026-11-12 19:14（CST），状态「动态审核后可见」
- 配图：6 张（`docs/images/screenshot-*.png`，按正文顺序：主界面浅色 / 深色 / 提取器 / 刷写进度 / 红线确认 / CLI）
- 定位：**实用展示**（能干什么、怎么用更省事）。技术改动与理由在 [README](../../README.zh-CN.md) 与 [refresh-changelog.md](../refresh-changelog.md)。
- 上一版帖子（v2.4.13，偏技术报告，已删除）：[v2.4.13-post.md](v2.4.13-post.md) 仅作历史存档。
- 发布通道：本机 `coolapk_desktop` agent bridge（MCP 47654，write_mode=confirm，一次通过）。
  注意：桥接限流「发帖类写 1 次/分钟、10 次/小时」，超限返回 429「请求过于频繁」；
  两次确认框同时出现时，`src/utils/confirm.ts` 会把前一个判为拒绝，需重发。

## 正文

在 Linux 上刷华为/麒麟手机，一套工具就够了。

Kirin Tool 的 Linux 版我们接手后做了自己的维护线，叫 refresh（仓库名带 -refresh 后缀，别和原版搞混）。解包、读设备、刷 Full-OTA、救援，图形界面和命令行共用同一套逻辑，不用再凑一堆半成品脚本。

**装起来很省事**
· deb 一行装完：sudo apt install ./kirin-tool-linux_2.4.13_amd64.deb（界面+命令行+udev 规则，不额外装运行时）
· 不想装就用 AppImage；只想命令行就一个单文件二进制

**日常最常用三件事**
1）看包里有什么：选中 UPDATE.APP，分区全列出来，勾上就导出，不连手机也行。
2）刷机前先看计划：命令行 flash-ota base.app --json 只打印清单、一个字节都不写；图形界面点刷写也是先出清单，看清再按。
3）怕手滑碰身份分区：oeminfo、persist、nvme 这类默认拒绝，必须你亲自确认，界面会把具体分区列出来；而且每次真写之前自动备份成带时间戳的文件夹（附校验清单），出事至少有的看。

**几个顺手细节**
· 记住上次选的芯片型号、固件文件和目录，不用每回重翻
· 浅色/深色主题
· 刷写进度里能看到「写入前备份」那一行，知道它到底备了什么
· 电量不足会提示一句（只提示不拦人，但真的建议 50% 以上再动手）

**想让 AI 打下手也行**
自带 MCP 接口，可以接进支持 MCP 的客户端让它帮你 doctor、读设备信息、分析刷写计划。但所有会写设备的操作都必须你本人确认，红线分区更是只能人工在命令行加参数——AI 在协议层就被拒，没法给自己开权限。

**提醒**
刷机有风险，数据会没，型号固件不对可能直接黑砖。完整流程目前只在一台华为 P40 Pro（ELS-AN00）上跑通，别的机型和芯片别想当然。

相对原版改了什么、为什么这么改、真机上踩了哪些坑，都写在 GitHub README 里（技术向）：
https://github.com/xiaquwan1001/kirin-tool-linux-refresh
下载：https://github.com/xiaquwan1001/kirin-tool-linux-refresh/releases/latest
