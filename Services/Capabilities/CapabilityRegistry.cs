// Licensed under the repository's Business Source License 1.1. See LICENSE.

namespace Kirin_Tool.Services.Capabilities;

/// <summary>
/// What a capability can do to the device/host. Mirrors <c>Kirin_Tool.Cli.Commands.CliWriteKind</c>
/// on purpose: <c>tests/SurfaceParity</c> cross-checks the two so a capability cannot claim
/// "read-only" while its CLI command performs a real write.
/// </summary>
public enum CapabilityWriteKind
{
    ReadOnly,
    PlanOnly,
    Write,
}

/// <summary>
/// One row of the GUI/CLI/MCP parity contract. Fields are deliberately strings (not
/// delegates): the registry is a *declaration* that a hardware-free test can compare against
/// the real CLI command table, the real MCP tool schema and the real GUI handler names.
/// </summary>
public sealed record Capability(
    string Id,
    string NameZh,
    string? CliCommand,
    string? McpTool,
    string? GuiHandler,
    CapabilityWriteKind WriteKind,
    IReadOnlyList<string> Params,
    IReadOnlyList<string> SideEffects,
    string? GuiOnlyReason = null)
{
    /// <summary>True when the capability has no CLI/MCP surface (declared debt).</summary>
    public bool IsGuiOnly => CliCommand is null || McpTool is null;
}

/// <summary>
/// The single source of truth for "what does this tool do, and through which faces?".
///
/// Editing rule (enforced by tests/SurfaceParity, not by convention):
///   * register a capability BEFORE adding a CLI command or a GUI handler;
///   * every CLI command must be referenced by at least one capability;
///   * a read-only capability may not stay GUI-only;
///   * a write capability that stays GUI-only must carry a non-empty <see cref="Capability.GuiOnlyReason"/>.
/// </summary>
public static class CapabilityRegistry
{
    private static Capability Read(string id, string nameZh, string? cli, string? gui, params string[] parameters) =>
        new(id, nameZh, cli, cli, gui, CapabilityWriteKind.ReadOnly, parameters,
            cli is null ? ["(no automated face yet)"] : [$"fastboot/ToolInfo 只读查询（{cli}）"]);

