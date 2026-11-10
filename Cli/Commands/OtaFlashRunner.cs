// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System.IO.Compression;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Cli.Commands;

/// <summary>
/// One partition that the CLI will extract and flash. Kept separate from
/// <see cref="PartitionInfo"/> so merged-super entries can carry a synthetic
/// source path (the merged image) instead of an UPDATE.APP offset.
/// </summary>
internal sealed class OtaPlanEntry
{
    public required string Partition { get; init; }
    public required long Size { get; init; }
    public required string Source { get; init; }
    public required string SourceFile { get; init; }
    public required long DataOffset { get; init; }
    public bool IsMergedSuper { get; init; }
    public bool IsRedline { get; init; }
}

internal sealed class OtaPlan
{
    public required List<OtaPlanEntry> Entries { get; init; }
    public required List<OtaPlanEntry> SuperParts { get; init; }
    public required List<OtaPlanEntry> Skipped { get; init; }
    public required List<string> Redlines { get; init; }
    public required List<string> Warnings { get; init; }
    public required long TotalBytes { get; init; }
}

internal sealed record OtaFlashOutcome(string Partition, bool Ok, string? Error);

/// <summary>
/// Mirror of the GUI's Full OTA policy (MainWindow.IsSkipPartition) plus a hard
/// redline list that the GUI only enforces through "Edit". The CLI never writes a
/// redline partition unless the caller passes --allow-redline explicitly.
/// </summary>
internal static class FlashPolicy
{
    // Same contains-list the GUI uses when building its Full OTA partition list.
    private static readonly string[] SkipContains =
    {
        "sha256rsa", "crc", "curver", "verlist", "package_type",
        "base_verlist", "base_ver", "ptable_cust", "cust_verlist",
        "cust_ver", "preload_verlist", "preload_ver", "ptable_preload",
    };

    // Device-keyed data. Writing any of these is unrecoverable without a backup.
    private static readonly string[] Redline =
    {
        "oeminfo", "nvme", "modem_secure", "persist", "modemnvm",
        "certification", "hisee_encos", "veritykey",
    };

    public static bool ShouldSkip(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName)) return true;
        string lower = partitionName.ToLowerInvariant();
        return SkipContains.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }

    public static bool IsRedline(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName)) return false;
        string lower = partitionName.ToLowerInvariant();
        return Redline.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }
}

