# ACK 默认值分入口：CLI/MCP = strict，GUI = lenient

> 适用版本：v2.4.15 起（基线 v2.4.14 / 2ade54b）。
> 相关：[cli-vcom-unlock-parity.md](cli-vcom-unlock-parity.md)、[verdict-honesty.md](verdict-honesty.md)、[cli-mcp.md](cli-mcp.md)。

## 1. 决策

`vcom-unlock` 的 `--ack-mode` 默认值按**入口**分开：

| 入口 | `ack-mode` 默认 | 语义 |
| :--- | :--- | :--- |
| CLI（`kirin-tool-cli vcom-unlock`） | `strict` | 读 1 字节；读不到或非 `0xAA` ⇒ 传输中断，exit 3 |
| MCP（`vcom_unlock` 工具） | `strict`（随 CLI schema 的 `default` 一起变，MCP 没有第二份默认值） | 同上 |
| GUI（VCOM Unlock 对话框） | `lenient` | 读 ACK、记录、不校验值、读不到也继续（历史行为，未变） |

差异只存在于**入口层**：CLI/MCP 在自己的参数解析里给出 `strict`；GUI 构造
`VcomFlasherOptions` 时不传 `AckMode`，于是落在类默认值 `Lenient` 上。
`Services/VcomFlasher.cs` 的 ACK 读取/校验/时序**一行未改**。

## 2. 理由

1. **自动化侧不该被"静默成功"误导。** v2.4.14 里 `lenient` 是默认：设备一声不吭也能 exit 0
   （结论里已有诚实的 `verified=false / reason=ack_missing:<n>`，但退出码仍 0）。
   CI/脚本只看退出码，于是"上传了但没人应答"会被当成成功继续往下跑。
2. **人类侧保留现场判断。** GUI 里有逐帧 trace、会话日志、琥珀色 "Uploaded — Unverified"
   和 `frames sent / ACKed / missing` 计数，人看着现场决定要不要换线重跑；
   让 GUI 因为一个 ACK 字节直接中断上传反而更危险（半截 bootloader 链）。
3. **结论语义不退回去。** `--ack-mode lenient` 仍 exit 0，但结论永远是
   `verified=false`（`gate=applied-unverified`, `conclusion=loaders-uploaded-unverified`）。
   "写下去了"和"设备确认了"继续是两个独立字段。

## 3. 结论（退出码 / JSON）对照

| 场景 | exit | `verified` | `gate` | `failureKind` / `reason` |
| :--- | :--- | :--- | :--- | :--- |
| 默认（strict）+ 设备每帧回 `0xAA` | 0 | true | `applied` | `reason=ack_ok` |
| 默认（strict）+ 设备静默 | 3 | false | `failed` | `failureKind=device-rejected`，`reason=device_rejected`；证据在 `ack.timeouts` / `ack.missing` |
| 默认（strict）+ 设备回 `0x55` | 3 | false | `failed` | 同上，证据在 `ack.bad` |
| `--ack-mode lenient` + 设备静默 | 0 | false | `applied-unverified` | `reason=ack_missing:<n>` |
| `--ack-mode lenient --require-ack` + 设备静默 | 3 | false | `failed` | `failureKind=ack-missing`，`reason=ack_missing:<n>`（上传仍走完） |
| `--ack-mode off` | 0 | false | `applied-unverified` | `reason=ack_not_requested` |

> strict 下 ACK 缺失是**传输层中断**，所以 `reason` 是 `device_rejected`（不是 `ack_missing:<n>`）：
> `ack_missing:<n>` 描述的是"走完了但没被确认"，strict 根本不会走完。两者的 ACK 计数都在 `ack` 里。

## 4. 兼容与迁移（谁会受影响）

- **会红的人**：把 `vcom-unlock` 当"盲发"用的脚本/自动化。设备不回 ACK（老旧 BootROM、
  半接的线、错误的 `--port` 指向一个不回话的 tty）时，过去 exit 0，现在 exit 3。
  处置：`--ack-mode lenient`（恢复旧退出码，但结论仍是 `verified=false`）或 `--ack-mode off`。
- **不受影响**：GUI；已显式写 `--ack-mode strict|lenient|off` 的调用；MCP 的 `confirm:true`
  门控与人工审批闸门（行为完全不变，只是参数 `default` 字段变了）。
- **MCP 客户端**：`tools/list` 里 `vcom_unlock` 的 `ack-mode` `default` 由 `lenient` 变 `strict`。
  依赖 schema 默认值生成调用的客户端会自动变严格；显式传 `lenient` 的不会。
- **不改变的部分**：帧布局/CRC/地址/延时/重试、`--require-ack` 语义、`verified/reason` 字段、
  `exit 3` 的既有含义（device-rejected）。

## 5. 测试计划（已落地，全量无硬件）

| 用例 | 覆盖 | 断言 |
| :--- | :--- | :--- |
| `tests/VcomUnlockCli` §1 | 计划输出 | 不带 `--ack-mode` 时 `plan.ackMode == "strict"` |
| `tests/VcomUnlockCli` §2 | 默认值 | 默认（不带 `--ack-mode`）+ 静默设备 ⇒ exit 3、`verified=false`、`failureKind=device-rejected` |
| `tests/VcomUnlockCli` §2 | 显式 lenient | `--ack-mode lenient` + 静默 ⇒ exit 0 但 `verified=false`、`gate=applied-unverified` |
| `tests/VerdictHonesty` §1a | 默认值 | 默认 + 静默 ⇒ exit 3、`verified=false` |
| `tests/VerdictHonesty` §1a′ | 显式 lenient | exit 0 + `verified=false` + `ack_missing:<n>`（v2.4.14 的诚实化语义未回退） |
| `tests/VerdictHonesty` §1b/§2 | `--require-ack` | 显式 `lenient` 下仍是"走完 + 事后判失败"，因为 strict 默认值会先中断 |
| `tests/SurfaceParity` | 三面注册表 | `vcom-unlock` 参数表不变（参数名未改），`SideEffects` 文案改为"CLI/MCP 默认 strict，GUI lenient" |

门禁：`dotnet build` + `tests/suites.txt` 全部 22 套 harness 全绿（无硬件依赖，不碰真机）。

## 6. 回滚

改动是**纯默认值 + 文案**，回滚成本极低：

1. `git revert <commit>` 即可（无数据迁移、无 schema 破坏、无状态）。
2. 只想临时放行而不回滚：调用侧加 `--ack-mode lenient`（MCP 传 `"ack-mode": "lenient"`）。
3. GUI 想改严格（本次未做）：把 `MainWindow.axaml.cs` 里 `VcomFlasherOptions.AckMode` 设为
   `VcomAckMode.Strict` 即可，入口层的类默认值 `Lenient` 不用动。

DONE-1
