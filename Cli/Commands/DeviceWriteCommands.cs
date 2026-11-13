// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Text;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Services.USBUpdate;
using Kirin_Tool.Utils;
using Kirin_Tool.Services.Verdict;
using Kirin_Tool.Services.WriteOperations;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// Turns an approval refusal into a machine-readable gate result (JSON) plus a human hint, and
/// returns the exit code the process must use. Shared by every gated write command so the
/// MCP client sees the same shape regardless of which capability was refused.
/// </summary>
internal static class ApprovalReport
{
    public static int Refuse(CliContext context, string command, ApprovalDecision decision)
    {
        int exit = decision is ApprovalDecision.Denied denied ? denied.ExitCode : ExitCodes.Gated;
        string gate = decision switch
        {
            ApprovalDecision.NotSupported => "approval_required_human",
            ApprovalDecision.TimedOut => "approval_timeout",
            _ => "refused",
        };
        string reason = decision switch
        {
            ApprovalDecision.Denied refusal => refusal.Reason,
            ApprovalDecision.TimedOut timedOut => "no approval answer within " + (int)timedOut.Waited.TotalSeconds + "s",
            ApprovalDecision.NotSupported notSupported => notSupported.Instruction,
            _ => "approval refused",
        };

        object? error = decision is ApprovalDecision.NotSupported unsupported
            ? new { code = unsupported.ErrorCode, message = unsupported.Instruction, entry = unsupported.Entry }
            : null;

        if (context.Json)
        {
            context.WriteJson(new
            {
                command,
                dryRun = true,
                applied = false,
                gate,
                confirmed = false,
                reason,
                error,
                exitCode = exit,
            });
        }

        context.Diag("approval refused: " + reason);
        return exit;
    }
}

/// <summary>
/// What a write command actually observed, and nothing else. <see cref="Applied"/> answers
/// "did the device take the write?", <see cref="Gate"/> is the machine-readable token
/// (`applied` / `partial` / `failed` / `unconfirmed`) and <see cref="ExitCode"/> is the process
/// code the same facts justify. The only way to build one is one of the factories below, so a
/// command cannot print "applied" without saying what the device answered (D-02).
/// </summary>
internal sealed record WriteReport(bool Applied, string Gate, int ExitCode, string? Reason, object Details);

internal static class WriteReports
{
    /// <summary>The write ran and the device accepted it (exit 0).</summary>
    public static WriteReport Applied(object details) => new(true, "applied", ExitCodes.Ok, null, details);

    /// <summary>Some of the writes in this command took effect (partial success).</summary>
    public static WriteReport Partial(object details, int exitCode, string reason) => new(true, "partial", exitCode, reason, details);

    /// <summary>The write did not take effect: refused, rejected, or silently ignored.</summary>
    public static WriteReport Failed(object details, int exitCode, string reason) => new(false, "failed", exitCode, reason, details);

    /// <summary>
    /// The command was accepted but its goal is not established by evidence (e.g. no OKAY token,
    /// no read-back). Never exit 0: an unproven write is not a success.
    /// </summary>
    public static WriteReport Unconfirmed(object details, int exitCode, string reason) => new(false, "unconfirmed", exitCode, reason, details);
}

/// <summary>
/// Base for the real-write commands: build the request, ask the gate, and only then touch the
/// device through <see cref="DeviceWriteOps"/>. There is deliberately no code path that reaches
/// the service without an <see cref="ApprovalAdmission"/> carrying a <see cref="WriteTicket"/>.
/// </summary>
internal abstract class GatedDeviceWriteCommand : ICliCommand
{
    public abstract string Name { get; }
    public abstract string McpName { get; }
    public abstract string Summary { get; }
    public abstract string Usage { get; }
    public CliWriteKind WriteKind => CliWriteKind.RealWrite;
    public abstract IReadOnlyList<CliParam> Params { get; }

    protected abstract string CapabilityId { get; }

    /// <summary>Dynamic facts (argv, targets, redline) for the approval request.</summary>
    protected abstract ApprovalRequest BuildRequest(ParsedArgs args, CliContext context);

