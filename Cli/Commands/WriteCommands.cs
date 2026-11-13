using System.Text;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Services.Verdict;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// Plan-only write commands: they compute and print exactly what would be written,
/// then never touch the device. They require <c>--yes</c> (CLI) or <c>confirm: true</c>
/// (MCP) even to acknowledge the plan, and always exit 4. Real-write commands
/// (flash-ota / flash-xml here, vcom-unlock in VcomUnlockCommand.cs) do write once confirmed.
/// </summary>
internal abstract class GatedWriteCommand : ICliCommand
{
    public abstract string Name { get; }
    public abstract string McpName { get; }
    public abstract string Summary { get; }
    public abstract string Usage { get; }
    public CliWriteKind WriteKind => CliWriteKind.PlanOnly;
    public abstract IReadOnlyList<CliParam> Params { get; }

    protected abstract Task<WritePlan> BuildPlanAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken);

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        WritePlan plan;
        try
        {
            plan = await BuildPlanAsync(args, context, cancellationToken);
        }
        catch (CliUsageException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException or ArgumentException or CliFileException)
        {
            context.Diag($"error: {ex.Message}");
            return ExitCodes.FileOrVerify;
        }

        bool confirmed = context.Yes || args.HasFlag("yes");
        // Machine-readable gate signal: "plan-only" before acknowledgement, "confirmed"
        // once acknowledged (still nothing written — this class is plan-only by design).
        string status = confirmed ? "confirmed" : "plan-only";

        var payload = new
        {
            command = Name,
            dryRun = true,
            applied = false,
            confirmed,
            gate = status,
            gateReason = "plan-only command: the plan below was generated from the existing services, but nothing was flashed, erased or written.",
            plan = plan.Details,
            exitCode = ExitCodes.Gated,
        };

        if (context.Json)
        {
            context.WriteJson(payload);
        }
        else
        {
            foreach (string line in plan.Lines) context.Output.WriteLine(line);
            context.Output.WriteLine();
            context.Output.WriteLine($"gate: {status} (exit {ExitCodes.Gated})");
            if (!confirmed)
                context.Output.WriteLine("re-run with --yes (CLI) or confirm:true (MCP) to acknowledge the plan; nothing is ever written by this command.");
            else
                context.Output.WriteLine("plan acknowledged; no device state was changed (plan-only command).");
        }

        if (!confirmed) context.Diag("gate: plan printed; re-run with --yes to acknowledge");
        else context.Diag("gate: acknowledged; nothing was written (plan-only command)");

        return ExitCodes.Gated;
    }
}

