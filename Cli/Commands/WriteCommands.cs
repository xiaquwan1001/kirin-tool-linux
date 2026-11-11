using System.Text;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// Gate for the second batch: write commands compute and print exactly what would be
/// written, then refuse to touch the device. They require <c>--yes</c> (CLI) or
/// <c>confirm: true</c> (MCP) even to leave "plan" mode, and the actual write path is
/// intentionally not implemented in this build, so they still exit 4.
/// </summary>
internal abstract class GatedWriteCommand : ICliCommand
{
    public abstract string Name { get; }
    public abstract string McpName { get; }
    public abstract string Summary { get; }
    public abstract string Usage { get; }
    public bool IsWrite => true;
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
        string status = confirmed ? "confirmed-but-write-path-not-implemented" : "plan-only";

        var payload = new
        {
            command = Name,
            dryRun = true,
            applied = false,
            confirmed,
            gate = new
            {
                required = "--yes (CLI) or confirm:true (MCP)",
                status,
                reason = "Write paths are gate-only in this build: the plan below was generated from the existing services, but nothing was flashed, erased or written.",
                exitCode = ExitCodes.Gated,
            },
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
                context.Output.WriteLine("re-run with --yes (CLI) or confirm:true (MCP) to acknowledge the plan; this build still will not write.");
            else
                context.Output.WriteLine("write path not implemented in this build; no device state was changed.");
        }

