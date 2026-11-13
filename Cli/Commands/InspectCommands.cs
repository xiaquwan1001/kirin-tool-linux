using Kirin_Tool.Services;

namespace Kirin_Tool.Cli.Commands;

// Read-only inspection commands added by the parity lane (see docs/parity-design.md §4.1).
// They exist because the GUI already had these read-only capabilities with no CLI/MCP face
// (psid, udev coverage) or only had them buried inside a write flow (partition table).
//
// Rules kept from the rest of the CLI:
//   * no business logic here - every command calls the same service the GUI calls;
//   * read-only means "cannot touch device state": no --yes, no --allow-redline, no confirm.

/// <summary>Device partition table (the read step the GUI does before a dump/flash).</summary>
internal sealed class PartitionsCommand : ICliCommand
{
    public string Name => "partitions";
    public string McpName => "partitions";
    public string Summary => "List the connected device's partition table (getvar ptable; read-only).";
    public string Usage =>
        "partitions [--json]\n" +
        "  same service as the GUI partition selector (Services/FastbootFlasherService.GetPartitionTableAsync)\n" +
        "  needs an in-device bootloader that answers 'getvar ptable'";
    public bool IsWrite => false;
    public IReadOnlyList<CliParam> Params => Array.Empty<CliParam>();

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        var session = await FastbootSession.TryOpenAsync();
        if (session == null)
        {
            context.Diag("error: fastboot binary not found; set KIRIN_FASTBOOT or install platform-tools");
            return ExitCodes.FileOrVerify;
        }
        if (!session.Connected)
        {
            context.Diag("error: no fastboot device detected");
            return ExitCodes.NoDevice;
        }

        List<Kirin_Tool.Models.FastbootPartition> partitions;
        try
        {
            partitions = await new FastbootFlasherService(session.Client).GetPartitionTableAsync();
        }
        catch (TimeoutException)
        {
            context.Diag("error: getvar ptable timed out");
            return ExitCodes.DeviceRejected;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (partitions.Count == 0)
        {
            context.Diag("error: the connected bootloader did not return a partition table (getvar ptable unsupported)");
            if (context.Json)
            {
                context.WriteJson(new { count = 0, partitions = Array.Empty<object>(), exitCode = ExitCodes.DeviceRejected });
            }
            return ExitCodes.DeviceRejected;
        }

        if (context.Json)
        {
            context.WriteJson(new
            {
                count = partitions.Count,
                partitions = partitions.Select(partition => new { name = partition.Name, identifier = partition.Identifier }).ToList(),
            });
        }
        else
        {
            context.Output.WriteLine(TextFormat.Table(
                partitions.Select(partition => new[] { partition.Name, partition.Identifier }).ToList(),
                "NAME", "IDENTIFIER"));
            context.Output.WriteLine();
            context.Output.WriteLine($"{partitions.Count} partitions");
        }

        return ExitCodes.Ok;
    }
}

/// <summary>PSID / device identifiers via 'oem get-psid' (the GUI's "Read identifiers" button).</summary>
internal sealed class PsidCommand : ICliCommand
{
    public string Name => "psid";
    public string McpName => "psid";
    public string Summary => "Read the device identifiers (oem get-psid; read-only).";
    public string Usage => "psid [--json]";
    public bool IsWrite => false;
    public IReadOnlyList<CliParam> Params => Array.Empty<CliParam>();

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        var session = await FastbootSession.TryOpenAsync();
        if (session == null)
        {
            context.Diag("error: fastboot binary not found; set KIRIN_FASTBOOT or install platform-tools");
            return ExitCodes.FileOrVerify;
        }
        if (!session.Connected)
        {
            context.Diag("error: no fastboot device detected");
            return ExitCodes.NoDevice;
        }

        IReadOnlyDictionary<string, string> values;
        try
        {
            values = await session.Client.ReadPsidAsync();
        }
        catch (TimeoutException)
        {
            context.Diag("error: oem get-psid timed out");
            return ExitCodes.DeviceRejected;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            context.Diag($"error: cannot parse 'oem get-psid' output: {ex.Message}");
            return ExitCodes.DeviceRejected;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (values.Count == 0)
        {
            context.Diag("error: 'oem get-psid' returned no identifiers (unsupported or rejected by the bootloader)");
            if (context.Json)
            {
                context.WriteJson(new { count = 0, identifiers = new Dictionary<string, string>(), exitCode = ExitCodes.DeviceRejected });
            }
            return ExitCodes.DeviceRejected;
        }

        if (context.Json)
        {
            context.WriteJson(new { count = values.Count, identifiers = values });
        }
        else
        {
            foreach (var pair in values) context.Output.WriteLine($"{pair.Key}: {pair.Value}");
        }
        return ExitCodes.Ok;
    }
}