internal sealed class FlashOtaCommand : ICliCommand
{
    public string Name => "flash-ota";
    public string McpName => "flash_ota";
    public string Summary => "Flash every partition of one or more UPDATE.APP / PTABLE.APP archives (Full OTA equivalent; writes only with --yes).";
    public string Usage =>
        "flash-ota <file>... [--partitions a,b] [--exclude x,y] [--allow-redline] [--yes] [--json]\n" +
        "  files are flashed in the order given (base PTABLE, base UPDATE, cust PTABLE, cust UPDATE, preload PTABLE, preload UPDATE)\n" +
        "  without --yes the plan is printed and nothing is written (exit 4); with --yes a real write happens";
    public CliWriteKind WriteKind => CliWriteKind.RealWrite;
    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("file", "string[]", "UPDATE.APP / PTABLE.APP paths, in flash order", Required: true, Positional: true),
        new CliParam("files", "string", "same as the positional files, comma separated (use from MCP)"),
        new CliParam("partitions", "string", "comma separated whitelist of partition names to flash"),
        new CliParam("exclude", "string", "comma separated partition names to skip"),
        new CliParam("allow-redline", "boolean", "permit writing oeminfo/nvme/modem_secure/persist/modemnvm*/certification/hisee_encos/veritykey"),
        new CliParam("work-dir", "string", "staging directory for extracted images (default: <dir of first file>/.kirintool-cli-tmp)"),
        new CliParam("strict-names", "boolean", "refuse instead of skipping partitions the loader does not expose"),
        new CliParam("no-backup", "boolean", "skip the automatic pre-op backup of redline partitions + ptable"),
        new CliParam("backup-dir", "string", "directory for the pre-op backup (default: $KIRIN_BACKUP_DIR or ~/.local/share/kirin-tool/backups)"),
        new CliParam("require-backup", "boolean", "abort instead of writing when the pre-op backup is incomplete"),
        new CliParam("yes", "boolean", "acknowledge the plan and perform the write"),
        new CliParam("json", "boolean", "structured output"),
    };

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        PreOpBackupCli.ValidateFlags(args);
        List<string> inputs = args.Positionals.ToList();
        string? filesValue = args.Value("files");
        if (!string.IsNullOrWhiteSpace(filesValue))
            inputs.AddRange(SplitList(filesValue));
        if (inputs.Count == 0) throw new CliUsageException("flash-ota requires at least one UPDATE.APP / PTABLE.APP path");

        var paths = new List<string>();
        foreach (string input in inputs)
        {
            string resolved = Path.GetFullPath(input, context.WorkingDirectory);
            if (!File.Exists(resolved)) throw new CliFileException($"file not found: {resolved}");
            paths.Add(resolved);
        }

        OtaPlan plan;
        try
        {
            plan = OtaFlashRunner.BuildPlan(paths, SplitList(args.Value("partitions")), SplitList(args.Value("exclude")), cancellationToken);
        }
        catch (CliFileException ex)
        {
            context.Diag($"error: {ex.Message}");
            return ExitCodes.FileOrVerify;
        }

        var rows = plan.Entries.Select(entry => new[]
        {
            entry.Partition,
            TextFormat.Bytes(entry.Size),
            entry.IsRedline ? "REDLINE" : entry.Source,
        }).ToList();

        var details = new
        {
            files = paths,
            partitionCount = plan.Entries.Count,
            skippedCount = plan.Skipped.Count,
            totalBytes = plan.TotalBytes,
            redlines = plan.Redlines,
            warnings = plan.Warnings,
            partitions = plan.Entries.Select(entry => new
            {
                partition = entry.Partition,
                size = entry.Size,
                source = entry.Source,
                redline = entry.IsRedline,
                total = entry.Size,
            }).ToList(),
            skipped = plan.Skipped.Select(entry => entry.Partition).ToList(),
        };

        // The gate decision comes before any output so stdout carries exactly one JSON
        // object: a refusal used to print the plan-only document first, which made the
        // stream unparsable for MCP clients and tests (two concatenated documents).
        bool allowRedline = args.HasFlag("allow-redline");
        bool redlineRefused = plan.Redlines.Count > 0 && !allowRedline;
        bool confirmed = context.Yes || args.HasFlag("yes");

        if (context.Json)
        {
            if (redlineRefused)
                context.WriteJson(new
                {
                    command = Name, dryRun = true, applied = false, gate = "refused",
                    redlines = plan.Redlines,
                    reason = "redline partitions present without --allow-redline",
                    plan = details, exitCode = ExitCodes.Gated,
                });
            else if (!confirmed)
                context.WriteJson(new { command = Name, dryRun = true, applied = false, gate = "plan-only", plan = details, exitCode = ExitCodes.Gated });
        }
        else
        {
            context.Output.WriteLine($"flash plan: {plan.Entries.Count} partitions, {TextFormat.Bytes(plan.TotalBytes)}");
            context.Output.WriteLine(TextFormat.Table(rows, "PARTITION", "SIZE", "SOURCE"));
            foreach (string warning in plan.Warnings) context.Output.WriteLine($"warning: {warning}");
            context.Output.WriteLine($"skipped by policy: {plan.Skipped.Count} entr{(plan.Skipped.Count == 1 ? "y" : "ies")}");
        }

        if (redlineRefused)
        {
            context.Diag($"refused: plan writes redline partitions ({string.Join(", ", plan.Redlines)}); pass --allow-redline to override");
            return ExitCodes.Gated;
        }

        if (!confirmed)
        {
            context.Diag("gate: plan only; re-run with --yes to flash");
            return ExitCodes.Gated;
        }

        // Approval gate (docs/approval-gate.md): --yes acknowledged the plan, but the write still
        // needs a gate decision. A plan that touches redline partitions makes the gate demand the
        // typed confirm phrase, so a script cannot --yes --allow-redline its way through.
        ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(ApprovalRequests.For(
            "flash-ota",
            argv: new[] { "fastboot", "flash", "<plan>", string.Join(",", paths) },
            targets: plan.Entries.Select(entry => entry.Partition).ToArray(),
            acknowledged: confirmed,
            redlineOverride: allowRedline));
        if (!admission.IsApproved)
            return ApprovalReport.Refuse(context, Name, admission.Decision);

        FastbootSession? session = await FastbootSession.TryOpenAsync();
        if (session == null)
        {
            context.Diag("error: fastboot binary not found");
            return ExitCodes.NoDevice;
        }
        if (!session.Connected)
        {
            context.Diag("error: no device in fastboot mode");
            return ExitCodes.NoDevice;
        }

        // Pre-op backup runs before any staging or writing; dump failures degrade to
        // warnings (see PreOpBackupCli) and only --require-backup turns them into a refusal.
        PreOpBackupResult backup = await PreOpBackupCli.RunForWriteAsync(
            args, context, "flash-ota", plan.Entries.Select(entry => entry.Partition), session, cancellationToken);
        int backupRefusal = PreOpBackupCli.RefusalExit(args, backup, context, "flash-ota");
        if (backupRefusal != ExitCodes.Ok) return backupRefusal;

        string? workDirArg = args.Value("work-dir");
        bool customStaging = workDirArg is { Length: > 0 };
        string workDirectory = customStaging
            ? Path.GetFullPath(workDirArg!, context.WorkingDirectory)
            : Path.Combine(Path.GetDirectoryName(paths[0]) ?? context.WorkingDirectory, ".kirintool-cli-tmp");
        try { Directory.CreateDirectory(workDirectory); }
        catch (Exception ex)
        {
            context.Diag($"error: cannot create staging directory {workDirectory}: {ex.Message}");
            return ExitCodes.FileOrVerify;
        }
        context.Diag($"staging directory: {workDirectory}");

        try
        {
            return await FlashStagedAsync(plan, session, workDirectory, args, context, backup, cancellationToken);
        }
        finally
        {
            // The default staging directory is disposable scratch space next to the payload;
            // an explicitly requested --work-dir is left alone for inspection.
            if (!customStaging)
            {
                try
                {
                    Directory.Delete(workDirectory, recursive: true);
                    context.Diag($"default staging directory removed: {workDirectory}");
                }
                catch (Exception ex)
                {
                    context.Diag($"warning: could not remove default staging directory {workDirectory}: {ex.Message}");
                }
            }
        }

        static async Task<int> FlashStagedAsync(OtaPlan plan, FastbootSession session, string workDirectory, ParsedArgs args, CliContext context, PreOpBackupResult backup, CancellationToken cancellationToken)
        {
            var (resolvedNames, unresolved) = await OtaFlashRunner.ResolveNamesAsync(
                session.Client, plan.Entries.Select(entry => entry.Partition), context, cancellationToken);
            if (unresolved.Count > 0)
            {
                // The temporary loader publishes its own partition map, which can be narrower than the
                // GPT a package was built for (e.g. no "kpatch" on ELS loaders). Skipping is safe for
                // those; --strict-names keeps the old all-or-nothing behaviour.
                if (args.HasFlag("strict-names"))
                {
                    context.Diag($"refused: {unresolved.Count} planned partition(s) do not exist on this loader: {string.Join(", ", unresolved)}");
                    return ExitCodes.DeviceRejected;
                }
                context.Diag($"warning: skipping {unresolved.Count} partition(s) the loader does not expose: {string.Join(", ", unresolved)}");
                var unresolvedNames = new HashSet<string>(unresolved.Select(line => line.Split(" -> ")[0]), StringComparer.OrdinalIgnoreCase);
                plan = new OtaPlan
                {
                    Entries = plan.Entries.Where(entry => !unresolvedNames.Contains(entry.Partition)).ToList(),
                    SuperParts = plan.SuperParts.Where(entry => !unresolvedNames.Contains(entry.Partition)).ToList(),
                    Skipped = plan.Skipped,
                    Redlines = plan.Redlines,
                    Warnings = plan.Warnings,
                    TotalBytes = plan.TotalBytes,
                };
            }

            List<OtaFlashOutcome> outcomes = await OtaFlashRunner.ExecuteAsync(plan, workDirectory, resolvedNames, session.Client, context, cancellationToken);
            int ok = outcomes.Count(outcome => outcome.Ok);
            var failed = outcomes.Where(outcome => !outcome.Ok).Select(outcome => new { partition = outcome.Partition, error = outcome.Error }).ToList();

            // Exit code and JSON agree: nothing flashed is a file/verify failure (5), a full
            // success is 0, and a partial write is a device rejection (3).
            int exitCode = ok == 0 ? ExitCodes.FileOrVerify : (ok == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected);

            // applied/gate follow the device answers: nothing flashed is a failure, a partial
            // write is "partial", only a full success may say "applied" (D-02).
            string otaGate = ok == 0 ? "failed" : (ok == outcomes.Count ? "applied" : "partial");
            string? otaReason = ok == outcomes.Count
                ? null
                : ok == 0
                    ? "every partition write failed"
                    : (outcomes.Count - ok) + " of " + outcomes.Count + " partition writes failed";

            if (context.Json)
                context.WriteJson(new
                {
                    command = "flash-ota",
                    dryRun = false,
                    applied = ok > 0,
                    gate = otaGate,
                    reason = otaReason,
                    fastboot = session.ExecutablePath,
                    backup = PreOpBackupCli.JsonPayload(backup),
                    flashed = ok,
                    total = outcomes.Count,
                    failed,
                    exitCode,
                });
            else
                context.Output.WriteLine($"flashed {ok}/{outcomes.Count} partitions");

            return exitCode;
        }
    }

    internal static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