    protected abstract Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken);

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        // --yes (CLI) / confirm:true (MCP) is an acknowledgement, never the approval itself.
        bool acknowledged = context.Yes || args.HasFlag("yes");
        ApprovalRequest request = BuildRequest(args, context) with
        {
            Acknowledged = acknowledged,
            RedlineOverride = args.HasFlag("allow-redline"),
            SessionId = sessionId ?? "",
        };

        ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (!admission.IsApproved)
            return ApprovalReport.Refuse(context, Name, admission.Decision);

        return await RunAsync(args, context, admission, cancellationToken).ConfigureAwait(false);
    }

    private readonly string? sessionId = null;

    protected static async Task<FastbootSession?> OpenConnectedSessionAsync(CliContext context)
    {
        FastbootSession? session = await FastbootSession.TryOpenAsync().ConfigureAwait(false);
        if (session == null)
        {
            context.Diag("error: fastboot binary not found");
            return null;
        }
        if (!session.Connected)
        {
            context.Diag("error: no device in fastboot mode");
            return null;
        }
        return session;
    }

    /// <summary>
    /// Writes the machine-readable result and returns the exit code the caller must use. The
    /// verdict is a parameter on purpose: there is no default, so "applied: true" can only be
    /// printed by a call site that observed the device accepting the write (D-02).
    /// </summary>
    protected static int WriteResult(CliContext context, string command, string capabilityId, WriteReport report,
        string? humanLine = null, IReadOnlyDictionary<string, object?>? evidence = null)
    {
        if (context.Json)
        {
            // Fixed shape first (command/capability/applied/gate/reason/result/exitCode), then the
            // command's own evidence keys (e.g. write-sn's verified/expected/readBack) at the top
            // level, so a client can read the verdict without digging into "result".
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = command,
                ["capability"] = capabilityId,
                ["dryRun"] = false,
                ["applied"] = report.Applied,
                ["gate"] = report.Gate,
                ["reason"] = report.Reason,
                ["result"] = report.Details,
                ["exitCode"] = report.ExitCode,
            };
            if (evidence != null)
            {
                foreach (var pair in evidence)
                {
                    if (!payload.ContainsKey(pair.Key)) payload[pair.Key] = pair.Value;
                }
            }
            context.WriteJson(payload);
        }
        else if (humanLine != null)
        {
            context.Output.WriteLine(humanLine);
        }
        return report.ExitCode;
    }

    /// <summary>One short reason line for the JSON: never the whole multi-line fastboot dump.</summary>
    protected static string Reason(string prefix, string? output)
    {
        string? first = (output ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
        return first == null ? prefix + " (fastboot printed nothing)" : prefix + ": " + first;
    }
}

/// <summary>fastboot reboot — the smallest write capability; reversible, so a plain --yes is enough.</summary>
internal sealed class RebootCommand : GatedDeviceWriteCommand
{
    public override string Name => "reboot";
    public override string McpName => "reboot";
    public override string Summary => "Reboot the device out of fastboot (same service method as the GUI button; approval gate first).";
    public override string Usage => "reboot [--yes] [--json]\n  without --yes the request is denied and nothing is sent (exit 4)";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("yes", "boolean", "acknowledge the write and send 'fastboot reboot'"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "reboot";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context) =>
        ApprovalRequests.For(CapabilityId, argv: new[] { "fastboot", "reboot" }, targets: Array.Empty<string>());

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        var ops = new DeviceWriteOps(session.Client);
        ProcessResult result = await ops.RebootAsync(admission.RequireTicket(), cancellationToken);
        var details = new { success = result.IsSuccess, output = result.Output };
        WriteReport report = result.IsSuccess
            ? WriteReports.Applied(details)
            : WriteReports.Failed(details, DeviceOutcome.FailureExit(result.Output), Reason("fastboot reboot failed", result.Output));
        return WriteResult(context, Name, CapabilityId, report,
            result.IsSuccess ? "reboot sent: the device is rebooting" : "reboot failed: " + result.Output);
    }
}

/// <summary>getvar nve:SN@&lt;value&gt; — permanent identity write, redline + irreversible.</summary>
internal sealed class WriteSnCommand : GatedDeviceWriteCommand
{
    public override string Name => "write-sn";
    public override string McpName => "write_sn";
    public override string Summary => "Permanently rewrite the NVE serial number (irreversible; typed confirm required).";
    public override string Usage =>
        "write-sn --value <serial> --yes --allow-redline [--json]\n" +
        "  interactive stdin then asks for the phrase 'I UNDERSTAND write-sn'; there is no non-interactive override";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("value", "string", "the new serial number", Required: true),
        new CliParam("allow-redline", "boolean", "acknowledge that this writes the redline identity segment"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "write-sn";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context)
    {
        string value = (args.Value("value") ?? "").Trim();
        if (value.Length == 0) throw new CliUsageException("write-sn requires --value <serial>");
        return ApprovalRequests.For(CapabilityId,
            argv: new[] { "fastboot", "getvar", "nve:SN@" + value },
            targets: new[] { "nve:SN" });
    }

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        string value = (args.Value("value") ?? "").Trim();
        var ops = new DeviceWriteOps(session.Client);
        ProcessResult result = await ops.WriteSerialNumberAsync(admission.RequireTicket(), value, cancellationToken);