    public static IReadOnlyList<Capability> All { get; } = new Capability[]
    {
        // ---------- 只读：CLI + MCP + GUI ----------
        Read("info", "设备信息", "info", "ReadDeviceInfo_Click"),
        Read("doctor", "环境自检", "doctor", null),
        Read("getvar", "读单个 fastboot 变量", "getvar", null, "name"),
        Read("read-sn", "读 NVE 序列号", "getvar", "HandleReadSerialNumber", "name"),
        Read("psid", "读 PSID / 设备标识", "psid", "ReadPsid_Click"),
        Read("partitions", "设备分区表", "partitions", "DumpPartitions_Click"),
        Read("capabilities", "芯片能力矩阵", "capabilities", null),
        Read("udev-check", "宿主机 udev 规则覆盖检查", "udev", "CheckUdevRules_Click"),
        Read("extract", "解包 UPDATE.APP", "extract", "StartExtraction_Click", "file", "outdir"),
        Read("dump", "单分区 dump 到宿主机", "dump", "DumpPartitions_Click", "partition", "output"),
        Read("backup", "身份分区备份 + manifest", "backup", null, "partition", "partitions", "backup-dir"),

        // ---------- 写：CLI + MCP（有 --yes / confirm:true 门控） ----------
        new("flash-ota", "整包刷写（Full OTA）", "flash-ota", "flash_ota", "StartFullOtaButton_Click",
            CapabilityWriteKind.Write,
            ["file", "files", "partitions", "exclude", "allow-redline", "work-dir", "strict-names", "no-backup", "backup-dir", "require-backup", "yes", "json"],
            ["逐分区写设备（真实写入）", "红线段 oeminfo/nvme/persist/modem_secure/certification/hisee_encos/veritykey 需 --allow-redline（MCP 不接受）", "写前自动 pre-op 备份"]),

        new("flash-xml", "按华为刷机 XML 刷写", "flash-xml", "flash_xml", "FlashFromXml_Click",
            CapabilityWriteKind.Write,
            ["path", "partitions", "exclude", "allow-redline", "strict-names", "no-backup", "backup-dir", "require-backup", "yes", "json"],
            ["逐标识写设备（真实写入）", "红线段需 --allow-redline（MCP 不接受）", "写前自动 pre-op 备份"]),

        new("vcom-unlock", "VCOM 引导链装载（BootROM 上传 loader）", "vcom-unlock", "vcom_unlock", "HandleUnlockFastboot",
            CapabilityWriteKind.Write,
            ["cpu", "yes", "dry-run", "ack-mode", "ack-timeout", "replug", "loader-dir", "stage-only", "port",
             "use-fastflash-loader", "no-frame-log", "json"],
            ["向 BootROM 上传 null/xloader+pwn/uce/fastboot/bl2 到 RAM（断电即失效）",
             "真实注入走同一 FirmwareUnlocker + VcomFlasher（GUI 与 CLI 同源）",
             "默认 --ack-mode lenient 等于 GUI 行为：ACK 只记录不校验，结论需看 verified/ack 统计",
             "缺 --yes 时只打印计划（plan-only 门控）"]),

        new("oeminfo", "OEMINFO 型号/区域改写规划", "oeminfo", "oeminfo", "BtnConvertOem_Click",
            CapabilityWriteKind.PlanOnly, ["model", "region", "input", "pull"],
            ["只生成改写计划，不刷 oeminfo", "oeminfo 是红线段（HMAC 无法离线重算）"]),

        new("downgrade-enable", "启用降级（OEMINFO 版本擦除）规划", "downgrade-enable", "downgrade_enable", "HandleEnableDowngrade",
            CapabilityWriteKind.PlanOnly, [],
            ["只打印 4 条 oem oeminfoerase-* 计划，不擦除", "版本字段擦除不可逆"]),

        // ---------- 仅 GUI：写能力（红线段 / 需人工与硬件交互），声明债务 ----------
        new("write-sn", "永久改写 NVE 序列号", null, null, "HandleWriteSerialNumber",
            CapabilityWriteKind.Write, [],
            ["getvar nve:SN@<value>，永久且不可逆"],
            "身份级永久写入（NVE SN）：MCP 结构性拒绝 allow-redline，无法自授权；CLI 侧开 nve 写命令要单独的契约设计（见 docs/parity-design.md §4.5）"),

        new("frp-remove", "清除 FRP 锁", null, null, "HandleFrpRemove",
            CapabilityWriteKind.Write, [],
            ["erase frp", "erase config", "oem frp-erase（三步不可逆擦除）"],
            "不可逆擦除且必须逐条人工确认，未纳入 CLI 契约"),

        new("unlock-bootloader", "解锁 bootloader（oem unlock）", null, null, "HandleUnlockBootloader",
            CapabilityWriteKind.Write, [],
            ["oem unlock UUUU…，需在设备上按电源键，ARB 计数不可逆"],
            "需要人工在设备上按键 + 输出流式分类，自动化会假装成功（GUI 有 AlreadyUnlocked/未确认分支）"),

        new("usb-testpoint", "软测试点进入/退出 USB Update", null, null, "HandleSoftwareTestpoint",
            CapabilityWriteKind.Write, [],
            ["patch XLOADER 并经 VCOM 传输到设备"],
            "依赖真实 VCOM/工程线枚举（12d1:3609），硬件不可自动化；VCOM/传输层由另一 lane 重建中"),

        new("reboot-usb-update", "重启到 USB Update（含救援分区刷写）", null, null, "HandleRebootToUsbUpdate",
            CapabilityWriteKind.Write, [],
            ["刷救援分区 ERECOVERY*（红线段）", "oem reboot-usb-update"],
            "救援刷写走红线段 + 需要锁状态判定，先由 GUI 人工确认路径承载"),

        new("reboot", "重启设备", null, null, "RebootDevice_Click",
            CapabilityWriteKind.Write, [],
            ["fastboot reboot"],
            "尚未纳入 CLI 契约；非破坏性，是下一步最容易消掉的 GUI-only 债务（docs/parity-design.md §4.4）"),
    };

    /// <summary>Stable lookup for GUI/tests/docs; null when the id is not registered.</summary>
    public static Capability? ById(string id) =>
        All.FirstOrDefault(capability => string.Equals(capability.Id, id, StringComparison.Ordinal));

    /// <summary>Capabilities that no CLI/MCP face exposes yet (declared debt, see design doc §4).</summary>
    public static IReadOnlyList<Capability> GuiOnly =>
        All.Where(capability => capability.IsGuiOnly).ToList();
}