internal sealed class FlashXmlCommand : ICliCommand
{
    public string Name => "flash-xml";
    public string McpName => "flash_xml";
    public string Summary => "Flash the <image> entries of a Huawei flashing XML (writes only with --yes).";
    public string Usage =>
        "flash-xml <flashing.xml|dir> [--partitions a,b] [--exclude x,y] [--allow-redline] [--yes] [--json]\n" +
        "  unresolved identifiers are skipped unless --strict-names (which refuses with exit 3)\n" +
        "  identifiers whose images are missing abort before any write; without --yes only the plan is printed (exit 4); with --yes a real write happens";
    public CliWriteKind WriteKind => CliWriteKind.RealWrite;
    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("path", "string", "flashing XML file, or a directory containing one", Required: true, Positional: true),
        new CliParam("partitions", "string", "comma separated whitelist of identifiers to flash"),
        new CliParam("exclude", "string", "comma separated identifiers to skip"),
        new CliParam("allow-redline", "boolean", "permit writing oeminfo/nvme/modem_secure/persist/modemnvm*/certification/hisee_encos/veritykey"),
        new CliParam("strict-names", "boolean", "refuse instead of skipping identifiers the loader does not expose"),
        new CliParam("no-backup", "boolean", "skip the automatic pre-op backup of redline identifiers + ptable"),
        new CliParam("backup-dir", "string", "directory for the pre-op backup (default: $KIRIN_BACKUP_DIR or ~/.local/share/kirin-tool/backups)"),
        new CliParam("require-backup", "boolean", "abort instead of writing when the pre-op backup is incomplete"),
        new CliParam("yes", "boolean", "acknowledge the plan and perform the write"),
        new CliParam("json", "boolean", "structured output"),
    };

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        PreOpBackupCli.ValidateFlags(args);
        if (args.Positionals.Count < 1) throw new CliUsageException("flash-xml requires a flashing XML path");
        string path = Path.GetFullPath(args.Positionals[0], context.WorkingDirectory);

        if (Directory.Exists(path))
        {
            var candidates = Directory.GetFiles(path, "*.xml", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count == 0) throw new CliFileException($"no *.xml flashing file found in {path}");
            path = candidates.Count > 1
                ? candidates.FirstOrDefault(file => Path.GetFileName(file).Contains("flash", StringComparison.OrdinalIgnoreCase)) ?? candidates[0]
                : candidates[0];
        }

        if (!File.Exists(path)) throw new CliFileException($"file not found: {path}");

        string? fastbootPath = BinaryLocator.FindFastboot();
        var service = new FastbootFlasherService(new FastbootClient(fastbootPath ?? "fastboot"));
        List<FastbootPartition> parsed = service.ParseFlashingXml(path);
        if (parsed.Count == 0) throw new CliFileException($"no flashable <image> entries parsed from {path}");

        List<string> only = FlashOtaCommand.SplitList(args.Value("partitions"));
        List<string> excluded = FlashOtaCommand.SplitList(args.Value("exclude"));
        var onlySet = only.Count > 0 ? new HashSet<string>(only, StringComparer.OrdinalIgnoreCase) : null;
        var excludeSet = excluded.Count > 0 ? new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase) : null;

        var selected = new List<FastbootPartition>();
        var skipped = new List<string>();
        var redlines = new List<string>();
        var missing = new List<string>();

        foreach (FastbootPartition partition in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identifier = partition.Identifier ?? string.Empty;
            if (onlySet != null && !onlySet.Contains(identifier) && !onlySet.Contains(partition.Name ?? string.Empty)) { skipped.Add(identifier); continue; }
            if (excludeSet != null && (excludeSet.Contains(identifier) || excludeSet.Contains(partition.Name ?? string.Empty)))
            {
                skipped.Add(identifier);
                continue;
            }
            if (FlashPolicy.IsRedline(identifier) || FlashPolicy.IsRedline(partition.Name ?? string.Empty))
                redlines.Add(identifier);
            if (partition.DumpPath == null || !File.Exists(partition.DumpPath))
                missing.Add($"{identifier} ({(partition.DumpPath ?? "unresolved")})");
            selected.Add(partition);
        }

        bool allowRedline = args.HasFlag("allow-redline");
        bool redlineRefused = redlines.Count > 0 && !allowRedline;
        bool confirmed = context.Yes || args.HasFlag("yes");

        // Name-resolution pre-flight moved to the plan phase (aligned with flash-ota's
        // skip semantics, but earlier): an unconfirmed plan already shows which
        // identifiers this loader does not expose, and the default is to SKIP them
        // with a warning instead of attempting the write anyway. --strict-names
        // still refuses before any write (exit 3).
        bool strictNames = args.HasFlag("strict-names");
        // The pre-flight is best effort on purpose: an unconfirmed plan must still work
        // without a device (offline planning), so "no device" only means the loader's
        // partition names could not be checked. A real write (--yes) still requires the
        // device below, and --strict-names needs one because it refuses on resolution.
        FastbootSession? session = await FastbootSession.TryOpenAsync();
        bool deviceReady = session is { Connected: true };
        Dictionary<string, string> resolvedNames = new(StringComparer.OrdinalIgnoreCase);
        List<string> unresolved = new();
        if (deviceReady && missing.Count == 0)
        {
            (resolvedNames, unresolved) = await OtaFlashRunner.ResolveNamesAsync(
                session!.Client,
                selected.Select(partition => partition.Identifier ?? string.Empty).Where(name => name.Length > 0),
                context,
                cancellationToken);
        }
        else if (strictNames)
        {
            context.Diag("error: --strict-names needs a connected device to resolve loader partition names");
            return ExitCodes.NoDevice;
        }
        else if (missing.Count == 0)
            context.Diag("warning: no device in fastboot mode; loader partition names were not checked");

        var unresolvedNames = new HashSet<string>(unresolved.Select(line => line.Split(" -> ")[0]), StringComparer.OrdinalIgnoreCase);
        if (unresolved.Count > 0 && strictNames)
        {
            context.Diag($"refused: {unresolved.Count} identifier(s) do not exist on this loader: {string.Join(", ", unresolved)}");
            return ExitCodes.DeviceRejected;
        }
        var writeList = new List<FastbootPartition>();
        var skippedUnresolved = new List<string>();
        foreach (FastbootPartition partition in selected)
        {
            string identifier = partition.Identifier ?? string.Empty;
            if (unresolvedNames.Contains(identifier)) skippedUnresolved.Add(identifier);
            else writeList.Add(partition);
        }
        if (skippedUnresolved.Count > 0)
            context.Diag($"warning: skipping {skippedUnresolved.Count} identifier(s) the loader does not expose: {string.Join(", ", skippedUnresolved)}");

        // One JSON document per invocation (see the same note in flash-ota): the
        // refusal path used to append a second document to the plan-only one.
        // Missing images are checked before the redline gate and win the reason, but both
        // are policy refusals: the JSON carries gate=refused plus a reason that says which.
        string? refusalReason = missing.Count > 0
            ? $"missing image(s): {string.Join(", ", missing)}"
            : redlineRefused
                ? "redline identifiers present without --allow-redline"
                : null;

        if (context.Json)
        {
            var planPayload = new
            {
                command = Name,
                dryRun = true,
                applied = false,
                xml = path,
                partitionCount = writeList.Count,
                skipped,
                skippedUnresolved,
                redlines,
                missingImages = missing,
                partitions = writeList.Select(partition => new
                {
                    identifier = partition.Identifier,
                    name = partition.Name,
                    image = partition.DumpPath,
                    redline = FlashPolicy.IsRedline(partition.Identifier ?? string.Empty),
                }).ToList(),
            };
            if (refusalReason != null)
                context.WriteJson(new
                {
                    command = planPayload.command, dryRun = true, applied = false, gate = "refused",
                    redlines, reason = refusalReason,
                    xml = path, partitionCount = writeList.Count, skipped, skippedUnresolved, missingImages = missing,
                    partitions = planPayload.partitions, exitCode = missing.Count > 0 ? ExitCodes.FileOrVerify : ExitCodes.Gated,
                });
            else if (!confirmed)
                context.WriteJson(new
                {
                    command = planPayload.command, dryRun = true, applied = false, gate = "plan-only",
                    xml = path, partitionCount = writeList.Count, skipped, skippedUnresolved, redlines, missingImages = missing,
                    partitions = planPayload.partitions, exitCode = ExitCodes.Gated,
                });
        }
        else
        {
            context.Output.WriteLine($"flash plan for {path}: {writeList.Count} images");
            context.Output.WriteLine(TextFormat.Table(writeList.Select(partition => new[]
            {
                partition.Identifier ?? "?",
                partition.Name ?? "?",
                FlashPolicy.IsRedline(partition.Identifier ?? string.Empty) ? "REDLINE" : "ok",
                partition.DumpPath ?? "(unresolved)",
            }).ToList(), "IDENTIFIER", "NAME", "STATE", "PATH"));
            if (skipped.Count > 0) context.Output.WriteLine($"skipped: {string.Join(", ", skipped)}");
            if (skippedUnresolved.Count > 0) context.Output.WriteLine($"skipped unresolved (loader does not expose): {string.Join(", ", skippedUnresolved)}");
        }

        if (missing.Count > 0)
        {
            context.Diag($"refused: {missing.Count} image(s) missing: {string.Join(", ", missing)}");
            return ExitCodes.FileOrVerify;
        }
        if (redlineRefused)
        {
            context.Diag($"refused: plan writes redline identifiers ({string.Join(", ", redlines)}); pass --allow-redline to override");
            return ExitCodes.Gated;
        }

        if (!confirmed)
        {
            context.Diag("gate: plan only; re-run with --yes to flash");
            return ExitCodes.Gated;
        }

        // Approval gate: same funnel as flash-ota and as the GUI's ConfirmRedlineWriteAsync.
        ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(ApprovalRequests.For(
            "flash-xml",
            argv: new[] { "fastboot", "flash-xml", path },
            targets: writeList.Select(partition => partition.Identifier ?? partition.Name ?? string.Empty).ToArray(),
            acknowledged: confirmed,
            redlineOverride: args.HasFlag("allow-redline")));
        if (!admission.IsApproved)
            return ApprovalReport.Refuse(context, Name, admission.Decision);

        // The plan phase opened the session best effort; a real write needs it now.
        if (session == null) { context.Diag("error: fastboot binary not found"); return ExitCodes.NoDevice; }
        if (!session.Connected) { context.Diag("error: no device in fastboot mode"); return ExitCodes.NoDevice; }

        PreOpBackupResult backup = await PreOpBackupCli.RunForWriteAsync(
            args, context, "flash-xml", writeList.Select(partition => partition.Identifier ?? string.Empty), session, cancellationToken);
        int backupRefusal = PreOpBackupCli.RefusalExit(args, backup, context, "flash-xml");
        if (backupRefusal != ExitCodes.Ok) return backupRefusal;

        var outcomes = new List<OtaFlashOutcome>();
        // Plan-level op for the XML write (docs/op-scope-map.md row 2): one file for the run,
        // one step per image. The per-command scope (fastboot.flash) stays a separate file.
        using var op = OpLog.Begin(OpNames.FlashXml, OpLog.FlashTool, new[] { path }, "flashing XML write plan");
        op.Step("plan: " + writeList.Count + " image(s) to write, " + redlines.Count + " redline identifier(s)");
        foreach (string skippedName in skipped)
            op.Step(OpLog.SkippedPrefix + skippedName + ": excluded by --exclude / not in --partitions");
        foreach (string skippedName in skippedUnresolved)
            op.Step(OpLog.SkippedPrefix + skippedName + ": the loader does not expose this identifier");
        for (int i = 0; i < writeList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FastbootPartition partition = writeList[i];
            string flashName = resolvedNames.TryGetValue(partition.Identifier ?? string.Empty, out string? deviceName)
                ? deviceName
                : (partition.Identifier ?? string.Empty).ToLowerInvariant();
            context.Diag($"[{i + 1}/{writeList.Count}] flash {flashName} <- {TextFormat.Bytes(new FileInfo(partition.DumpPath!).Length)}");
            ProcessResult result = await session.Client.FlashPartition(flashName, partition.DumpPath!);
            bool ok = result.IsSuccess && (result.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);
            outcomes.Add(new OtaFlashOutcome(flashName, ok, ok ? null : (result.Output ?? string.Empty).Trim()));
            context.Diag(ok ? $"    OKAY {flashName}" : $"    FAILED {flashName}: {result.Output}");
            op.Step((ok ? OpLog.OkPrefix : OpLog.FailedPrefix) + "flash " + flashName + " <- " + OpLog.FileStep(partition.DumpPath)
                + " (identifier " + (partition.Identifier ?? "?") + ")"
                + (ok ? "" : ", loader said: " + OpLog.OneLine(result.Output)));
        }

        int okCount = outcomes.Count(outcome => outcome.Ok);
        op.Result(outcomes.Count > 0 && okCount == outcomes.Count, null, null, null, null,
            OpLog.OutcomeVerdict(okCount, outcomes.Count),
            "verdict rule: exit code 0 AND the loader printed OKAY, per image");
        // JSON exitCode matches the real return code: nothing flashed is a file/verify
        // failure (5), a full success is 0, a partial write is a device rejection (3).
        int exitCode = okCount == 0 ? ExitCodes.FileOrVerify : (okCount == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected);
        // Same rule as flash-ota: the JSON verdict is the device answers, never a default (D-02).
        string xmlGate = okCount == 0 ? "failed" : (okCount == outcomes.Count ? "applied" : "partial");
        string? xmlReason = okCount == outcomes.Count
            ? null
            : okCount == 0
                ? "every identifier write failed"
                : (outcomes.Count - okCount) + " of " + outcomes.Count + " identifier writes failed";
        if (context.Json)
            context.WriteJson(new
            {
                command = Name,
                dryRun = false,
                applied = okCount > 0,
                gate = xmlGate,
                reason = xmlReason,
                fastboot = session.ExecutablePath,
                backup = PreOpBackupCli.JsonPayload(backup),
                flashed = okCount,
                total = outcomes.Count,
                failed = outcomes.Where(outcome => !outcome.Ok).Select(outcome => new { partition = outcome.Partition, error = outcome.Error }).ToList(),
                exitCode,
            });
        else
            context.Output.WriteLine($"flashed {okCount}/{outcomes.Count} images");

        return exitCode;
    }
}