        if (!confirmed) context.Diag("gate: plan printed; re-run with --yes to acknowledge");
        else context.Diag("gate: acknowledged, but the write path is not implemented in this build");

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
        "  without --yes the plan is printed and nothing is written (exit 4)";
    public bool IsWrite => true;
    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("file", "string[]", "UPDATE.APP / PTABLE.APP paths, in flash order", Required: true, Positional: true),
        new CliParam("files", "string", "same as the positional files, comma separated (use from MCP)"),
        new CliParam("partitions", "string", "comma separated whitelist of partition names to flash"),
        new CliParam("exclude", "string", "comma separated partition names to skip"),
        new CliParam("allow-redline", "boolean", "permit writing oeminfo/nvme/modem_secure/persist/modemnvm*/certification/hisee_encos/veritykey"),
        new CliParam("work-dir", "string", "staging directory for extracted images (default: <dir of first file>/.kirintool-cli-tmp)"),
        new CliParam("strict-names", "boolean", "refuse instead of skipping partitions the loader does not expose"),
        new CliParam("yes", "boolean", "acknowledge the plan and perform the write"),
        new CliParam("json", "boolean", "structured output"),
    };

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
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

        if (context.Json && !context.Yes)
            context.WriteJson(new { command = Name, dryRun = true, applied = false, gate = "plan-only", plan = details, exitCode = ExitCodes.Gated });
        else
        {
            context.Output.WriteLine($"flash plan: {plan.Entries.Count} partitions, {TextFormat.Bytes(plan.TotalBytes)}");
            context.Output.WriteLine(TextFormat.Table(rows, "PARTITION", "SIZE", "SOURCE"));
            foreach (string warning in plan.Warnings) context.Output.WriteLine($"warning: {warning}");
            context.Output.WriteLine($"skipped by policy: {plan.Skipped.Count} entr{(plan.Skipped.Count == 1 ? "y" : "ies")}");
        }

        bool allowRedline = args.HasFlag("allow-redline");
        if (plan.Redlines.Count > 0 && !allowRedline)
        {
            context.Diag($"refused: plan writes redline partitions ({string.Join(", ", plan.Redlines)}); pass --allow-redline to override");
            return ExitCodes.Gated;
        }

        bool confirmed = context.Yes || args.HasFlag("yes");
        if (!confirmed)
        {
            context.Diag("gate: plan only; re-run with --yes to flash");
            return ExitCodes.Gated;
        }

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

        string workDirectory = args.Value("work-dir") is { Length: > 0 } workDirArg
            ? Path.GetFullPath(workDirArg, context.WorkingDirectory)
            : Path.Combine(Path.GetDirectoryName(paths[0]) ?? context.WorkingDirectory, ".kirintool-cli-tmp");
        try { Directory.CreateDirectory(workDirectory); }
        catch (Exception ex)
        {
            context.Diag($"error: cannot create staging directory {workDirectory}: {ex.Message}");
            return ExitCodes.FileOrVerify;
        }
        context.Diag($"staging directory: {workDirectory}");

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

        if (context.Json)
            context.WriteJson(new
            {
                command = Name,
                dryRun = false,
                applied = true,
                fastboot = session.ExecutablePath,
                flashed = ok,
                total = outcomes.Count,
                failed,
                exitCode = ok == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected,
            });
        else
            context.Output.WriteLine($"flashed {ok}/{outcomes.Count} partitions");

        if (ok == 0) return ExitCodes.FileOrVerify;
        return ok == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected;
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
        "  identifiers whose images are missing abort before any write; without --yes only the plan is printed (exit 4)";
    public bool IsWrite => true;
    public IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("path", "string", "flashing XML file, or a directory containing one", Required: true, Positional: true),
        new CliParam("partitions", "string", "comma separated whitelist of identifiers to flash"),
        new CliParam("exclude", "string", "comma separated identifiers to skip"),
        new CliParam("allow-redline", "boolean", "permit writing oeminfo/nvme/modem_secure/persist/modemnvm*/certification/hisee_encos/veritykey"),
        new CliParam("yes", "boolean", "acknowledge the plan and perform the write"),
        new CliParam("json", "boolean", "structured output"),
    };

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
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
            if (onlySet != null && !onlySet.Contains(identifier)) { skipped.Add(identifier); continue; }
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

        if (context.Json && !context.Yes)
            context.WriteJson(new
            {
                command = Name,
                dryRun = true,
                applied = false,
                xml = path,
                partitionCount = selected.Count,
                skipped,
                redlines,
                missingImages = missing,
                partitions = selected.Select(partition => new
                {
                    identifier = partition.Identifier,
                    name = partition.Name,
                    image = partition.DumpPath,
                    redline = FlashPolicy.IsRedline(partition.Identifier ?? string.Empty),
                }).ToList(),
                exitCode = ExitCodes.Gated,
            });
        else
        {
            context.Output.WriteLine($"flash plan for {path}: {selected.Count} images");
            context.Output.WriteLine(TextFormat.Table(selected.Select(partition => new[]
            {
                partition.Identifier ?? "?",
                partition.Name ?? "?",
                FlashPolicy.IsRedline(partition.Identifier ?? string.Empty) ? "REDLINE" : "ok",
                partition.DumpPath ?? "(unresolved)",
            }).ToList(), "IDENTIFIER", "NAME", "STATE", "PATH"));
            if (skipped.Count > 0) context.Output.WriteLine($"skipped: {string.Join(", ", skipped)}");
        }

        if (missing.Count > 0)
        {
            context.Diag($"refused: {missing.Count} image(s) missing: {string.Join(", ", missing)}");
            return ExitCodes.FileOrVerify;
        }
        if (redlines.Count > 0 && !allowRedline)
        {
            context.Diag($"refused: plan writes redline identifiers ({string.Join(", ", redlines)}); pass --allow-redline to override");
            return ExitCodes.Gated;
        }

        bool confirmed = context.Yes || args.HasFlag("yes");
        if (!confirmed)
        {
            context.Diag("gate: plan only; re-run with --yes to flash");
            return ExitCodes.Gated;
        }

        FastbootSession? session = await FastbootSession.TryOpenAsync();
        if (session == null) { context.Diag("error: fastboot binary not found"); return ExitCodes.NoDevice; }
        if (!session.Connected) { context.Diag("error: no device in fastboot mode"); return ExitCodes.NoDevice; }

        var outcomes = new List<OtaFlashOutcome>();
        for (int i = 0; i < selected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FastbootPartition partition = selected[i];
            // Huawei loaders match partition names case-sensitively; GPT names are lower case.
            string flashName = (partition.Identifier ?? string.Empty).ToLowerInvariant();
            context.Diag($"[{i + 1}/{selected.Count}] flash {flashName} <- {TextFormat.Bytes(new FileInfo(partition.DumpPath!).Length)}");
            ProcessResult result = await session.Client.FlashPartition(flashName, partition.DumpPath!);
            bool ok = result.IsSuccess && (result.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);
            outcomes.Add(new OtaFlashOutcome(flashName, ok, ok ? null : (result.Output ?? string.Empty).Trim()));
            context.Diag(ok ? $"    OKAY {flashName}" : $"    FAILED {flashName}: {result.Output}");
        }

        int okCount = outcomes.Count(outcome => outcome.Ok);
        if (context.Json)
            context.WriteJson(new
            {
                command = Name,
                dryRun = false,
                applied = true,
                fastboot = session.ExecutablePath,
                flashed = okCount,
                total = outcomes.Count,
                failed = outcomes.Where(outcome => !outcome.Ok).Select(outcome => new { partition = outcome.Partition, error = outcome.Error }).ToList(),
                exitCode = okCount == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected,
            });
        else
            context.Output.WriteLine($"flashed {okCount}/{outcomes.Count} images");

        if (okCount == 0) return ExitCodes.FileOrVerify;
        return okCount == outcomes.Count ? ExitCodes.Ok : ExitCodes.DeviceRejected;
    }
}