        // Same verdict as the GUI (Services/Verdict/NveWriteVerdict.cs): exit 0 only means
        // fastboot did not reject the command locally (audit F04/S18). Read the value back
        // through the client the write went through; only an identical read-back counts.
        string? readBack = null;
        string? readBackError = null;
        try { readBack = await session.Client.ReadNveVariable("SN"); }
        catch (Exception ex) { readBackError = ex.Message; }

        NveWriteVerdict verdict = NveWriteCheck.Compare(value, readBack, result.IsSuccess, readBackError);
        var details = new
        {
            success = result.IsSuccess,
            expected = verdict.Expected,
            readBack = verdict.ReadBack,
            readBackError,
            verified = verdict.Verified,
            reason = verdict.Reason,
            output = result.Output,
        };

        // The reason token is the verdict's own (command_failed / readback_unavailable /
        // readback_mismatch:<value>), never a re-worded sentence: clients switch on it.
        WriteReport report = verdict.Verified
            ? WriteReports.Applied(details)
            : WriteReports.Failed(details, ExitCodes.DeviceRejected, verdict.Reason);

        var evidence = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["verified"] = verdict.Verified,
            ["expected"] = verdict.Expected,
            ["readBack"] = verdict.ReadBack,
            ["readBackError"] = readBackError,
        };

        return WriteResult(context, Name, CapabilityId, report,
            verdict.Verified
                ? "serial number written and read back from the device: " + verdict.ReadBackDisplay
                : "Write Not Confirmed — requested '" + verdict.Expected + "', read back '" + verdict.ReadBackDisplay + "'. " + verdict.Explain,
            evidence);
    }
}

/// <summary>erase frp / erase config / oem frp-erase — three irreversible steps.</summary>
internal sealed class FrpRemoveCommand : GatedDeviceWriteCommand
{
    public override string Name => "frp-remove";
    public override string McpName => "frp_remove";
    public override string Summary => "Erase the FRP lock (erase frp, erase config, oem frp-erase; irreversible).";
    public override string Usage =>
        "frp-remove --yes --allow-redline [--json]\n" +
        "  interactive stdin then asks for the phrase 'I UNDERSTAND frp-remove'";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("allow-redline", "boolean", "acknowledge that erase frp/config is a redline operation"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "frp-remove";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context) =>
        ApprovalRequests.For(CapabilityId,
            argv: new[] { "fastboot", "erase", "frp", "erase", "config", "oem", "frp-erase" },
            targets: new[] { "frp", "config" });

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        var ops = new DeviceWriteOps(session.Client);
        FrpBypassResult result = await ops.FrpEraseAsync(admission.RequireTicket(), cancellationToken);
        var details = new
        {
            steps = new[] { result.Step1Success, result.Step2Success, result.Step3Success },
            successfulSteps = result.SuccessfulStepsCount,
            allStepsFailed = result.AllStepsFailed,
        };
        // A step only counts when fastboot exited 0 AND printed OKAY (FastbootClient.EraseFrpWithSteps).
        WriteReport report = result.SuccessfulStepsCount == 3
            ? WriteReports.Applied(details)
            : result.SuccessfulStepsCount == 0
                ? WriteReports.Failed(details, ExitCodes.DeviceRejected, "0/3 FRP erase commands returned OKAY")
                : WriteReports.Partial(details, ExitCodes.DeviceRejected,
                    result.SuccessfulStepsCount + "/3 FRP erase commands returned OKAY; the rest did not");
        return WriteResult(context, Name, CapabilityId, report,
            $"{result.SuccessfulStepsCount}/3 FRP erase commands returned OKAY (FRP state itself is not verifiable on this path)");
    }
}