internal sealed class OemInfoCommand : GatedWriteCommand
{
    public override string Name => "oeminfo";
    public override string McpName => "oeminfo";
    public override string Summary => "Plan an OEMINFO model/region rewrite from a file or a device dump (gate-only: never flashes).";
    public override string Usage => "oeminfo --model <model> --region <vendor-country> (--input <oeminfo.img> | --pull) [--yes] [--json]";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("model", "string", "new model string written into the OEMINFO model entry", Required: true),
        new CliParam("region", "string", "new vendor/country string written into the region entry", Required: true),
        new CliParam("input", "string", "local oeminfo.img to convert"),
        new CliParam("pull", "boolean", "dump oeminfo from the connected device instead of --input"),
    };

    protected override async Task<WritePlan> BuildPlanAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        string? model = args.Value("model");
        string? region = args.Value("region");
        if (string.IsNullOrWhiteSpace(model)) throw new CliUsageException("oeminfo requires --model <model>");
        if (string.IsNullOrWhiteSpace(region)) throw new CliUsageException("oeminfo requires --region <vendor-country>");

        bool pull = args.HasFlag("pull");
        string? input = args.Value("input");
        if (pull && input != null) throw new CliUsageException("oeminfo accepts either --pull or --input, not both");
        if (!pull && input == null) throw new CliUsageException("oeminfo requires --input <oeminfo.img> or --pull");

        string? inputPath = null;
        int? detectedVersion = null;
        object? conversion = null;

        if (!pull)
        {
            inputPath = Path.GetFullPath(input!, context.WorkingDirectory);
            if (!File.Exists(inputPath)) throw new CliFileException($"file not found: {inputPath}");
            detectedVersion = TryDetectOemInfoVersion(inputPath);

            var service = new OemInfoService(new FastbootClient(BinaryLocator.FindFastboot() ?? "fastboot"));
            cancellationToken.ThrowIfCancellationRequested();
            OemInfoConversionResult result = await service.ConvertOemInfo(false, inputPath, model!, region!);
            service.CleanupTempFiles();
            conversion = new
            {
                success = result.IsSuccess,
                error = result.ErrorMessage,
                outputBytes = result.ConvertedData?.Length,
                pulledFromDevice = result.PulledFromDevice,
            };
            if (!result.IsSuccess)
                throw new CliFileException($"OEMINFO conversion failed: {result.ErrorMessage}");
        }

        var details = new
        {
            mode = pull ? "device-pull" : "file",
            input = inputPath,
            model,
            region,
            target = "oeminfo",
            oemInfoVersion = detectedVersion,
            conversion,
            implementation = "Services/OemInfoService.ConvertOemInfo + FastbootClient.FlashOemInfo (flash not invoked in this build)",
        };

        var lines = new List<string>
        {
            $"oeminfo plan ({(pull ? "pull from device" : inputPath)})",
            $"model:  {model}",
            $"region: {region}",
            $"target partition: oeminfo",
        };
        if (detectedVersion != null) lines.Add($"detected OEMINFO version: {detectedVersion}");
        if (!pull) lines.Add("conversion: succeeded locally (in-memory, temp files cleaned)");
        else lines.Add("conversion: would dump oeminfo from the device first");

        return new WritePlan(details, lines);
    }

    private static int? TryDetectOemInfoVersion(string path)
    {
        try
        {
            byte[] data = File.ReadAllBytes(path);
            byte[] magic = Encoding.ASCII.GetBytes("OEM_INFO");
            for (int offset = 0; offset + 12 <= data.Length; offset += 0x400)
            {
                if (data.AsSpan(offset, magic.Length).SequenceEqual(magic))
                    return BitConverter.ToInt32(data, offset + 8);
            }
        }
        catch (Exception)
        {
            // best effort only
        }
        return null;
    }
}

