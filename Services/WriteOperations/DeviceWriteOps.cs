// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Diagnostics;
using Kirin_Tool.Models;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Services.USBUpdate;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services.WriteOperations;

/// <summary>
/// The one place where an irreversible/destructive device write is actually sent. Every face
/// (GUI handler, CLI command, MCP tool) calls these methods, so the behaviour, the refusal
/// branches and the diagnostics cannot drift between faces.
///
/// Every method takes a <see cref="WriteTicket"/>: the ticket can only be minted by
/// <see cref="ApprovalGuard"/> after an <see cref="IApprovalGate"/> said yes, which makes
/// "did this path go through the gate?" a compile-time property instead of a code-review habit.
/// </summary>
public sealed class DeviceWriteOps
{
    private readonly Func<FastbootClient> _fastboot;
    private readonly SoftwareTestpointService? _testpoint;

    public DeviceWriteOps(FastbootClient fastboot, SoftwareTestpointService? testpoint = null)
        : this(() => fastboot, testpoint)
    {
    }

    /// <summary>
    /// Resolve the client per call: the GUI replaces its fastboot client (and the tests inject a
    /// stub binary) after construction, and a captured instance would silently keep the old one.
    /// </summary>
    public DeviceWriteOps(Func<FastbootClient> fastboot, SoftwareTestpointService? testpoint = null)
    {
        _fastboot = fastboot ?? throw new ArgumentNullException(nameof(fastboot));
        _testpoint = testpoint;
    }

    private FastbootClient Client => _fastboot();

    /// <summary>fastboot reboot (reversible: the device comes back).</summary>
    public Task<ProcessResult> RebootAsync(WriteTicket ticket, CancellationToken cancellationToken = default)
    {
        Require(ticket, "reboot");
        return Client.RebootAsync();
    }

    /// <summary>getvar nve:SN@&lt;value&gt; — permanent identity write.</summary>
    public Task<ProcessResult> WriteSerialNumberAsync(WriteTicket ticket, string value, CancellationToken cancellationToken = default)
    {
        Require(ticket, "write-sn");
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("a serial number value is required", nameof(value));
        return Client.WriteNveVariable("SN", value.Trim());
    }

    /// <summary>erase frp / erase config / oem frp-erase, three irreversible steps.</summary>
    public Task<FrpBypassResult> FrpEraseAsync(WriteTicket ticket, CancellationToken cancellationToken = default)
    {
        Require(ticket, "frp-remove");
        return Client.EraseFrpWithSteps();
    }

    /// <summary>oem unlock — needs the user to press POWER on the device; ARB increments permanently.</summary>
    public Task<(bool IsSuccess, string Output)> UnlockBootloaderAsync(
        WriteTicket ticket, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Require(ticket, "unlock-bootloader");
        return Client.UnlockBootloaderAsync(progress);
    }

    /// <summary>
    /// Rescue-partition write (ERECOVERY* = redline segment) that is part of the reboot-to-USB-Update
    /// flow. Shared by the GUI's progress-dialog loop and the CLI's sequential loop.
    /// </summary>
    public async Task<ProcessResult> FlashRescuePartitionAsync(
        WriteTicket ticket, string partition, string imagePath, CancellationToken cancellationToken = default, OperationScope? scope = null)
    {
        Require(ticket, "reboot-usb-update");
        // CLI reboot-usb-update passes its plan-level flash.rescue scope; the GUI loop calls
        // this one partition at a time and therefore gets one flash.rescue op per target.
        using var own = scope == null
            ? OpLog.Begin(OpNames.FlashRescue, OpLog.FlashTool, new[] { partition, imagePath }, "rescue partition write")
            : null;
        OperationScope op = (scope ?? own)!;
        op.Step("flash " + partition + " <- " + OpLog.FileStep(imagePath) + " [redline rescue segment]");
        try
        {
            ProcessResult result = await Client.FlashPartition(partition, imagePath);
            op.Step(OpLog.ResultStep(partition, result, result.IsSuccess));
            if (own != null)
                op.Result(result.IsSuccess, result.ExitCode, null, result.Output, null,
                    OpLog.ExitVerdict(result), "verdict rule: fastboot exit code");
            return result;
        }
        catch (Exception ex)
        {
            op.Step(OpLog.FailedPrefix + partition + ": " + OpLog.ExceptionVerdict(ex) + ": " + ex.Message);
            if (own != null)
                op.Result(false, null, null, null, ex.Message, OpLog.ExceptionVerdict(ex));
            throw;
        }
    }

    /// <summary>
    /// The mode switch itself: read rescue_ugs_port, then trigger rescue_enter_recovery. Returns the
    /// raw loader answer — callers classify it (OKAY / FAILED / Unknown) themselves, never assume success.
    /// </summary>
    public async Task<string> EnterUsbUpdateModeAsync(
        WriteTicket ticket, CancellationToken cancellationToken = default, OperationScope? scope = null)
    {
        Require(ticket, "reboot-usb-update");
        try
        {
            string port = await Client.GetVarAsync("rescue_ugs_port").ConfigureAwait(false);
            ProcessRunner.WriteDiagnostic("DeviceWriteOps", "rescue_ugs_port: " + port);
            scope?.Step(OpLog.OkPrefix + "rescue_ugs_port: " + port);
        }
        catch (Exception ex)
        {
            // The port read is advisory evidence only; the mode entry below is the real action.
            ProcessRunner.WriteDiagnostic("DeviceWriteOps", "rescue_ugs_port read failed: " + ex.Message);
            scope?.Step(OpLog.DegradedPrefix + "rescue_ugs_port read failed (" + ex.Message + "); mode entry still attempted");
        }
        string answer = await Client.GetVarAsync("rescue_enter_recovery").ConfigureAwait(false);
        scope?.Step("rescue_enter_recovery answer: " + (string.IsNullOrWhiteSpace(answer) ? "(empty)" : answer.Trim()));
        return answer;
    }

    /// <summary>
    /// Software testpoint: patch XLOADER and push it over VCOM (enter) or flash XLOADER back (exit).
    /// The service throws on every failure path, so "no exception" is its verdict.
    /// </summary>
    public Task SoftwareTestpointAsync(WriteTicket ticket, bool enter, string updateAppPath, CancellationToken cancellationToken = default)
    {
        Require(ticket, "usb-testpoint");
        if (_testpoint == null)
            throw new InvalidOperationException("no SoftwareTestpointService is wired into this DeviceWriteOps instance");
        if (string.IsNullOrWhiteSpace(updateAppPath))
            throw new ArgumentException("a Base UPDATE.APP path is required", nameof(updateAppPath));
        return enter
            ? _testpoint.EnterSoftwareTestpoint(updateAppPath)
            : _testpoint.ExitSoftwareTestpoint(updateAppPath);
    }

    /// <summary>
    /// A ticket is bound to exactly one capability: a "reboot" approval must never be replayable as a
    /// serial-number write.
    /// </summary>
    private static void Require(WriteTicket? ticket, string capabilityId)
    {
        if (ticket == null)
            throw new InvalidOperationException("refusing to write '" + capabilityId + "' without an approved WriteTicket (approval gate bypass attempt)");
        if (!string.Equals(ticket.CapabilityId, capabilityId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "ticket for '" + ticket.CapabilityId + "' cannot authorize '" + capabilityId + "'");
    }
}
