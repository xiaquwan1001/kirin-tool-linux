# fix-F10a：v2.4.13 真实 UI 截图（6 张 → docs/images/）

日期：2026-11-12 · lane：F10 · 范围：**只产出 docs/images/ 下的 PNG + 本报告**（未改主仓源码、未改其它文档、未做任何 git 操作）

## 结论

**6/6 完成**。全部 6 张 PNG 均为本机 headless 渲染 tag `v2.4.13`（commit `47fb3da`）的真实 UI，
尺寸统一 1600x1000，无上游/旧版本图片。逐张来源与状态见下表。

| # | 文件 | 尺寸 | 来源（怎么截的） | 状态 |
| :-- | :--- | :--- | :--- | :--- |
| 1 | `docs/images/screenshot-main.png` | 1600x1000 | MainWindow（light，#DeviceInfoItem 页，Information 展开）；本机无 fastboot 设备 → 值为界面原生的 "Not Read"，未编造设备数据 | ✅ |
| 2 | `docs/images/screenshot-dark.png` | 1600x1000 | 同上布局，`UserSettings.SetTheme("dark")` + `RequestedThemeVariant=Dark`（标题栏主题选择器同步显示 Dark） | ✅ |
| 3 | `docs/images/screenshot-extractor.png` | 1600x1000 | Extractor Tools 页 + 合成 UPDATE.APP（9 条目，经 `LoadExtractionSourceAsync` 真实解析）：状态 "Found 9 partitions…"、9/9 选中、Size 列按真实条目大小 | ✅ |
| 4 | `docs/images/screenshot-flash-progress.png` | 1600x1000 | `ProcessDialogUapp` 真实控件 + 真实进度 API（`CompletePartition` / `UpdateCurrentPartition`）：3 绿（boot/kernel/vendor）、1 红（modem_fw FAILED）、1 橙进行中（super 41%）、总进度 63%、底部 v2.4.13 pre-op 备份行 | ✅ |
| 5 | `docs/images/screenshot-redline-confirm.png` | 1600x1000 | `DialogService.ShowConfirmDialog` 真实对话框 + 生产文案（列 oeminfo / nvme / persist，说明不可恢复、需显式 "Write Anyway"） | ✅ |
| 6 | `docs/images/screenshot-cli-doctor.png` | 1600x1000 | 实跑 `KirinTool.Cli doctor`（exit 0）与 `flash-ota UPDATE.APP --partitions oeminfo --json`（exit 4）；JSON 含 `"gate": "refused"`，stderr 为 `pass --allow-redline to override` | ✅ |

## 生产方法

- worktree：`git -C ~/src/kirin-tool-linux worktree add ~/.cache/kirin-shots v2.4.13`（HEAD `47fb3da`），截图后 `worktree remove --force`。
- harness：worktree 内临时工程 `tests/GuiShots/`（csproj + Program.cs，`ProjectReference ../../Kirin-Tool.csproj`），
  与 `tests/GuiQol` 同款启动：
  `AppBuilder.Configure<App>().WithInterFont().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting()`。
  **未写入主仓、未提交**。
- 交互走真实代码路径：导航用 `NavigationListBox`，提取用 `LoadExtractionSourceAsync`（反射，等同 `tests/Extractor`），
  进度/红线段落用公开方法与 `DialogService`；没有手工拼 UI。
- CLI：worktree 内 `dotnet build KirinTool.Cli -c Release` 后由 harness `dotnet <dll>` 实跑，stdout/stderr 原样进入终端图。
- 两个小于画布的对话框用 `LayoutTransformControl` 按 1.7x / 2.2x **矢量放大**（不是位图拉伸），因此文字在 1600 宽下仍锐利。

## 诚实边界（截图里能看出来的真实状态）