internal sealed class DowngradeEnableCommand : GatedWriteCommand
{
    public override string Name => "downgrade-enable";
    public override string McpName => "downgrade_enable";
    public override string Summary => "Plan the OEMINFO version erase that enables downgrade (gate-only: never erases in this build).";
    public override string Usage => "downgrade-enable [--yes] [--json]";
    public override IReadOnlyList<CliParam> Params => Array.Empty<CliParam>();

    protected override Task<WritePlan> BuildPlanAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        string[] commands =
        {
            "oem oeminfoerase-amssver",
            "oem oeminfoerase-basever",
            "oem oeminfoerase-custver",
            "oem oeminfoerase-preloadver",
        };

        var steps = commands.Select((command, index) => new { step = index + 1, command }).ToList();
        var details = new
        {
            implementation = "Services/FastbootClient.EnableDowngradeWithSteps (not invoked in this build)",
            target = "oeminfo version fields",
            steps,
        };

        var lines = new List<string>
        {
            "downgrade-enable plan",
            "would run these fastboot commands in order (irreversible version-field erase):",
            TextFormat.Table(steps.Select(step => new[] { step.step.ToString(), step.command }).ToList(), "STEP", "COMMAND"),
        };

        return Task.FromResult(new WritePlan(details, lines));
    }
}