/// <summary>
/// Chip capability matrix - the same table the GUI consults through
/// MainWindow.AllowChipOperationAsync (Services/ChipCapabilities.cs), plus the bundled loader
/// inventory and the rescue partition plan. No device needed (read-only).
/// </summary>
internal sealed class CapabilitiesCommand : ICliCommand
{
    private static readonly string[] Operations =
    [
        "read-info", "read-sn", "read-psid", "dump-partitions", "reboot-usb-update",
        "flash-xml", "flash-ota", "vcom-unlock", "write-sn", "frp-remove", "unlock-bootloader",
    ];

    public string Name => "capabilities";
    public string McpName => "capabilities";
    public string Summary => "Show the per-chip capability matrix, bundled loaders and the rescue partition plan (read-only, no device).";
    public string Usage => "capabilities [--json]";
    public bool IsWrite => false;
    public IReadOnlyList<CliParam> Params => Array.Empty<CliParam>();

    public async Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        var chips = BundledLoaders.SupportedChips();
        var loaders = BundledLoaders.Inventory();

        var matrix = chips.ToDictionary(
            chip => chip,
            chip => Operations.ToDictionary(operation => operation, operation => ChipCapabilities.Allows(chip, operation)),
            StringComparer.Ordinal);

        var rescue = chips.ToDictionary(
            chip => chip,
            chip => RescuePartitionPlan.ForChip(chip)
                .Select(entry => new { image = entry.Image, target = entry.Target })
                .ToList(),
            StringComparer.Ordinal);

        var restrictedExample = ChipCapabilities.Kirin9000;

        if (context.Json)
        {
            context.WriteJson(new
            {
                operations = Operations,
                restrictedChip = restrictedExample,
                chips = chips.Select(chip => new
                {
                    chip,
                    loaders = loaders.FirstOrDefault(entry => entry.Chip == chip).Files,
                    allowed = matrix[chip].Where(pair => pair.Value).Select(pair => pair.Key).ToList(),
                    refused = matrix[chip].Where(pair => !pair.Value).Select(pair => pair.Key).ToList(),
                }).ToList(),
                rescuePlan = rescue,
            });
        }
        else
        {
            context.Output.WriteLine($"capability matrix (chip {restrictedExample} is the restricted model: {ChipCapabilities.UnsupportedNotice})");
            foreach (string chip in chips)
            {
                var refused = matrix[chip].Where(pair => !pair.Value).Select(pair => pair.Key).ToList();
                int loadersCount = loaders.FirstOrDefault(entry => entry.Chip == chip).Files;
                context.Output.WriteLine($"  {chip,-10} loaders={loadersCount,-3} {(refused.Count == 0 ? "all operations allowed" : "refused: " + string.Join(", ", refused))}");
                foreach (var entry in rescue[chip])
                    context.Output.WriteLine($"       rescue: {entry.image} -> {entry.target}");
            }
            context.Output.WriteLine();
            context.Output.WriteLine("operations: " + string.Join(", ", Operations));
        }

        return ExitCodes.Ok;
    }
}

/// <summary>
/// Host udev rule coverage (the GUI's "Check again" button): the same read-only inspection the
/// GUI uses to decide whether to show the permission banner. Never runs udevadm, never touches USB.
/// </summary>
internal sealed class UdevCommand : ICliCommand
{
    public string Name => "udev";
    public string McpName => "udev";
    public string Summary => "Check host udev rule coverage for Huawei (12d1) USB/serial access (read-only, no device).";
    public string Usage => "udev [--json]\n  exit 0 when both usb and tty rules are covered, 5 otherwise (the fix commands are printed)";
    public bool IsWrite => false;
    public IReadOnlyList<CliParam> Params => Array.Empty<CliParam>();

    public Task<int> ExecuteAsync(ParsedArgs args, CliContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UdevRuleCoverage coverage = UdevRuleNotice.Inspect();

        if (context.Json)
        {
            context.WriteJson(new
            {
                usb = coverage.Usb,
                serial = coverage.Serial,
                readError = coverage.ReadError,
                complete = coverage.Complete,
                appImage = UdevRuleNotice.IsAppImage,
                directories = UdevRuleNotice.RuleDirectories,
                applyCommands = coverage.Complete ? null : UdevRuleNotice.ApplyCommands,
                exitCode = coverage.Complete ? ExitCodes.Ok : ExitCodes.FileOrVerify,
            });
        }
        else
        {
            context.Output.WriteLine($"usb rule (12d1 usb_device): {(coverage.Usb ? "covered" : "MISSING")}");
            context.Output.WriteLine($"serial rule (12d1 tty):      {(coverage.Serial ? "covered" : "MISSING")}");
            if (coverage.ReadError) context.Output.WriteLine("note: at least one rule directory could not be read (permissions)");
            if (!coverage.Complete)
            {
                context.Output.WriteLine();
                context.Output.WriteLine("run as root to install the vendor-wide rules:");
                context.Output.WriteLine(UdevRuleNotice.ApplyCommands);
            }
        }

        if (coverage.Complete) return Task.FromResult(ExitCodes.Ok);
        context.Diag("error: udev rules incomplete - fastboot/adb may need root or a manual udev rule");
        return Task.FromResult(ExitCodes.FileOrVerify);
    }
}