/// <summary>Headless equivalent of the GUI's FlashFullOtaPartitions pipeline.</summary>
internal static class OtaFlashRunner
{
    /// <summary>
    /// UPDATE.APP entry names are not fastboot partition names: the archive carries
    /// HISIUFS_GPT (the GPT blob) while the loader exposes it as "ptable", and the loader
    /// matches names case-sensitively (lower case). Mirrors Utils/UpdateApp.GetPartitionName.
    /// </summary>
    private static readonly Dictionary<string, string> NameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hisiufs_gpt"] = "ptable",
        ["efi"] = "ptable",
        ["ufsfw"] = "ufs_fw",
        ["erecovery_ramdis"] = "erecovery_ramdisk",
        ["recovery_ramdis"] = "recovery_ramdisk",
        ["vbmeta_hw_produc"] = "vbmeta_hw_product",
    };

    /// <summary>
    /// Resolve every planned partition against the loader's own partition list, so a bad
    /// name fails before anything is written. Unresolved names are reported, not guessed.
    /// </summary>
    public static async Task<(Dictionary<string, string> Resolved, List<string> Unresolved)> ResolveNamesAsync(
        FastbootClient client,
        IEnumerable<string> plannedNames,
        CliContext context,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        HashSet<string>? available = null;
        try
        {
            ProcessResult result = await client.QueryVariableAsync("ptable");
            List<string> names = FastbootReadResponses.Partitions(result);
            if (names.Count > 0)
                available = new HashSet<string>(names.Select(name => name.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            context.Diag($"warning: cannot read the partition list from the device ({ex.Message}); falling back to lower-cased names");
        }

        foreach (string planned in plannedNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string lower = planned.Trim().ToLowerInvariant();
            string candidate = NameAliases.TryGetValue(lower, out string? alias) ? alias : lower;

            if (available == null)
            {
                resolved[planned] = candidate;
                continue;
            }

            if (available.Contains(candidate))
            {
                resolved[planned] = candidate;
                continue;
            }

            // Some packages name the GPT entry directly; try the alias in reverse.
            if (available.Contains(lower))
            {
                resolved[planned] = lower;
                continue;
            }

            unresolved.Add($"{planned} -> {candidate}");
        }

        return (resolved, unresolved);
    }

    public static OtaPlan BuildPlan(
        IReadOnlyList<string> files,
        IReadOnlyCollection<string>? onlyPartitions,
        IReadOnlyCollection<string>? excludedPartitions,
        CancellationToken cancellationToken)
    {
        var entries = new List<OtaPlanEntry>();
        var skipped = new List<OtaPlanEntry>();
        var redlines = new List<string>();
        var warnings = new List<string>();

        var only = onlyPartitions?.Count > 0
            ? new HashSet<string>(onlyPartitions, StringComparer.OrdinalIgnoreCase)
            : null;
        var excluded = excludedPartitions?.Count > 0
            ? new HashSet<string>(excludedPartitions, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<ExtractionEntry> read;
            try
            {
                read = PartitionExtractor.Read(file, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new CliFileException($"cannot read {file}: {ex.Message}");
            }

            if (read.Count == 0)
                throw new CliFileException($"no partitions found in {file}");

            string source = Path.GetFileName(file);
            foreach (ExtractionEntry entry in read)
            {
                PartitionInfo partition = entry.Partition;
                var candidate = new OtaPlanEntry
                {
                    Partition = partition.Name,
                    Size = partition.Size,
                    Source = source,
                    SourceFile = partition.UpdateAppFilePath ?? file,
                    DataOffset = partition.DataOffset,
                    IsRedline = FlashPolicy.IsRedline(partition.Name),
                };

                if (FlashPolicy.ShouldSkip(partition.Name))
                {
                    skipped.Add(candidate);
                    continue;
                }

                if (excluded != null && (excluded.Contains(partition.Name) || excluded.Contains(candidate.Source)))
                {
                    skipped.Add(candidate);
                    continue;
                }

                if (only != null && !only.Contains(partition.Name))
                {
                    skipped.Add(candidate);
                    continue;
                }

                if (candidate.IsRedline) redlines.Add(partition.Name);
                entries.Add(candidate);
            }
        }

        // The GUI merges every "super" fragment into one image before flashing;
        // UPDATE.APP packs super as several pieces.
        List<OtaPlanEntry> supers = entries.Where(e => e.Partition.Equals("super", StringComparison.OrdinalIgnoreCase)).ToList();
        if (supers.Count > 1)
        {
            int first = entries.IndexOf(supers[0]);
            entries.RemoveAll(e => e.Partition.Equals("super", StringComparison.OrdinalIgnoreCase));
            entries.Insert(first, new OtaPlanEntry
            {
                Partition = "super",
                Size = supers.Sum(s => s.Size),
                Source = "Merged super",
                SourceFile = supers[0].SourceFile,
                DataOffset = 0,
                IsMergedSuper = true,
            });
            warnings.Add($"super is split across {supers.Count} entries; they will be merged before flashing.");
        }

        return new OtaPlan
        {
            Entries = entries,
            SuperParts = supers,
            Skipped = skipped,
            Redlines = redlines,
            Warnings = warnings,
            TotalBytes = entries.Sum(e => e.Size),
        };
    }

    public static async Task<List<OtaFlashOutcome>> ExecuteAsync(
        OtaPlan plan,
        string workDirectory,
        IReadOnlyDictionary<string, string> resolvedNames,
        FastbootClient client,
        CliContext context,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<OtaFlashOutcome>();
        // A merged retail super image is ~6.5 GB; /tmp is tmpfs on many distros, so the
        // caller decides where the staging directory lives (default: next to the payload).
        string tempDirectory = Path.Combine(workDirectory, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            string? mergedSuperPath = null;
            List<OtaPlanEntry> supers = plan.SuperParts;
            OtaPlanEntry? mergedEntry = plan.Entries.FirstOrDefault(e => e.IsMergedSuper);
            if (mergedEntry != null && supers.Count > 1)
            {
                var partPaths = new List<string>();
                for (int i = 0; i < supers.Count; i++)
                {
                    string partPath = Path.Combine(tempDirectory, $"super_part_{i}.img");
                    context.Diag($"[super] extracting part {i + 1}/{supers.Count} from {supers[i].Source} ...");
                    await ExtractAsync(supers[i], partPath, cancellationToken);
                    partPaths.Add(partPath);
                }

                string current = partPaths[0];
                for (int i = 1; i < partPaths.Count; i++)
                {
                    string output = i == partPaths.Count - 1
                        ? Path.Combine(tempDirectory, "super_merged.img")
                        : Path.Combine(tempDirectory, $"super_intermediate_{i}.img");
                    context.Diag($"[super] merging part {i + 1}/{partPaths.Count} ...");
                    await SuperMerger.MergeSuperImages(current, partPaths[i], output, _ => { });
                    current = output;
                }
                mergedSuperPath = current;
                context.Diag($"[super] merged {TextFormat.Bytes(new FileInfo(mergedSuperPath).Length)}");
            }

            for (int i = 0; i < plan.Entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                OtaPlanEntry entry = plan.Entries[i];
                string? payloadPath = null;
                try
                {
                    if (entry.IsMergedSuper)
                    {
                        if (mergedSuperPath == null) throw new InvalidOperationException("merged super image missing");
                        payloadPath = mergedSuperPath;
                    }
                    else
                    {
                        payloadPath = Path.Combine(tempDirectory, $"{i:D2}_{entry.Partition}.img");
                        await ExtractAsync(entry, payloadPath, cancellationToken);
                    }

                    payloadPath = await DecompressIfGzipAsync(payloadPath, tempDirectory, context, cancellationToken);

                    long bytes = new FileInfo(payloadPath).Length;
                    // Huawei loaders resolve partitions case-sensitively: "BOOT" is answered with
                    // "UltraFlash refused (remote: 'partition not exist')" and the fallback then dies
                    // with "partition length get error". UPDATE.APP stores names in upper case, the
                    // GPT stores them in lower case, so always flash the lower-case form.
                    string flashName = resolvedNames.TryGetValue(entry.Partition, out string? deviceName)
                        ? deviceName
                        : entry.Partition.ToLowerInvariant();
                    context.Diag($"[{i + 1}/{plan.Entries.Count}] flash {flashName} <- {TextFormat.Bytes(bytes)}"
                        + (string.Equals(flashName, entry.Partition, StringComparison.OrdinalIgnoreCase) ? "" : $" (from {entry.Partition})"));
                    ProcessResult result = await client.FlashPartition(flashName, payloadPath);
                    bool ok = result.IsSuccess && (result.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);
                    outcomes.Add(new OtaFlashOutcome(flashName, ok, ok ? null : Trim(result.Output)));
                    context.Diag(ok ? $"    OKAY {entry.Partition}" : $"    FAILED {entry.Partition}: {Trim(result.Output)}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    outcomes.Add(new OtaFlashOutcome(entry.Partition.ToLowerInvariant(), false, ex.Message));
                    context.Diag($"    FAILED {entry.Partition.ToLowerInvariant()}: {ex.Message}");
                }
                finally
                {
                    if (payloadPath != null && File.Exists(payloadPath) && payloadPath != mergedSuperPath)
                    {
                        try { File.Delete(payloadPath); } catch { }
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDirectory, true); } catch { }
        }

        return outcomes;
    }

    /// <summary>
    /// The GUI copies partition bytes out of UPDATE.APP verbatim; BD-style packages
    /// store gzip-compressed payloads that then fail mid-flash. Decompress here
    /// instead, and say so on stderr.
    /// </summary>
    private static async Task<string> DecompressIfGzipAsync(string path, string tempDirectory, CliContext context, CancellationToken cancellationToken)
    {
        byte[] magic = new byte[2];
        await using (var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = await probe.ReadAsync(magic.AsMemory(0, 2), cancellationToken);
            if (read < 2) return path;
        }

        if (magic[0] != 0x1f || magic[1] != 0x8b) return path;

        string output = Path.Combine(tempDirectory, Path.GetFileNameWithoutExtension(path) + ".raw");
        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        await using (var outputStream = new FileStream(output, FileMode.Create, FileAccess.Write))
        {
            await gzip.CopyToAsync(outputStream, 1 << 20, cancellationToken);
        }
        context.Diag($"    gzip payload decompressed -> {TextFormat.Bytes(new FileInfo(output).Length)}");
        try { File.Delete(path); } catch { }
        return output;
    }

    private static async Task ExtractAsync(OtaPlanEntry entry, string outputPath, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(entry.SourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        input.Seek(entry.DataOffset, SeekOrigin.Begin);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        byte[] buffer = new byte[1 << 20];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await input.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read <= 0) throw new InvalidDataException("unexpected end of file while reading " + entry.Partition);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(no output)";
        string single = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return single.Length <= 300 ? single : single[..300] + "...";
    }
}
