# fix-F11：酷安帖（v2.4.13）+ agent bridge 发布

日期：2026-11-12 · lane：F11 · 范围：`docs/coolapk/v2.4.13-post.md` + 本报告；不改代码、不做 git add/commit/push。

## 状态

- [x] 读素材（README / features / release-notes / cli-mcp / F10a）
- [x] 写帖子正文
- [x] bridge 探活
- [x] 只读接口验证
- [x] 上传 6 张图
- [x] 发帖
- [x] 核对：未使用上游旧图

## 逐步记录


### 1) 素材与配置核查（2026-11-12T18:10:35+0800）

- 读了 README.md、docs/features.md、docs/release-notes/v2.4.13.md、docs/cli-mcp.md、docs/audit/2026-11-12/fix-F10a.md、docs/screenshots.md。
- 帖子正文写入 `docs/coolapk/v2.4.13-post.md`：标题 29 字；正文约 800 字（544 汉字 + 命令/JSON），6 段结构；6 张图按正文顺序。
- 仓库状态：`git describe` = v2.4.13-2-g0d27e3d；仅新增本报告与 docs/coolapk/（未 add/commit/push）。


### 2) Bridge 探活与配置（2026-11-12T18:10:35+0800）

- 起始状态：`ss -ltn` 无 47653/47654；`pgrep` 无 coolapk_desktop。
- 启动：`DISPLAY=:0 nohup /usr/bin/coolapk_desktop >/tmp/coolapk.log 2>&1 &`，约 3 s 后两端口就绪（47653 REST / 47654 MCP），未重启过。
- 运行版本 1.42.1；`agent-bridge.json` 由客户端启动时生成：transport=tcp、port=47653、mcp_port=47654、scopes=[read, write.publish, write.interact]、write_mode=**confirm**、mcp_enabled=true。token 仅在 `agent-bridge-token`，未打印、未写入本报告与帖子。
- 通道只读验证：REST `GET /v1/health` 返回 account/scopes/writeMode=mcpEndpoint 正常；MCP `initialize`（protocol 2025-06-18，serverInfo coolapk-desktop 1.42.1）与 `tools/list`（46 个工具）正常；`tools/call coolapk_me` 返回当前账号资料（UID/邮箱未记录）。
- 「上传测试图」：该 bridge **没有独立 upload 接口**，图片由 `coolapk_publish_feed` 在闸门确认通过后自行读盘上传。因此这一步退化为本地就绪校验：6 张图均为 PNG 1600x1000，合计 662 KB（单张 ≤8 MB、合计 ≤20 MB 限额内），文件可读、MIME 判定为 image/png。

### 3) 发布（2026-11-12T18:10:35+0800）

- 闸门：write_mode=confirm → 调用 `coolapk_publish_feed` 后客户端弹确认（60 s 窗口）。发请求前用 `notify-send` 提醒本机用户，请求随即通过确认，返回 `dryRun=false`。
- 结果：**发布成功**。feed id **75023844**，URL https://www.coolapk.com/feed/75023844 ；客户端返回 adminInfo「动态审核后可见」（审核期）。
- 提交内容：message 2447 字符（646 汉字），`imagePaths` 6 条绝对路径，`visibleStatus=1`，幂等 `requestId=f11-v2413-post-20261112-1`。
- 回读校验：`coolapk_feed_detail(75023844)` → message 长度 2935（服务端归一化）、`picArr` 6 张真实图片（`http://image.coolapk.com/feed/2026/1112/...@1600x...`）。
- 审计行：`{"tool":"coolapk_publish_feed","dryRun":false,"imageCount":6,"messageChars":2447,"ok":true,"scope":"write.publish"}`。
- 重试：0 次（首次即成功）。

### 4) 用到的 bridge 接口（方法名）

- REST：`GET /v1/health`
- MCP：`initialize`、`tools/list`、`tools/call` → `coolapk_me`、`coolapk_publish_feed`、`coolapk_feed_detail`

### 5) 图片清单（上传顺序 = 正文顺序）

| # | 文件 | 尺寸 | 大小 | SHA256 前 16 |
| :-- | :--- | :--- | :--- | :--- |
| 1 | screenshot-main.png | 1600x1000 | 79449 B | d795f28f165b9468 |
| 2 | screenshot-dark.png | 1600x1000 | 79608 B | f4abc0023de62aed |
| 3 | screenshot-extractor.png | 1600x1000 | 89948 B | 9607e258fc3cf0cb |
| 4 | screenshot-flash-progress.png | 1600x1000 | 112970 B | 346813efe64b32ae |
| 5 | screenshot-redline-confirm.png | 1600x1000 | 150888 B | c36a704008198cc9 |
| 6 | screenshot-cli-doctor.png | 1600x1000 | 149523 B | d0ac5cf8d09f9749 |

### 6) 「未使用上游旧图」核对结论

**通过**。6 张图 SHA256 前缀与 `docs/audit/2026-11-12/fix-F10a.md` 记录的 v2.4.13 headless 渲染值逐一相同（d795f28f / f4abc002 / 9607e258 / 346813ef / c36a7040 / d0ac5cf8），
全部 1600x1000、来源为本机 tag v2.4.13 的真实 Avalonia 控件渲染；无上游（Kethily/Redminote11tech）或旧版本截图混入。帖子正文也如实说明「无真机连接、设备字段显示 Not Read」。

### 7) 收尾

- 未执行任何 git add/commit/push；未改代码。
- 新增文件：`docs/coolapk/v2.4.13-post.md`、`docs/audit/2026-11-12/fix-F11.md`。

