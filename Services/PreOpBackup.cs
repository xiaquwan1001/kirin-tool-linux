// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services;

/// <summary>Per-partition backup outcome recorded in the manifest.</summary>
public sealed record PreOpBackupPartitionResult(
    string Name,
    string? File,
    long? Size,
    string? Sha256,
    string Status,
    string? Reason);

/// <summary>
/// Outcome of one pre-op backup run. <see cref="Attempted"/> is false only when the
/// backup directory could not be created or the caller disabled the feature; in that
/// case no directory exists and nothing was dumped.
/// </summary>
public sealed record PreOpBackupResult(
    bool Attempted,
    bool Complete,
    string? Directory,
    string? ManifestPath,
    IReadOnlyList<PreOpBackupPartitionResult> Partitions,
    string? Error)
{
    public static readonly PreOpBackupResult Skipped =
        new(false, false, null, null, Array.Empty<PreOpBackupPartitionResult>(), "disabled");

    public int OkCount => Partitions.Count(p => p.Status == "ok");
    public int TotalCount => Partitions.Count;
}

/// <summary>Options for one pre-op backup run.</summary>
public sealed class PreOpBackupOptions
{
    /// <summary>Which command triggered the backup (flash-ota, flash-xml, gui-full-ota, gui-xml, gui-rescue, gui-usb-update, backup).</summary>
    public string Trigger { get; set; } = "unknown";

    /// <summary>Partition names from the write plan; redline members plus "ptable" form the backup set.</summary>
    public IReadOnlyList<string> WritePartitions { get; set; } = Array.Empty<string>();

    /// <summary>Explicit --backup-dir override; null falls back to $KIRIN_BACKUP_DIR then the user data directory.</summary>
    public string? BackupDir { get; set; }

    /// <summary>Strict mode: callers abort the write when the backup is incomplete.</summary>
    public bool RequireBackup { get; set; }

    /// <summary>Fastboot executable recorded in the manifest (informational).</summary>
    public string? FastbootPath { get; set; }

    /// <summary>
    /// Wall-clock budget for the whole backup (default 60 s). A slow or unresponsive
    /// loader must never stall a write for minutes: whatever is left is recorded as
    /// status "skipped-timeout" and the caller continues.
    /// </summary>
    public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per-partition dump timeout (default 10 s). A loader that answers neither verb can
    /// otherwise hold the write path for minutes, because DumpPartitionAsync has its own
    /// generous timeout; the backup is best effort and must yield quickly.
    /// </summary>
    public TimeSpan PerPartitionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Back up exactly the requested names instead of filtering to the redline + ptable
    /// set. Used by the standalone 'backup' command where the caller picked the list.
    /// </summary>
    public bool KeepAllPartitions { get; set; }
}

/// <summary>
/// Best-effort automatic backup of device identity partitions (redline set + ptable)
/// before any real write. Never throws for dump failures: per-partition problems are
/// recorded as status "unsupported"/"failed" with a reason and the caller decides
/// whether to continue (default) or abort (--require-backup).
/// </summary>
public sealed class PreOpBackup
{
    /// <summary>Default identity set for the standalone 'backup' command (no write plan available).</summary>
    public static readonly IReadOnlyList<string> DefaultIdentitySet = new[]
    {
        "ptable", "oeminfo", "nvm", "modemst1", "modemst2", "fsg",
        "persist", "modemnvm_1", "modemnvm_2",
    };

    private const string ManifestFileName = "manifest.json";

    private readonly FastbootFlasherService? _flasher;
    private readonly Func<string, string, Task<(bool Success, string Message)>>? _dumpAsync;
    private readonly Func<string, Task<string?>>? _getVarAsync;

    /// <summary>Production constructor: dumps via <see cref="FastbootFlasherService.DumpPartitionAsync"/>.</summary>
    public PreOpBackup(FastbootFlasherService flasher, Func<string, Task<string?>>? getVarAsync = null)
    {
        _flasher = flasher ?? throw new ArgumentNullException(nameof(flasher));
        _getVarAsync = getVarAsync;
    }

    /// <summary>Test/diagnostics constructor with an injectable dump delegate: (partitionName, savePath) → result.</summary>
    public PreOpBackup(Func<string, string, Task<(bool Success, string Message)>> dumpAsync)
    {
        _dumpAsync = dumpAsync ?? throw new ArgumentNullException(nameof(dumpAsync));
    }