/// <summary>
/// CLI glue for the pre-op backup: shared by flash-ota / flash-xml. Dumps degrade to
/// stderr warnings; only an explicit --require-backup turns an incomplete backup into
/// a refusal (exit 4) before the first write.
/// </summary>
internal static class PreOpBackupCli
{
    /// <summary>
    /// Fails fast on contradictory backup flags: <c>--no-backup</c> disables the backup,
    /// <c>--require-backup</c> refuses to write without one. Accepting both used to mean the
    /// stricter flag silently lost (audit S10/S12 family).
    /// </summary>
    public static void ValidateFlags(ParsedArgs args)
    {
        if (args.HasFlag("no-backup") && args.HasFlag("require-backup"))
            throw new CliUsageException("--no-backup cannot be combined with --require-backup: one disables the backup, the other refuses to write without it");
    }

    public static async Task<PreOpBackupResult> RunForWriteAsync(
        ParsedArgs args,
        CliContext context,
        string trigger,
        IEnumerable<string> writePartitions,
        FastbootSession session,
        CancellationToken cancellationToken)
    {
        if (args.HasFlag("no-backup"))
        {
            ValidateFlags(args);
            // Audit line, not just a passing note: a skipped backup is a decision the log and the
            // machine-readable JSON must both carry (audit S10/S11).
            context.Diag($"pre-op backup SKIPPED (--no-backup): nothing was dumped before '{trigger}'; " +
                         "a partial write cannot be rolled back from this run");
            return PreOpBackupResult.Skipped;
        }

        var flasher = new FastbootFlasherService(session.Client);
        var backup = new PreOpBackup(flasher, variable => DeviceVars.TryReadAsync(session.Client, variable));
        PreOpBackupResult result = await backup.RunAsync(new PreOpBackupOptions
        {
            Trigger = trigger,
            WritePartitions = writePartitions.ToList(),
            BackupDir = args.Value("backup-dir"),
            RequireBackup = args.HasFlag("require-backup"),
            FastbootPath = session.ExecutablePath,
        }, cancellationToken);

        if (result.Error != null)
            context.Diag($"warning: pre-op backup failed: {result.Error}");
        else if (!result.Complete)
            context.Diag($"warning: pre-op backup incomplete ({result.OkCount}/{result.TotalCount}); backup directory: {result.Directory}");
        else
            context.Diag($"pre-op backup complete ({result.OkCount}/{result.TotalCount}); backup directory: {result.Directory}");

        return result;
    }