/// <summary>oem unlock — needs the POWER button on the device; ARB increments permanently.</summary>
internal sealed class UnlockBootloaderCommand : GatedDeviceWriteCommand
{
    public override string Name => "unlock-bootloader";
    public override string McpName => "unlock_bootloader";
    public override string Summary => "Run 'oem unlock' (the device must confirm with the POWER button; ARB is irreversible).";
    public override string Usage =>
        "unlock-bootloader --yes --allow-redline [--json]\n" +
        "  the device asks for the POWER button; add --allow-redline to accept the ARB increment";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("allow-redline", "boolean", "acknowledge the permanent ARB increment"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "unlock-bootloader";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context) =>
        ApprovalRequests.For(CapabilityId,
            argv: new[] { "fastboot", "oem", "unlock" },
            targets: new[] { "bootloader-lock-state", "arb" });

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        var ops = new DeviceWriteOps(session.Client);
        var progress = new Progress<string>(output => context.Diag("unlock: " + output));
        (bool IsSuccess, string Output) result = await ops.UnlockBootloaderAsync(admission.RequireTicket(), progress, cancellationToken);
        var details = new { success = result.IsSuccess, output = result.Output };
        WriteReport report = result.IsSuccess
            ? WriteReports.Applied(details)
            : WriteReports.Failed(details, DeviceOutcome.FailureExit(result.Output), Reason("oem unlock did not confirm", result.Output));
        return WriteResult(context, Name, CapabilityId, report,
            result.IsSuccess ? "unlock reported success (confirm the lock state on the device itself)" : "unlock did not confirm: " + result.Output);
    }
}

/// <summary>Software testpoint: patch XLOADER and push it over VCOM (enter) or flash it back (exit).</summary>
internal sealed class UsbTestpointCommand : GatedDeviceWriteCommand
{
    public override string Name => "usb-testpoint";
    public override string McpName => "usb_testpoint";
    public override string Summary => "Enter or exit the software testpoint (patches XLOADER and transfers it over VCOM; needs engineering access).";
    public override string Usage =>
        "usb-testpoint --file <Base UPDATE.APP> (--enter | --exit) --yes --allow-redline [--json]\n" +
        "  entering leaves the device in VCOM (12d1:3609); exiting flashes XLOADER back over fastboot";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("enter", "boolean", "enter the software testpoint (device ends up in VCOM mode)"),
        new CliParam("exit", "boolean", "exit the software testpoint (flash XLOADER back)"),
        new CliParam("file", "string", "Base UPDATE.APP that carries the XLOADER image", Required: true),
        new CliParam("allow-redline", "boolean", "acknowledge that XLOADER is rewritten"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "usb-testpoint";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context)
    {
        bool enter = args.HasFlag("enter");
        bool exit = args.HasFlag("exit");
        if (enter == exit) throw new CliUsageException("usb-testpoint requires exactly one of --enter / --exit");
        string file = args.Value("file") ?? "";
        if (file.Length == 0) throw new CliUsageException("usb-testpoint requires --file <Base UPDATE.APP>");
        string resolved = Path.GetFullPath(file, context.WorkingDirectory);
        if (!File.Exists(resolved)) throw new CliFileException("file not found: " + resolved);

        return ApprovalRequests.For(CapabilityId,
            argv: new[] { enter ? "sw-testpoint-enter" : "sw-testpoint-exit", resolved },
            targets: new[] { "XLOADER" });
    }

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        bool enter = args.HasFlag("enter");
        string resolved = Path.GetFullPath(args.Value("file")!, context.WorkingDirectory);

        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        // No interactive retry channel in the CLI: a VCOM retry prompt that nobody can answer would
        // hang automation, so the callback refuses and the service throws with its own message.
        var testpoint = new Services.USBUpdate.SoftwareTestpointService(
            message => context.Diag("testpoint: " + message),
            Path.Combine(Path.GetTempPath(), "kirin-tool-dload"),
            session.Client,
            (title, message) =>
            {
                context.Diag("retry required: " + title + ": " + message);
                return Task.FromResult(false);
            });

