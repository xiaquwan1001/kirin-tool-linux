// Licensed under the repository's Business Source License 1.1. See LICENSE.

using Kirin_Tool.Services.Approval;

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
    string? GuiOnlyReason = null,
    CapabilityApprovalKind Approval = CapabilityApprovalKind.None,
    ApprovalReversibility Reversibility = ApprovalReversibility.Reversible,
    string? ServiceMethod = null,
    string Tool = "fastboot")
{
    /// <summary>True when the capability has no CLI/MCP surface (declared debt).</summary>
    public bool IsGuiOnly => CliCommand is null || McpTool is null;

    /// <summary>True when this capability must pass the approval gate before it may run.</summary>
    public bool NeedsApproval => Approval != CapabilityApprovalKind.None;
}

/// <summary>
/// The single source of truth for "what does this tool do, and through which faces?".
///
/// Editing rule (enforced by tests/SurfaceParity, not by convention):
///   * register a capability BEFORE adding a CLI command or a GUI handler;
///   * every CLI command must be referenced by at least one capability;
///   * a read-only capability may not stay GUI-only;
///   * a write capability must have all three faces, must name the service method all three share,
///     and must be reachable through <c>IApprovalGate</c> from every face (docs/approval-gate.md).
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
        Read("logs", "统一操作日志查询（会话目录 / 主日志 / 旧日志）", "logs", null, "verb", "session", "trace", "root"),

        // ---------- 写：CLI + MCP + GUI，执行前一律过审批闸门 ----------
        new("flash-ota", "整包刷写（Full OTA）", "flash-ota", "flash_ota", "StartFullOtaButton_Click",
            CapabilityWriteKind.Write,
            ["file", "files", "partitions", "exclude", "allow-redline", "work-dir", "strict-names", "no-backup", "backup-dir", "require-backup", "yes", "json"],
            ["逐分区写设备（真实写入）", "红线段 oeminfo/nvme/persist/modem_secure/certification/hisee_encos/veritykey 需 --allow-redline（MCP 不接受）", "写前自动 pre-op 备份"],
            Approval: CapabilityApprovalKind.Once,
            ServiceMethod: "FastbootClient.FlashPartition"),

        new("flash-xml", "按华为刷机 XML 刷写", "flash-xml", "flash_xml", "FlashFromXml_Click",
            CapabilityWriteKind.Write,
            ["path", "partitions", "exclude", "allow-redline", "strict-names", "no-backup", "backup-dir", "require-backup", "yes", "json"],
            ["逐标识写设备（真实写入）", "红线段需 --allow-redline（MCP 不接受）", "写前自动 pre-op 备份"],
            Approval: CapabilityApprovalKind.Once,
            ServiceMethod: "FastbootClient.FlashPartition"),

        new("vcom-unlock", "VCOM 引导链装载（BootROM 上传 loader）", "vcom-unlock", "vcom_unlock", "HandleUnlockFastboot",
            CapabilityWriteKind.Write,
            ["cpu", "yes", "dry-run", "ack-mode", "ack-timeout", "require-ack", "replug", "loader-dir", "stage-only",
             "port", "use-fastflash-loader", "no-frame-log", "json"],
            ["向 BootROM 上传 null/xloader+pwn/uce/fastboot/bl2 到 RAM（断电即失效）",
             "真实注入走同一 FirmwareUnlocker + VcomFlasher（GUI 与 CLI 同源）",
             "默认 --ack-mode strict：CLI/MCP 下设备不回 0xAA 即 exit 3（GUI 仍为 lenient：ACK 只记录不校验，结论看 verified/ack 统计）",
             "缺 --yes 时只打印计划（plan-only 门控）"],
            Approval: CapabilityApprovalKind.Once,
            Reversibility: ApprovalReversibility.Reversible,
            ServiceMethod: "FirmwareUnlocker.UnlockFastboot",
            Tool: "vcom"),

        // oeminfo was one row that lied about itself: the CLI/MCP only plan, while the GUI flashed
        // the result of that plan straight to the device. Two capabilities, one per outcome (D-01).
        new("oeminfo-convert", "OEMINFO 型号/区域改写（本地生成镜像）", "oeminfo", "oeminfo", "BtnConvertOem_Click",
            CapabilityWriteKind.PlanOnly, ["model", "region", "input", "pull"],
            ["在宿主机上生成改写后的 oeminfo.img（只写本地文件，不写设备）",
             "oeminfo 是红线段（HMAC 用设备密钥，无法离线重算）；把它写进设备是另一条能力 oeminfo-flash"]),

        new("oeminfo-flash", "刷写转换后的 OEMINFO 镜像", "oeminfo-flash", "oeminfo_flash", "HandleOemInfoFlash",
            CapabilityWriteKind.Write, ["file", "allow-redline", "yes", "json"],
            ["把转换后的镜像真实写入 oeminfo 分区（红线段：身份/型号/区域，HMAC 由设备密钥生成）",
             "写错无法在工具内回退，只能整包重刷或走救援"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.FlashOemInfoAsync"),

        new("downgrade-enable", "启用降级（OEMINFO 版本擦除）规划", "downgrade-enable", "downgrade_enable", "HandleEnableDowngrade",
            CapabilityWriteKind.PlanOnly, [],
            ["只打印 4 条 oem oeminfoerase-* 计划，不擦除", "版本字段擦除不可逆"]),

        // ---------- 红线 / 不可逆写：三面齐备，必须手输确认串 ----------
        new("write-sn", "永久改写 NVE 序列号", "write-sn", "write_sn", "HandleWriteSerialNumber",
            CapabilityWriteKind.Write,
            ["value", "allow-redline", "yes", "json"],
            ["getvar nve:SN@<value>，永久且不可逆", "序列号是身份字段，写前先备份原值"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.WriteSerialNumberAsync"),

        new("frp-remove", "清除 FRP 锁", "frp-remove", "frp_remove", "HandleFrpRemove",
            CapabilityWriteKind.Write,
            ["allow-redline", "yes", "json"],
            ["erase frp", "erase config", "oem frp-erase（三步不可逆擦除）"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.FrpEraseAsync"),

        new("unlock-bootloader", "解锁 bootloader（oem unlock）", "unlock-bootloader", "unlock_bootloader", "HandleUnlockBootloader",
            CapabilityWriteKind.Write,
            ["allow-redline", "yes", "json"],
            ["oem unlock UUUU…，需在设备上按电源键，ARB 计数不可逆", "解锁后设备状态不可回退到锁定前的 ARB"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.UnlockBootloaderAsync"),

        new("usb-testpoint", "软测试点进入/退出 USB Update", "usb-testpoint", "usb_testpoint", "HandleSoftwareTestpoint",
            CapabilityWriteKind.Write,
            ["enter", "exit", "file", "allow-redline", "yes", "json"],
            ["patch XLOADER 并经 VCOM 传输到设备", "退出时把 XLOADER 刷回，写错会导致设备不枚举"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.SoftwareTestpointAsync",
            Tool: "vcom"),

        new("reboot-usb-update", "重启到 USB Update（含救援分区刷写）", "reboot-usb-update", "reboot_usb_update", "HandleRebootToUsbUpdate",
            CapabilityWriteKind.Write,
            ["file", "allow-redline", "yes", "json"],
            ["刷救援分区 ERECOVERY*（红线段）", "getvar rescue_ugs_port / rescue_enter_recovery 进入 USB Update 模式"],
            Approval: CapabilityApprovalKind.RedlineTypedConfirm,
            Reversibility: ApprovalReversibility.Irreversible,
            ServiceMethod: "DeviceWriteOps.EnterUsbUpdateModeAsync"),

        new("reboot", "重启设备", "reboot", "reboot", "RebootDevice_Click",
            CapabilityWriteKind.Write, ["yes", "json"],
            ["fastboot reboot"],
            Approval: CapabilityApprovalKind.Once,
            Reversibility: ApprovalReversibility.Reversible,
            ServiceMethod: "DeviceWriteOps.RebootAsync"),
    };

    /// <summary>Stable lookup for GUI/tests/docs; null when the id is not registered.</summary>
    public static Capability? ById(string id) =>
        All.FirstOrDefault(capability => string.Equals(capability.Id, id, StringComparison.Ordinal));

    /// <summary>Capabilities that no CLI/MCP face exposes yet (declared debt, see design doc §4).</summary>
    public static IReadOnlyList<Capability> GuiOnly =>
        All.Where(capability => capability.IsGuiOnly).ToList();
}