    /// <summary>Exit code for a --require-backup refusal, or Ok when the write may proceed.</summary>
    public static int RefusalExit(ParsedArgs args, PreOpBackupResult result, CliContext context, string command = "write")
    {
        // The decision itself lives in Services/Verdict/BackupGate.cs so the same rule can be
        // exercised without a device: "not attempted" (directory/manifest creation failed) is a
        // refusal just like "incomplete" (audit S10/S11).
        BackupGateVerdict verdict = BackupGate.Evaluate(args.HasFlag("require-backup"), result);
        if (!verdict.Refuse) return ExitCodes.Ok;

        context.Diag($"refused: --require-backup set and {verdict.Reason}; nothing was written (exit {verdict.ExitCode})");
        if (context.Json)
        {
            context.WriteJson(new
            {
                command,
                dryRun = false,
                applied = false,
                verified = false,
                gate = "refused",
                reason = "require_backup_failed",
                refusal = verdict.Reason,
                backup = JsonPayload(result),
                exitCode = verdict.ExitCode,
            });
        }
        return verdict.ExitCode;
    }

    /// <summary>Machine-readable summary embedded in the applied-write JSON document.</summary>
    public static object JsonPayload(PreOpBackupResult result) => new
    {
        dir = result.Directory,
        complete = result.Complete,
        skipped = !result.Attempted,
        manifest = result.ManifestPath,
        error = result.Error,
        partitions = result.Partitions.Select(p => new
        {
            name = p.Name,
            file = p.File,
            size = p.Size,
            sha256 = p.Sha256,
            status = p.Status,
            reason = p.Reason,
        }).ToList(),
    };
}