        var ops = new DeviceWriteOps(session.Client, testpoint);
        try
        {
            await ops.SoftwareTestpointAsync(admission.RequireTicket(), enter, resolved, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The service throws on every failure path; "no exception" is its only evidence, so
            // an exception is the only thing that can turn this into a failure.
            var failure = new { enter, file = resolved, transferred = false, error = ex.Message };
            return WriteResult(context, Name, CapabilityId,
                WriteReports.Failed(failure, ExitCodes.DeviceRejected, "testpoint transfer failed: " + ex.Message),
                "testpoint transfer FAILED: " + ex.Message);
        }

        // No exception means the frames were sent; it is not evidence about the device state, so
        // the gate stays "applied" only for the transfer itself and the human line says so.
        return WriteResult(context, Name, CapabilityId,
            WriteReports.Applied(new { enter, file = resolved, transferred = true, deviceStateVerified = false }),
            enter
                ? "testpoint transfer returned without an error report; whether the device really is in VCOM mode is NOT confirmed by this command"
                : "XLOADER was flashed back; confirm the device answers 'fastboot getvar product' again");
    }
}

/// <summary>Reboot to USB Update: flash the rescue partitions, then trigger the mode entry read-back.</summary>
internal sealed class RebootUsbUpdateCommand : GatedDeviceWriteCommand
{
    public override string Name => "reboot-usb-update";
    public override string McpName => "reboot_usb_update";
    public override string Summary => "Flash the rescue partitions from a Base UPDATE.APP and enter USB Update mode (redline rescue segment).";
    public override string Usage =>
        "reboot-usb-update --file <Base UPDATE.APP> --yes --allow-redline [--json]\n" +
        "  interactive stdin then asks for the phrase 'I UNDERSTAND reboot-usb-update'";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("file", "string", "Base UPDATE.APP carrying the rescue images", Required: true),
        new CliParam("allow-redline", "boolean", "acknowledge the ERECOVERY* (redline) rescue write"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "reboot-usb-update";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context)
    {
        string file = args.Value("file") ?? "";
        if (file.Length == 0) throw new CliUsageException("reboot-usb-update requires --file <Base UPDATE.APP>");
        string resolved = Path.GetFullPath(file, context.WorkingDirectory);
        if (!File.Exists(resolved)) throw new CliFileException("file not found: " + resolved);

        var targets = RescuePartitionPlan.ForChip("").Select(entry => entry.Target).ToArray();
        return ApprovalRequests.For(CapabilityId,
            argv: new[] { "flash", "<rescue-partitions>", resolved, "getvar", "rescue_enter_recovery" },
            targets: targets,
            touchesRedline: true);
    }

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        string resolved = Path.GetFullPath(args.Value("file")!, context.WorkingDirectory);
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        WriteTicket ticket = admission.RequireTicket();
        var ops = new DeviceWriteOps(session.Client);
        var plan = RescuePartitionPlan.ForChip("");
        var flashed = new List<string>();
        using var updateApp = new UpdateApp(resolved);
        // Plan-level op for the rescue segment (docs/op-scope-map.md row 4): one file for the
        // run; the per-partition write below lands as a step of it.
        using var op = OpLog.Begin(OpNames.FlashRescue, OpLog.FlashTool,
            new[] { resolved, "allow-redline" }, "rescue partitions + USB Update mode entry");
        op.Step("rescue plan: " + string.Join(", ", plan.Select(entry => entry.Target))
            + " from " + OpLog.FileStep(resolved));
        string tempDirectory = Path.Combine(Path.GetTempPath(), "kirintool-rescue-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            foreach (RescueImageTarget entry in plan)
            {
                PartitionInfo? info = updateApp.Partitions.FirstOrDefault(
                    candidate => candidate.Name.Equals(entry.Image, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    context.Diag("warning: " + entry.Image + " is not in this UPDATE.APP; skipping " + entry.Target);
                    // Declared skip (was only a console warning): the plan step says what is missing.
                    op.Step(OpLog.SkippedPrefix + entry.Target + ": " + entry.Image
                        + " is not in this UPDATE.APP");
                    continue;
                }

                string imagePath = Path.Combine(tempDirectory, entry.Target + ".img");
                await updateApp.ExtractPartition(info, imagePath).ConfigureAwait(false);
                op.Step("image: " + entry.Target + " <- " + OpLog.FileStep(imagePath));
                ProcessResult write = await ops.FlashRescuePartitionAsync(ticket, entry.Target, imagePath, cancellationToken, op);
                if (!write.IsSuccess)
                {
                    context.Diag("error: flashing " + entry.Target + " failed: " + write.Output);
                    op.Result(false, write.ExitCode, null, write.Output, null, OpLog.ExitVerdict(write),
                        "rescue write failed at " + entry.Target + "; USB Update mode entry was not attempted");
                    return ExitCodes.DeviceRejected;
                }
                flashed.Add(entry.Target);
            }

            if (flashed.Count == 0)
            {
                context.Diag("error: no rescue partition could be flashed; USB Update mode entry was not attempted");
                op.Result(false, null, null, null, null, "partition-outcomes:none",
                    "none of the rescue images was present in the package; the mode entry was not attempted");
                return ExitCodes.FileOrVerify;
            }

            string modeOutput = await ops.EnterUsbUpdateModeAsync(ticket, cancellationToken, op);
            bool modeConfirmed = DeviceOutcome.LooksRejected(modeOutput) == false &&
                (modeOutput ?? "").ToUpperInvariant().Contains("OKAY");
            var rescueDetails = new
            {
                flashed,
                modeEntry = modeOutput,
                modeEntryConfirmed = modeConfirmed,
            };

            // The command's goal is USB Update mode. Rescue writes that ran are real, but an
            // unconfirmed mode entry is not a success (no OKAY token / the loader rejected it).
            WriteReport report = modeConfirmed
                ? WriteReports.Applied(rescueDetails)
                : DeviceOutcome.LooksRejected(modeOutput)
                    ? WriteReports.Failed(rescueDetails, ExitCodes.DeviceRejected, Reason("rescue_enter_recovery was rejected by the loader", modeOutput))
                    : WriteReports.Unconfirmed(rescueDetails, ExitCodes.DeviceRejected,
                        "rescue_enter_recovery printed no OKAY, so USB Update mode entry is not confirmed");
            op.Result(modeConfirmed, null, null, modeOutput, null,
                modeConfirmed ? "output-contains:OKAY" : "output-missing:OKAY",
                flashed.Count + " rescue partition(s) written; mode entry confirmed=" + modeConfirmed);
            return WriteResult(context, Name, CapabilityId, report,
                "rescue partitions flashed: " + string.Join(", ", flashed) + "; loader answer: " + modeOutput);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); } catch { }
        }
    }

}

