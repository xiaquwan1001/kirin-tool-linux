using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// Real VCOM/BootROM unlock: loads the unlocked-boot loaders into RAM through
/// <see cref="FirmwareUnlocker"/> + <see cref="VcomFlasher"/> — the exact same services the GUI
/// calls (no protocol copy). The CLI only supplies the non-interactive pieces: an explicit
/// loader directory, an ACK policy, a stage split for the manual cable replug, and frame tracing.
/// See docs/cli-vcom-unlock-parity.md for the item-by-item GUI alignment table.
/// </summary>
internal sealed class VcomUnlockCommand : ICliCommand
{
    public string Name => "vcom-unlock";
    public string McpName => "vcom_unlock";
    public string Summary => "Load the unlocked-boot loaders over the VCOM BootROM (12d1:3609); uploads only with --yes.";
    public string Usage =>
        "vcom-unlock --cpu <chip> [--ack-mode strict|lenient|off] [--ack-timeout <sec>]\n" +
        "            [--replug skip|prompt|wait:<sec>] [--loader-dir <dir>] [--stage-only all|null|xloader|rest]\n" +
        "            [--port <tty>] [--use-fastflash-loader] [--dry-run] [--yes] [--json]\n" +
        "  without --yes only the plan is printed (exit 4); --dry-run keeps that behaviour even with --yes\n" +
        "  --stage-only rest continues after a manual replug and never re-sends the START frame\n" +
        "  ack-mode default lenient == the GUI's behaviour (read the ACK, never fail on its value)\n" +
        "  progress/ACK frames go to stderr; the last line of stdout is one JSON document";
    public CliWriteKind WriteKind => CliWriteKind.RealWrite;

    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("cpu", "string", "chipset loader family, e.g. hisi980, hisi990", Required: true, Enum: BundledLoaders.SupportedChips().ToList()),
        new CliParam("ack-mode", "string", "ACK policy: strict (fail unless 0xAA) | lenient (record only, GUI behaviour) | off (never wait)", Enum: new[] { "strict", "lenient", "off" }, Default: "lenient"),
        new CliParam("ack-timeout", "string", "seconds to wait for each ACK (default 10, the GUI value)"),
        new CliParam("replug", "string", "cable prompt policy: skip (non-interactive default) | prompt | wait:<seconds>"),
        new CliParam("loader-dir", "string", "directory holding the per-chip loader subdirectories (default: <program dir>/loaders)"),
        new CliParam("stage-only", "string", "stage subset: all | null | xloader | rest (rest never re-sends START)", Enum: new[] { "all", "null", "xloader", "rest" }, Default: "all"),
        new CliParam("port", "string", "explicit VCOM tty/COM path (skips the 12d1:3609 enumeration; bulk-only framing, no modem lines)"),
        new CliParam("use-fastflash-loader", "boolean", "upload fastbootf.ktl instead of fastboot.ktl (the GUI switch)"),
        new CliParam("no-frame-log", "boolean", "suppress the per-frame stderr trace (aggregates stay in the JSON document)"),
        new CliParam("dry-run", "boolean", "print the plan even when --yes is given"),
        new CliParam("yes", "boolean", "acknowledge the plan and really upload the loaders"),
        new CliParam("json", "boolean", "structured output"),
    };

    /// <summary>Chips whose null→XLOADER handoff needs a physical cable replug (same list as the GUI).</summary>
    private static readonly string[] ReplugAfterNullCpus = { "hisi980", "hisi810", "hisi820", "hisi985", "hisi990" };

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        string cpu = (args.Value("cpu") ?? string.Empty).Trim().ToLowerInvariant();
        if (cpu.Length == 0)
            throw new CliUsageException("vcom-unlock requires --cpu <chip> (supported: " + string.Join(", ", BundledLoaders.SupportedChips()) + ")");
        if (!FirmwareUnlocker.CpuAddresses.ContainsKey(cpu))
            throw new CliUsageException($"unsupported chip '{cpu}' (supported: {string.Join(", ", BundledLoaders.SupportedChips())})");

        // One audit file per invocation, even for a dry run: the header alone tells a later
        // reader which process/parameters would have touched the tty.
        using var logger = VcomSessionLogger.Create(cpu);
        if (logger.FilePath != null) context.Diag($"log: {logger.FilePath}");
        else context.Diag($"log: disabled (cannot create {VcomSessionLogger.DefaultDirectory}; set {VcomSessionLogger.DirectoryEnvironmentVariable})");
        logger.Event("command.start", ("command", Name), ("cpu", cpu), ("json", context.Json), ("yes", context.Yes));

        try
        {
            return await ExecuteCoreAsync(args, context, cpu, logger, cancellationToken);
        }
        finally
        {
            logger.Event("command.end");
        }
    }

    private async Task<int> ExecuteCoreAsync(ParsedArgs args, CliContext context, string cpu, VcomSessionLogger logger, CancellationToken cancellationToken)
    {
        VcomAckMode ackMode = ParseAckMode(args.Value("ack-mode"));
        TimeSpan ackTimeout = ParseAckTimeout(args.Value("ack-timeout"));
        (ReplugPolicy replug, int replugWaitSeconds) = ParseReplug(args.Value("replug"));
        UnlockStageSelection stages = ParseStages(args.Value("stage-only"));
        bool useFastFlash = args.HasFlag("use-fastflash-loader");
        bool dryRun = args.HasFlag("dry-run");
        bool frameLog = !args.HasFlag("no-frame-log");

        string loaderBase = args.Value("loader-dir") is { Length: > 0 } loaderDirArg
            ? Path.GetFullPath(loaderDirArg, context.WorkingDirectory)
            : BundledLoaders.Directory;
        string loaderDirectory = Path.Combine(loaderBase, cpu);

        var planned = BundledLoaders.Plan(cpu, useFastFlash, loaderBase)
            .Where(stage => StageSelected(stage.Stage, stages))
            .ToList();
        int presentCount = planned.Count(stage => stage.Exists);

        bool confirmed = (context.Yes || args.HasFlag("yes")) && !dryRun;
        var planPayload = new
        {
            cpu,
            loaderDirectory,
            implementation = "Services/FirmwareUnlocker.UnlockFastboot + Services/VcomFlasher (invoked: this command performs the real upload)",
            requiresVcomMode = "HUAWEI USB COM 1.0 (12d1:3609)",
            ackMode = AckModeName(ackMode),
            ackTimeoutSeconds = ackTimeout.TotalSeconds,
            replug = ReplugName(replug, replugWaitSeconds),
            stageSelection = StageName(stages),
            useFastFlashLoader = useFastFlash,
            stageCount = planned.Count,
            presentStages = presentCount,
            missingStages = planned.Count - presentCount,
            stages = planned.Select(stage => new
            {
                stage = stage.Stage,
                file = stage.File,
                path = Path.Combine(loaderDirectory, stage.File),
                address = "0x" + stage.Address.ToString("X8", CultureInfo.InvariantCulture),
                pwn = stage.PwnFlag,
                exists = stage.Exists,
                sendsStart = stages != UnlockStageSelection.Rest && stage.Stage == (planned.Count > 0 ? planned[0].Stage : string.Empty),
            }).ToList(),
        };

        // ---- gate: plan-only (no --yes, or an explicit --dry-run) ----
        if (!confirmed)
        {
            logger.Event("plan",
                ("cpu", cpu),
                ("loaderDirectory", loaderDirectory),
                ("stages", StageName(stages)),
                ("ackMode", AckModeName(ackMode)),
                ("replug", ReplugName(replug, replugWaitSeconds)),
                ("stageCount", planned.Count),
                ("presentStages", presentCount),
                ("dryRun", dryRun),
                ("confirmed", context.Yes || args.HasFlag("yes")));
            if (context.Json)
                context.WriteJson(new
                {
                    command = Name,
                    dryRun = true,
                    applied = false,
                    confirmed = context.Yes || args.HasFlag("yes"),
                    gate = "plan-only",
                    gateReason = dryRun
                        ? "--dry-run requested: nothing was sent to the device."
                        : "not acknowledged: re-run with --yes (CLI) or confirm:true (MCP) to really upload the loaders.",
                    plan = planPayload,
                    exitCode = ExitCodes.Gated,
                });
            else
            {
                WritePlanText(context, cpu, loaderDirectory, planned, ackMode, replug, replugWaitSeconds, stages);
                context.Output.WriteLine();
                context.Output.WriteLine($"gate: plan-only (exit {ExitCodes.Gated})");
                context.Output.WriteLine("re-run with --yes (CLI) or confirm:true (MCP) to really upload the loaders.");
            }
            context.Diag(dryRun ? "gate: --dry-run; nothing was sent" : "gate: plan printed; re-run with --yes to upload");
            return ExitCodes.Gated;
        }

        // ---- pre-flight (local file checks, before touching the device) ----
        if (!Directory.Exists(loaderDirectory))
        {
            context.Diag($"error: loader directory not found: {loaderDirectory}");
            return Finish(context, false, "failed", planPayload, Array.Empty<UnlockStageReport>(), null, 0,
                $"loader directory not found: {loaderDirectory}", "local-file", 0, ExitCodes.FileOrVerify);
        }
        if (presentCount == 0)
        {
            string list = string.Join(", ", planned.Select(stage => stage.File));
            context.Diag($"error: none of the selected loaders exist in {loaderDirectory} ({list})");
            return Finish(context, false, "failed", planPayload, Array.Empty<UnlockStageReport>(), null, 0,
                $"no selected loader file present in {loaderDirectory} ({list})", "local-file", 0, ExitCodes.FileOrVerify);
        }

        // The null→XLOADER handoff on these chips needs the device to be unplugged from the
        // computer side; a non-interactive run cannot do that on its own.
        bool continuesPastNull = stages is UnlockStageSelection.All or UnlockStageSelection.Xloader;
        bool needsReplug = continuesPastNull && ReplugAfterNullCpus.Contains(cpu);
        if (needsReplug && replug == ReplugPolicy.Skip)
        {
            context.Diag("refused: this chip's null→XLOADER handoff requires a physical cable replug; " +
                         "pass --replug wait:<seconds> (or --replug prompt) for the handoff, or split the run: " +
                         "--stage-only null, then unplug/replug, then --stage-only rest");
            return Finish(context, false, "refused", planPayload, Array.Empty<UnlockStageReport>(), null, 0,
                "cable replug required between null and XLOADER; use --replug wait:<seconds>/prompt or --stage-only null + rest",
                "replug-required", 0, ExitCodes.Gated);
        }

        // ---- real run ----
        var stageReports = new List<UnlockStageReport>();
        int framesSent = 0, ackExpected = 0, ackOk = 0, ackBad = 0, ackTimeouts = 0;

        logger.Event("run",
            ("cpu", cpu),
            ("loaderDirectory", loaderDirectory),
            ("stages", StageName(stages)),
            ("ackMode", AckModeName(ackMode)),
            ("replug", ReplugName(replug, replugWaitSeconds)),
            ("port", args.Value("port")),
            ("pid", Environment.ProcessId));

        var flasherOptions = new VcomFlasherOptions
        {
            PortName = args.Value("port") is { Length: > 0 } portArg ? portArg : null,
            AckMode = ackMode,
            AckTimeout = ackTimeout,
            Logger = logger,
            FrameObserver = frame =>
            {
                framesSent++;
                switch (frame.AckStatus)
                {
                    case "ok": ackExpected++; ackOk++; break;
                    case "bad": ackExpected++; ackBad++; break;
                    case "timeout": ackExpected++; ackTimeouts++; break;
                }
                if (!frameLog) return;
                string where = frame.Address is { } address
                    ? $" addr=0x{address:X8}" + (frame.Sequence is { } seq ? $" seq={seq}" : string.Empty)
                    : string.Empty;
                string ack = frame.AckStatus switch
                {
                    "ok" => "ack=0xAA",
                    "bad" => $"ack=0x{frame.Ack:X2}",
                    "timeout" => "ack=TIMEOUT",
                    _ => "ack=-",
                };
                context.Diag($"[frame] {frame.Kind,-5} {frame.Bytes,5}B{where} {ack}");
            },
        };

        var progressItems = new ObservableCollection<ProgressItemViewModel>(
            FirmwareUnlocker.CpuAddresses[cpu].Select(stage => new ProgressItemViewModel { FileName = stage.Name, StatusText = "Pending" }));

        var unlockOptions = new UnlockOptions
        {
            LoaderDirectory = loaderDirectory,
            Flasher = flasherOptions,
            Logger = logger,
            Stages = stages,
            StageObserver = report =>
            {
                stageReports.Add(report);
                context.Diag($"[stage] {report.Stage,-8} {report.Status,-8} {report.File} {TextFormat.Bytes(report.Bytes)}" +
                             $" frames={report.Frames} acks={report.Acks} {report.DurationMs}ms" +
                             (report.Note != null ? $" ({report.Note})" : string.Empty));
            },
            ConnectRetryWindow = replug == ReplugPolicy.Skip ? TimeSpan.Zero : TimeSpan.FromSeconds(replugWaitSeconds),
        };

        // Interaction handler: the GUI blocks on a dialog here; the CLI writes the same
        // instruction to stderr and either waits (prompt) or sleeps for the requested window.
        Func<string, Task<bool>>? interactionHandler = replug switch
        {
            ReplugPolicy.Skip => null,
            ReplugPolicy.Wait => async message =>
            {
                context.Diag("replug: " + message);
                context.Diag($"replug: waiting {replugWaitSeconds}s for the cable, then polling for 12d1:3609 (no START is re-sent).");
                await Task.Delay(TimeSpan.FromSeconds(replugWaitSeconds), cancellationToken);
                return true;
            },
            _ => async message =>
            {
                context.Diag("replug: " + message);
                if (Console.IsInputRedirected)
                {
                    context.Diag("replug: stdin is not interactive; treating it as 'wait 15s'.");
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                    return true;
                }
                context.Diag("replug: unplug/replug now, then press Enter (Ctrl+C aborts).");
                await Console.In.ReadLineAsync(cancellationToken);
                return true;
            },
        };

        if (stages == UnlockStageSelection.Rest)
            context.Diag("stage-only rest: the device must already be in VCOM mode after the replug; START is not re-sent.");
        else if (replug == ReplugPolicy.Skip)
            context.Diag("note: the device must already be in VCOM mode (HUAWEI USB COM 1.0, 12d1:3609) — a physical replug is never requested before START.");

        var stopwatch = Stopwatch.StartNew();
        UnlockResult result;
        try
        {
            result = await new FirmwareUnlocker().UnlockFastboot(
                cpu, progressItems, new SyncProgress(message => context.Diag("progress: " + message)),
                interactionHandler, useFastFlash, unlockOptions);
        }
        catch (OperationCanceledException)
        {
            context.Diag("error: cancelled");
            return Finish(context, false, "failed", planPayload, stageReports, ackMode, framesSent, "cancelled", "cancelled",
                stopwatch.ElapsedMilliseconds, ExitCodes.FileOrVerify);
        }
        stopwatch.Stop();

        int exitCode = result.IsSuccess ? ExitCodes.Ok : result.FailureKind switch
        {
            UnlockFailureKind.NoDevice => ExitCodes.NoDevice,
            UnlockFailureKind.Cancelled => ExitCodes.Gated,
            UnlockFailureKind.LocalFile => ExitCodes.FileOrVerify,
            _ => ExitCodes.DeviceRejected,
        };

        string failureKind = result.IsSuccess
            ? "none"
            : result.FailureKind.ToString().ToLowerInvariant() switch
            {
                "nodevice" => "no-device",
                "devicerejected" => "device-rejected",
                "localfile" => "local-file",
                "cancelled" => "cancelled",
                _ => "other",
            };

        if (!result.IsSuccess)
            context.Diag($"error: {result.Message} (failureKind={failureKind}, exit {exitCode})");
        else
            context.Diag($"unlock complete: {result.Message}");

        return Finish(context, result.IsSuccess, result.IsSuccess ? "applied" : "failed", planPayload, stageReports, ackMode,
            framesSent, result.Message, failureKind, stopwatch.ElapsedMilliseconds, exitCode,
            new { expected = ackExpected, ok = ackOk, bad = ackBad, timeouts = ackTimeouts });
    }

    /// <summary>
    /// Emits the single stdout JSON document (always, JSON mode or not: it is the machine-readable
    /// result) plus a one-line human summary on stderr, and returns the exit code.
    /// </summary>
    private static int Finish(
        CliContext context,
        bool applied,
        string gate,
        object plan,
        IReadOnlyList<UnlockStageReport> stages,
        VcomAckMode? ackMode,
        int framesSent,
        string message,
        string failureKind,
        long elapsedMs,
        int exitCode,
        object? ack = null)
    {
        var payload = new
        {
            command = "vcom-unlock",
            dryRun = false,
            applied,
            confirmed = true,
            gate,
            cpu = plan.GetType().GetProperty("cpu")!.GetValue(plan),
            loaderDirectory = plan.GetType().GetProperty("loaderDirectory")!.GetValue(plan),
            stageSelection = plan.GetType().GetProperty("stageSelection")!.GetValue(plan),
            replug = plan.GetType().GetProperty("replug")!.GetValue(plan),
            ackMode = ackMode is { } mode ? AckModeName(mode) : plan.GetType().GetProperty("ackMode")!.GetValue(plan),
            ack,
            framesSent,
            stages = stages.Select(stage => new
            {
                stage = stage.Stage,
                file = stage.File,
                address = "0x" + stage.Address.ToString("X8", CultureInfo.InvariantCulture),
                pwn = stage.Pwn,
                bytes = stage.Bytes,
                frames = stage.Frames,
                acks = stage.Acks,
                status = stage.Status,
                durationMs = stage.DurationMs,
                note = stage.Note,
            }).ToList(),
            elapsedMs,
            conclusion = applied ? "unlocked-fastboot-loaded" : "failed",
            failureKind,
            message,
            exitCode,
        };
        context.WriteJson(payload);
        context.Diag($"{conclusionText(applied)} exit={exitCode} elapsed={elapsedMs}ms frames={framesSent}");
        return exitCode;
    }

    private static string conclusionText(bool applied) => applied ? "conclusion: unlocked fastboot loaded" : "conclusion: failed";

    private static void WritePlanText(CliContext context, string cpu, string loaderDirectory,
        IReadOnlyList<(string Stage, string File, int Address, bool PwnFlag, bool Exists)> planned,
        VcomAckMode ackMode, ReplugPolicy replug, int replugWaitSeconds, UnlockStageSelection stages)
    {
        context.Output.WriteLine($"vcom unlock plan for {cpu}");
        context.Output.WriteLine($"loader directory: {loaderDirectory}");
        context.Output.WriteLine($"ack mode: {AckModeName(ackMode)}   replug: {ReplugName(replug, replugWaitSeconds)}   stage-only: {StageName(stages)}");
        context.Output.WriteLine("would upload, in order (device must already be in VCOM mode unless --stage-only rest):");
        context.Output.WriteLine(TextFormat.Table(planned.Select(stage => new[]
        {
            stage.Stage, "0x" + stage.Address.ToString("X8", CultureInfo.InvariantCulture), stage.File, stage.Exists ? "present" : "MISSING",
        }).ToList(), "STAGE", "ADDRESS", "LOADER", "FILE"));
        if (planned.Any(stage => !stage.Exists))
            context.Output.WriteLine("warning: missing loader files are skipped by the service; a run with none present exits 5.");
    }

    private static bool StageSelected(string stageName, UnlockStageSelection stages) => stages switch
    {
        UnlockStageSelection.Null => stageName == "null",
        UnlockStageSelection.Xloader => stageName is "null" or "XLOADER",
        UnlockStageSelection.Rest => stageName != "null",
        _ => true,
    };

    private enum ReplugPolicy { Skip, Prompt, Wait }

    private static VcomAckMode ParseAckMode(string? value) => (value ?? "lenient").Trim().ToLowerInvariant() switch
    {
        "lenient" or "" => VcomAckMode.Lenient,
        "strict" => VcomAckMode.Strict,
        "off" => VcomAckMode.Off,
        _ => throw new CliUsageException($"--ack-mode must be strict, lenient or off (got '{value}')"),
    };

    private static string AckModeName(VcomAckMode mode) => mode switch
    {
        VcomAckMode.Strict => "strict",
        VcomAckMode.Off => "off",
        _ => "lenient",
    };

    private static TimeSpan ParseAckTimeout(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return TimeSpan.FromSeconds(10);
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || seconds <= 0 || seconds > 600)
            throw new CliUsageException($"--ack-timeout must be a number of seconds in (0, 600] (got '{value}')");
        return TimeSpan.FromSeconds(seconds);
    }

    private static (ReplugPolicy, int) ParseReplug(string? value)
    {
        string text = (value ?? "skip").Trim().ToLowerInvariant();
        if (text is "skip" or "") return (ReplugPolicy.Skip, 0);
        if (text == "prompt") return (ReplugPolicy.Prompt, 15);
        if (text.StartsWith("wait:", StringComparison.Ordinal))
        {
            string number = text[5..];
            if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > 600)
                throw new CliUsageException($"--replug wait:<seconds> needs 1..600 seconds (got '{value}')");
            return (ReplugPolicy.Wait, seconds);
        }
        throw new CliUsageException($"--replug must be skip, prompt or wait:<seconds> (got '{value}')");
    }

    private static string ReplugName(ReplugPolicy policy, int waitSeconds) => policy switch
    {
        ReplugPolicy.Prompt => "prompt",
        ReplugPolicy.Wait => $"wait:{waitSeconds}",
        _ => "skip",
    };

    private static UnlockStageSelection ParseStages(string? value) => (value ?? "all").Trim().ToLowerInvariant() switch
    {
        "all" or "" => UnlockStageSelection.All,
        "null" => UnlockStageSelection.Null,
        "xloader" => UnlockStageSelection.Xloader,
        "rest" => UnlockStageSelection.Rest,
        _ => throw new CliUsageException($"--stage-only must be all, null, xloader or rest (got '{value}')"),
    };

    private static string StageName(UnlockStageSelection stages) => stages switch
    {
        UnlockStageSelection.Null => "null",
        UnlockStageSelection.Xloader => "xloader",
        UnlockStageSelection.Rest => "rest",
        _ => "all",
    };

    /// <summary>Progress&lt;T&gt; would post to the thread pool; stderr order must match the wire order.</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _report;
        public SyncProgress(Action<string> report) => _report = report;
        public void Report(string value) => _report(value);
    }
}