internal sealed class VcomUnlockCommand : GatedWriteCommand
{
    public override string Name => "vcom-unlock";
    public override string McpName => "vcom_unlock";
    public override string Summary => "Plan a VCOM/BootROM loader unlock for a chip (gate-only: never uploads in this build).";
    public override string Usage => "vcom-unlock --cpu <chip> [--yes] [--json]";
    public override IReadOnlyList<CliParam> Params => new[]
    {
        new CliParam("cpu", "string", "chipset loader family, e.g. hisi980, hisi990", Required: true, Enum: BundledLoaders.SupportedChips().ToList()),
    };

    protected override Task<WritePlan> BuildPlanAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        string? cpu = args.Value("cpu");
        if (string.IsNullOrWhiteSpace(cpu))
            throw new CliUsageException("vcom-unlock requires --cpu <chip> (supported: " + string.Join(", ", BundledLoaders.SupportedChips()) + ")");

        cpu = cpu.Trim().ToLowerInvariant();
        if (!FirmwareUnlocker.CpuAddresses.ContainsKey(cpu))
            throw new CliUsageException($"unsupported chip '{cpu}' (supported: {string.Join(", ", BundledLoaders.SupportedChips())})");

        var stages = BundledLoaders.Plan(cpu);
        string loaderDirectory = Path.Combine(BundledLoaders.Directory, cpu);

        var stageList = stages.Select(stage => new
        {
            stage = stage.Stage,
            file = stage.File,
            path = Path.Combine(loaderDirectory, stage.File),
            address = "0x" + stage.Address.ToString("X8"),
            pwn = stage.PwnFlag,
            exists = stage.Exists,
        }).ToList();

        var details = new
        {
            cpu,
            loaderDirectory,
            implementation = "Services/FirmwareUnlocker.UnlockFastboot + Services/VcomFlasher (not invoked in this build)",
            requiresVcomMode = "HUAWEI USB COM 1.0 (12d1:3609)",
            stageCount = stageList.Count,
            missingStages = stageList.Count(stage => !stage.exists),
            stages = stageList,
        };

        var lines = new List<string>
        {
            $"vcom unlock plan for {cpu}",
            $"loader directory: {loaderDirectory}",
            "would upload, in order (device must already be in VCOM mode):",
            TextFormat.Table(stageList.Select(stage => new[]
            {
                stage.stage, stage.address, stage.file, stage.exists ? "present" : "MISSING",
            }).ToList(), "STAGE", "ADDRESS", "LOADER", "FILE"),
        };
        if (stageList.Any(stage => !stage.exists))
            lines.Add("warning: missing loader files would be skipped by the current service.");

        return Task.FromResult(new WritePlan(details, lines));
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