/// <summary>
/// flash oeminfo — the device half of the OEMINFO rewrite. The CLI `oeminfo` command only builds
/// the image locally (PlanOnly); writing it is a redline, irreversible device write and needs its
/// own capability + approval, exactly like the GUI button does (D-01).
/// </summary>
internal sealed class OemInfoFlashCommand : GatedDeviceWriteCommand
{
    public override string Name => "oeminfo-flash";
    public override string McpName => "oeminfo_flash";
    public override string Summary => "Flash a converted OEMINFO image to the oeminfo partition (redline identity image; irreversible).";
    public override string Usage =>
        "oeminfo-flash --file <converted-oeminfo.img> --yes --allow-redline [--json]\n" +
        "  produce the image first with: oeminfo --model <model> --region <region> --input <oeminfo.img>\n" +
        "  interactive stdin then asks for the phrase 'I UNDERSTAND oeminfo-flash'";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("file", "string", "converted oeminfo image to write", Required: true),
        new CliParam("allow-redline", "boolean", "acknowledge that oeminfo is a redline identity partition"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "oeminfo-flash";

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context)
    {
        string file = args.Value("file") ?? "";
        if (file.Length == 0) throw new CliUsageException("oeminfo-flash requires --file <converted-oeminfo.img>");
        string resolved = Path.GetFullPath(file, context.WorkingDirectory);
        if (!File.Exists(resolved)) throw new CliFileException("file not found: " + resolved);

        return ApprovalRequests.For(CapabilityId,
            argv: new[] { "fastboot", "flash", "oeminfo", resolved },
            targets: new[] { "oeminfo" });
    }

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        string resolved = Path.GetFullPath(args.Value("file")!, context.WorkingDirectory);
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        var ops = new DeviceWriteOps(session.Client);
        ProcessResult result = await ops.FlashOemInfoAsync(admission.RequireTicket(), resolved, cancellationToken);

        // Exit code 0 without an explicit OKAY token is Unknown: fastboot accepted the command,
        // the write is unproven. Same rule the GUI applies to the same partition.
        bool okay = result.IsSuccess && (result.Output ?? string.Empty).ToUpperInvariant().Contains("OKAY");
        var details = new
        {
            file = resolved,
            partition = "oeminfo",
            success = result.IsSuccess,
            okay,
            output = result.Output,
        };