    /// <summary>
    /// Backup set for a write plan: redline members of the plan (deduplicated,
    /// case-insensitive) plus "ptable". "ptable" is always included — the GPT is the
    /// cheapest life insurance even when the plan contains no redline partitions.
    /// </summary>
    public static IReadOnlyList<string> BackupSet(IEnumerable<string> writePartitions)
    {
        var set = new List<string>(FlashPolicy.RedlineHits(writePartitions));
        if (!set.Contains("ptable", StringComparer.OrdinalIgnoreCase))
            set.Add("ptable");
        return set;
    }

    /// <summary>Base directory for timestamped backups: override → $KIRIN_BACKUP_DIR → user data dir.</summary>
    public static string ResolveBaseDirectory(string? overrideDir)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(overrideDir);

        string? env = Environment.GetEnvironmentVariable("KIRIN_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            return Path.GetFullPath(env);

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "kirin-tool", "backups");
    }

    /// <summary>
    /// Creates yyyyMMdd-HHmmss under <paramref name="baseDirectory"/>; when the name is
    /// taken (same-second concurrency) a -2, -3, … suffix is appended. An existing
    /// backup directory is NEVER overwritten or reused.
    /// </summary>
    public static string CreateTimestampDirectory(string baseDirectory, DateTime? utcNow = null)
    {
        Directory.CreateDirectory(baseDirectory);
        string stamp = (utcNow ?? DateTime.UtcNow).ToLocalTime().ToString("yyyyMMdd-HHmmss");
        string candidate = Path.Combine(baseDirectory, stamp);
        for (int suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(baseDirectory, $"{stamp}-{suffix}");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>Runs the backup. Never throws for dump failures; only a directory-creation failure sets <see cref="PreOpBackupResult.Error"/>.</summary>
    public async Task<PreOpBackupResult> RunAsync(PreOpBackupOptions options, CancellationToken cancellationToken = default)
    {
        // One op per backup run (docs/op-scope-map.md row 5): the directory, the identity read,
        // every partition dump and the manifest write are steps of this file.
        using var op = OpLog.Begin(OpNames.BackupPreOp, new[]
        {
            options.Trigger ?? "unknown",
            options.BackupDir ?? "(default)",
            options.RequireBackup ? "require-backup" : "best-effort",
        }, "pre-op backup of the redline set + ptable");
        op.Step("write plan: " + (options.WritePartitions?.Count ?? 0) + " partition(s), budget "
            + options.Budget.TotalSeconds.ToString("0") + "s, per-partition timeout "
            + options.PerPartitionTimeout.TotalSeconds.ToString("0") + "s");
        op.Step("backup set: " + string.Join(", ", PartitionList(options)));

        string backupDir;
        try
        {
            backupDir = CreateTimestampDirectory(ResolveBaseDirectory(options.BackupDir));
        }
        catch (Exception ex)
        {
            op.Result(false, null, null, null, ex.Message, OpLog.ExceptionVerdict(ex),
                "cannot create backup directory; nothing was dumped");
            return new PreOpBackupResult(false, false, null, null, Array.Empty<PreOpBackupPartitionResult>(),
                $"cannot create backup directory: {ex.Message}");
        }
        op.Step("backup directory: " + backupDir);

        if (!_deviceIdentityRead) await ReadDeviceIdentityAsync(op);
        op.Step("device identity: product=" + (DeviceProduct ?? "(unread)")
            + ", serial=" + (DeviceSerial ?? "(unread)"));

        var partitions = new List<PreOpBackupPartitionResult>();
        // Same partition, single dump per process: cache the result by name (case-insensitive).
        var cache = new Dictionary<string, PreOpBackupPartitionResult>(StringComparer.OrdinalIgnoreCase);

        var budget = Stopwatch.StartNew();
        int consecutiveFailures = 0;
        string? abortReason = null;
        foreach (string name in PartitionList(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cache.TryGetValue(name, out var cached))
            {
                op.Step(OpLog.SkippedPrefix + name + ": already dumped in this process (status " + cached.Status + ")");
                partitions.Add(cached);
                continue;
            }

            if (abortReason != null)
            {
                // A declared degradation, not a silent one: the partition is recorded as
                // unsupported and the reason is in the same step line.
                op.Step(OpLog.DegradedPrefix + name + ": unsupported, " + abortReason);
                partitions.Add(new PreOpBackupPartitionResult(name, null, null, null, "unsupported", abortReason));
                continue;
            }
            if (budget.Elapsed > options.Budget)
            {
                abortReason = $"backup budget of {options.Budget.TotalSeconds:0}s exhausted before this partition";
                op.Step(OpLog.SkippedPrefix + name + ": " + abortReason);
                partitions.Add(new PreOpBackupPartitionResult(name, null, null, null, "skipped-timeout", abortReason));
                continue;
            }

            var result = await DumpOneAsync(name, backupDir, options.PerPartitionTimeout, cancellationToken, op);
            cache[name] = result;
            partitions.Add(result);
            LogPartition(op, result);

            // A temporary VCOM loader answers both dump verbs with "invalid command" (or
            // returns OKAY without writing a file). After two consecutive failures there is
            // nothing to gain from more process spawns: stop and say why, so a write is not
            // delayed by a capability the loader does not have.
            consecutiveFailures = result.Status == "ok" ? 0 : consecutiveFailures + 1;
            if (consecutiveFailures >= 2)
                abortReason = "loader did not produce a dump (invalid command or empty file); remaining partitions skipped";
        }

        string? manifestPath = WriteManifest(options, backupDir, partitions, op);
        if (manifestPath != null)
            op.Step(OpLog.OkPrefix + "manifest: " + OpLog.FileStep(manifestPath));
        else
            op.Step(OpLog.FailedPrefix + "manifest not written: " + (_manifestError ?? "unknown reason"));

        bool complete = partitions.Count > 0 && partitions.All(p => p.Status == "ok");
        int okCount = partitions.Count(p => p.Status == "ok");
        string verdict = complete
            ? "partition-status:all-ok"
            : "partition-status:" + okCount + "/" + partitions.Count + "-ok";
        string reasons = string.Join("; ", partitions
            .Where(p => p.Status != "ok")
            .Select(p => p.Name + "=" + p.Status + (string.IsNullOrEmpty(p.Reason) ? "" : " (" + p.Reason + ")")));
        op.Result(complete, null, null, null, null, verdict,
            (complete ? "every partition dumped" : "incomplete backup: " + reasons)
            + (manifestPath == null ? "; manifest missing" : ""));
        return new PreOpBackupResult(true, complete, backupDir, manifestPath, partitions, null);
    }

    /// <summary>One step line per partition: status is the evidence, never just a boolean.</summary>
    private static void LogPartition(OperationScope op, PreOpBackupPartitionResult result)
    {
        switch (result.Status)
        {
            case "ok":
                op.Step(OpLog.OkPrefix + result.Name + ": " + (result.Size ?? 0) + " B, sha256="
                    + (result.Sha256 ?? "(unavailable)") + ", file=" + (result.File ?? "(none)"));
                break;
            case "unsupported":
                op.Step(OpLog.DegradedPrefix + result.Name + ": unsupported by this loader ("
                    + (result.Reason ?? "no reason given") + ")");
                break;
            case "skipped-timeout":
                op.Step(OpLog.SkippedPrefix + result.Name + ": " + (result.Reason ?? "budget exhausted"));
                break;
            default:
                op.Step(OpLog.FailedPrefix + result.Name + ": " + (result.Reason ?? "dump failed"));
                break;
        }
    }

    private static IReadOnlyList<string> PartitionList(PreOpBackupOptions options)
    {
        if (options.KeepAllPartitions)
        {
            // Keep the caller's order, drop blanks and case-insensitive duplicates.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return (options.WritePartitions ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Where(name => seen.Add(name))
                .ToList();
        }
        return BackupSet(options.WritePartitions);
    }

    private async Task<PreOpBackupPartitionResult> DumpOneAsync(string partition, string backupDir, TimeSpan perPartitionTimeout, CancellationToken cancellationToken, OperationScope? op = null)
    {
        string target = Path.Combine(backupDir, SanitizeFileName(partition) + ".img");
        try
        {
            Task<(bool Success, string Message)> dumpTask = _flasher != null
                ? _flasher.DumpPartitionAsync(partition, target, op)
                : _dumpAsync!(partition, target);
            // The dump cannot be cancelled from here (the fastboot child is owned by the
            // flasher), so abandon it on timeout and record the reason: the write path must
            // not wait on a capability the loader may not have.
            Task finished = await Task.WhenAny(dumpTask, Task.Delay(perPartitionTimeout, cancellationToken));
            if (finished != dumpTask)
            {
                op?.Step(OpLog.FailedPrefix + partition + ": dump timed out after "
                    + perPartitionTimeout.TotalSeconds.ToString("0") + "s (abandoned, write path continues)");
                TryDeletePartial(target, op);
                return new PreOpBackupPartitionResult(partition, null, null, null, "failed",
                    $"dump timed out after {perPartitionTimeout.TotalSeconds:0}s");
            }
            (bool success, string message) = await dumpTask;

            if (!success)
            {
                // Temporary VCOM loaders answer both dump verbs with "invalid command":
                // that is a loader capability gap, not a transient device failure.
                string status = message != null && message.Contains("invalid command", StringComparison.OrdinalIgnoreCase)
                    ? "unsupported"
                    : "failed";
                TryDeletePartial(target, op);
                return new PreOpBackupPartitionResult(partition, null, null, null, status, message ?? "dump failed");
            }

            long size = File.Exists(target) ? new FileInfo(target).Length : 0;
            if (size == 0)
            {
                op?.Step(OpLog.FailedPrefix + partition + ": dump produced an empty file (loader answered without writing)");
                TryDeletePartial(target, op);
                return new PreOpBackupPartitionResult(partition, null, 0, null, "failed", "dump produced an empty file");
            }

            string? sha256 = await ComputeSha256Async(target, cancellationToken, op);
            return new PreOpBackupPartitionResult(partition, target, size, sha256, "ok", null);
        }
        catch (OperationCanceledException)
        {
            op?.Step(OpLog.SkippedPrefix + partition + ": cancelled while dumping");
            TryDeletePartial(target, op);
            throw;
        }
        catch (Exception ex)
        {
            op?.Step(OpLog.FailedPrefix + partition + ": " + OpLog.ExceptionVerdict(ex) + ": " + ex.Message);
            TryDeletePartial(target, op);
            return new PreOpBackupPartitionResult(partition, null, null, null, "failed", ex.Message);
        }
    }

    private async Task<string?> ReadDeviceVarAsync(string variable, OperationScope? op = null)
    {
        if (_getVarAsync == null)
        {
            op?.Step(OpLog.DegradedPrefix + variable + ": no getvar reader wired into this backup instance");
            return null;
        }
        try
        {
            string? value = await _getVarAsync(variable);
            if (string.IsNullOrWhiteSpace(value))
                op?.Step(OpLog.DegradedPrefix + variable + ": loader returned an empty value");
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            // Identity is advisory for the manifest; the failure is now recorded, the run continues.
            op?.Step(OpLog.DegradedPrefix + variable + ": read failed (" + ex.Message + "); identity recorded as null");
            return null;
        }
    }

    private string? WriteManifest(PreOpBackupOptions options, string backupDir, IReadOnlyList<PreOpBackupPartitionResult> partitions, OperationScope? op = null)
    {
        _manifestError = null;
        try
        {
            var document = new
            {
                tool = new { name = ToolName(), version = ToolVersion() },
                timestampUtc = DateTime.UtcNow.ToString("o"),
                fastboot = options.FastbootPath,
                device = new { product = DeviceProduct, serialNumber = DeviceSerial },
                trigger = options.Trigger,
                writePlan = options.WritePartitions,
                partitions = partitions.Select(p => new
                {
                    name = p.Name,
                    file = p.File,
                    size = p.Size,
                    sha256 = p.Sha256,
                    status = p.Status,
                    reason = p.Reason,
                }).ToList(),
            };

            string finalPath = Path.Combine(backupDir, ManifestFileName);
            string tmpPath = finalPath + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                // Nulls (unreadable device identity, failed dumps) must stay explicit in the manifest.
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            // Atomic rename: an interrupted run leaves either no manifest or a complete one,
            // never a half-written manifest.json.
            File.Move(tmpPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch (Exception ex)
        {
            // A missing manifest must not erase the fact that dumps may have succeeded. The
            // error used to be swallowed entirely; it is now recorded on the step that reports it.
            _manifestError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // Best-effort device identity, read once per run and reused by the manifest.
    private string? DeviceProduct;
    private string? DeviceSerial;
    private bool _deviceIdentityRead;

    // Why the manifest could not be written (surfaced as a step line instead of an empty catch).
    private string? _manifestError;

    internal async Task ReadDeviceIdentityAsync(OperationScope? op = null)
    {
        _deviceIdentityRead = true;
        DeviceProduct = await ReadDeviceVarAsync("product", op);
        DeviceSerial = await ReadDeviceVarAsync("serialno", op);
    }

    private static void TryDeletePartial(string path, OperationScope? op = null)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            op?.Step(OpLog.DegradedPrefix + "partial dump left on disk: " + path + " (" + ex.Message + ")");
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static async Task<string?> ComputeSha256Async(string path, CancellationToken cancellationToken, OperationScope? op = null)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            // The dump itself is still on disk, so this is a degradation, not a failure.
            op?.Step(OpLog.DegradedPrefix + "sha256 unavailable for " + path + " (" + ex.Message + ")");
            return null;
        }
    }

    private static string ToolName() => typeof(PreOpBackup).Assembly.GetName().Name ?? "kirin-tool";

    private static string? ToolVersion() =>
        typeof(PreOpBackup).Assembly.GetName().Version?.ToString();
}