1. 本机没有 fastboot 设备：设备信息页显示界面默认的 "Not Read"，未伪造型号/锁状态/SN；这是 v2.4.13 未连机的真实样子。
2. 提取与刷写用的是合成容器（`HWTEST00` 头 + 真实条目名/大小，SYSTEM 用稀疏文件），**不是**真实固件；行状态来自真实控件与真实 API 调用，没有真机写入。
3. 刷写进度图是"staged"运行（真实对话框 + 真实状态机），不是一次真机烧写；底部备份行文案取自 `RunPreOpBackupAsync` 的生产字符串。
4. CLI 的 doctor 报 `fastboot-device: no device`（exit 0，工具链本身 OK）；flash-ota 走到 gate 就停在 refused（exit 4），未触达设备。
5. 脱敏：build 用户 home → `~`；`… devices:` 行里的真机序列号 → `XXXX`（该行仅此一处含 SN）。无 PSID/IMEI/账号。
6. 1600x1000 的窗口下，设备信息页内容在 y≈762 结束，下方是窗口背景（应用默认布局就是顶对齐，未裁剪、未拉伸）。

## 校验

- `file` 全部为 `PNG image data, 1600 x 1000`（RGBA/RGB 混合，无 1x 缩放）。
- 渲染断言（harness 内，失败即抛）：每张 1600x1000；CLI 图字号 14.0px ≥ 14（58 行，行高 16.6px，最宽 946px，末行 y=982）；
  对话框缩放后内容包围盒与原始逻辑尺寸一致（900x550 @1.7、452x325 @2.2）。
- 像素抽检：flash 图含绿/红/橙状态色（各 1816–5448 采样点）；light/dark 亮度分布相反且各有对比；CLI 图深色 #1b1b1b 底、墨迹至 y=978。
- SHA256：
  - `screenshot-main.png` `d795f28f165b9468a78ada17b0f5f403b1a3f31baa0493d129c7cda0aaf58631`
  - `screenshot-dark.png` `f4abc0023de62aed82ede86d9ac8f9c80d90f6536d79061ba369c647c22419a4`
  - `screenshot-extractor.png` `9607e258fc3cf0cbf5d5eda99d62e8a06a2c46076fb6c91ab208cbce6ff0de67`
  - `screenshot-flash-progress.png` `346813efe64b32aec43314c96dc937ac8df772c80b7d35b2387e4c73ad825d66`
  - `screenshot-redline-confirm.png` `c36a704008198cc941facb9555298b2c87e4c8da3d3d33d3de258ce432e805e8`
  - `screenshot-cli-doctor.png` `d0ac5cf8d09f974912b64fc137c970c14403409430847c9c4eaea76caa9b9a68`

## 后续（不在本 lane 范围，留给 F9/F11）

- `docs/screenshots.md` 末尾仍是 "Status: not captured yet"，README 也还没嵌图——按本轮约束（只截图）未改，需要时一句话即可更新。
- 若要替换成真机图（连 P40 Pro 后重拍 #1/#2/#4），harness 的设计是"有真机就用真机"：设备信息页会显示读到的值，进度图可直接接真机刷写。

## 逐张记录（生成后即时追加）

| 时间顺序 | 文件 | 尺寸 | 来源 |
| :-- | :--- | :--- | :--- |
| 1 | screenshot-main.png | 1600x1000 | MainWindow light / DeviceInfo（未连机真实态） |
| 2 | screenshot-extractor.png | 1600x1000 | Extractor 页 / 合成 UPDATE.APP 9 条目 |
| 3 | screenshot-dark.png | 1600x1000 | 同 #1，dark 变体 + 主题持久化 |
| 4 | screenshot-flash-progress.png | 1600x1000 | ProcessDialogUapp / 真实状态 API（3 绿 1 红 1 橙 + 备份行） |
| 5 | screenshot-redline-confirm.png | 1600x1000 | DialogService.ShowConfirmDialog / 生产红线段文案 |
| 6 | screenshot-cli-doctor.png | 1600x1000 | 实跑 doctor + flash-ota --json（gate: refused） |

**6/6 完成**（2026-11-12）。