        WriteReport report = okay
            ? WriteReports.Applied(details)
            : result.IsSuccess
                ? WriteReports.Unconfirmed(details, ExitCodes.DeviceRejected,
                    "fastboot exited 0 without an OKAY token for 'flash oeminfo'")
                : WriteReports.Failed(details, DeviceOutcome.FailureExit(result.Output),
                    Reason("flash oeminfo failed", result.Output));

        return WriteResult(context, Name, CapabilityId, report,
            okay
                ? "oeminfo write returned OKAY (the loader accepted the image)"
                : "Write Not Confirmed — " + Reason("last output", result.Output));
    }
}

/// <summary>
/// downgrade-erase — the device half of "Enable Downgrade". The CLI `downgrade-enable` command only
/// prints the four commands (PlanOnly, exit 4); erasing the oeminfo version fields is a redline,
/// irreversible write and needs its own capability + approval, exactly like the GUI button does (D-13,
/// the same split D-01 made for oeminfo).
/// </summary>
internal sealed class DowngradeEraseCommand : GatedDeviceWriteCommand
{
    public override string Name => "downgrade-erase";
    public override string McpName => "downgrade_erase";
    public override string Summary => "Erase the OEMINFO version fields amss/base/cust/preload to enable a downgrade (irreversible; typed confirm required).";
    public override string Usage =>
        "downgrade-erase --yes --allow-redline [--json]\n" +
        "  runs the 4 irreversible commands 'oem oeminfoerase-{amss,base,cust,preload}ver' in order\n" +
        "  interactive stdin then asks for the phrase 'I UNDERSTAND downgrade-erase'\n" +
        "  the plan-only variant (prints the 4 commands, writes nothing, exit 4) is: downgrade-enable";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("allow-redline", "boolean", "acknowledge that the oeminfo version records are erased irreversibly"),
        new CliParam("yes", "boolean", "acknowledge the request (the typed phrase is still required)"),
        new CliParam("json", "boolean", "structured output"),
    };

    protected override string CapabilityId => "downgrade-erase";

    private static readonly string[] EraseCommands =
    {
        "oem oeminfoerase-amssver",
        "oem oeminfoerase-basever",
        "oem oeminfoerase-custver",
        "oem oeminfoerase-preloadver",
    };

    protected override ApprovalRequest BuildRequest(ParsedArgs args, CliContext context) =>
        ApprovalRequests.For(CapabilityId,
            argv: new[] { "fastboot" }.Concat(EraseCommands.SelectMany(command => command.Split(' '))).ToArray(),
            targets: new[] { "oeminfo" }.Concat(EraseCommands.Select(command => command.Split(' ')[1])).ToArray());

    protected override async Task<int> RunAsync(ParsedArgs args, CliContext context, ApprovalAdmission admission, CancellationToken cancellationToken)
    {
        FastbootSession? session = await OpenConnectedSessionAsync(context);
        if (session == null) return ExitCodes.NoDevice;

        var ops = new DeviceWriteOps(session.Client);
        EnableDowngradeResult result = await ops.EnableDowngradeAsync(admission.RequireTicket(), cancellationToken);

        int okSteps = result.SuccessfulStepsCount;
        var details = new
        {
            steps = new[] { result.Step1Success, result.Step2Success, result.Step3Success, result.Step4Success },
            successfulSteps = okSteps,
            allStepsFailed = result.AllStepsFailed,
            // Evidence name for the op log / JSON: a step counts only when the loader answered OKAY.
            verdictSource = "step-outcomes:" + okSteps + "/4-okay",
            outputs = new[] { result.Step1Output, result.Step2Output, result.Step3Output, result.Step4Output },
        };

        // 4/4 OKAY is the only "applied"; nothing here can prove the version records are gone (no
        // read-back exists), so the human line says what was and was not established.
        WriteReport report = okSteps == 4
            ? WriteReports.Applied(details)
            : okSteps == 0
                ? WriteReports.Failed(details, ExitCodes.DeviceRejected, "0/4 version-field erases returned OKAY")
                : WriteReports.Partial(details, ExitCodes.DeviceRejected,
                    okSteps + "/4 version-field erases returned OKAY; the rest did not");

        return WriteResult(context, Name, CapabilityId, report,
            okSteps == 4
                ? "all 4 version-field erases returned OKAY (OKAY only means the commands ran - a downgrade is NOT confirmed; verify with 'oem dump-emmc oeminfo')"
                : okSteps + "/4 version-field erases returned OKAY - the downgrade is not confirmed");
    }
}
