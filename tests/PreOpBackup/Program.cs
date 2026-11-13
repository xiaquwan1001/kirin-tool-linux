// Hardware-free harness for the pre-op backup planner and manifest writer.
// Run by both CI pipelines via tests/suites.txt; any failed check throws and the
// process exits non-zero.

using Kirin_Tool.Services;

namespace Kirin_Tool.Tests.BackupPlan;

internal static class Program
{
    private static int _failures;

    public static int Main(string[] args)
    {
        // The whole harness runs inside one disposable scratch directory.
        string scratch = Directory.CreateTempSubdirectory("kirin-backup-plan-").FullName;
        string oldCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = scratch;
        try
        {
            TestBackupSetSelection();
            TestDirectoryNamingNeverOverwrites();
            TestManifestStructureAndStatuses();
            TestAllUnsupportedStillContinues();
            TestAtomicManifestRename();
            TestOperationLogCoverage(scratch);
        }
        finally
        {
            Environment.CurrentDirectory = oldCwd;
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }

        if (_failures > 0)
        {
            Console.Error.WriteLine($"BackupPlan: {_failures} check(s) FAILED");
            return 1;
        }
        Console.WriteLine("BackupPlan: all checks passed");
        return 0;
    }

    /// <summary>Every run of one op, oldest first. v2.4.15: one session file holds many ops,
    /// so runs are split at each phase=start line of that op rather than one file per run.</summary>
    private static List<List<System.Text.Json.JsonElement>> OpRuns(string logRoot, string op)
    {
        var runs = new List<(string Started, List<System.Text.Json.JsonElement> Lines)>();
        if (!Directory.Exists(logRoot)) return runs.Select(run => run.Lines).ToList();
        foreach (string file in Directory.EnumerateFiles(logRoot, "*.jsonl", SearchOption.AllDirectories))
        {
            List<System.Text.Json.JsonElement>? current = null;
            foreach (string raw in File.ReadAllLines(file))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                System.Text.Json.JsonElement element = System.Text.Json.JsonDocument.Parse(raw).RootElement.Clone();
                if (!element.TryGetProperty("op", out System.Text.Json.JsonElement name) || name.GetString() != op) continue;
                // The session's own start line repeats the first op name but carries no scopeId;
                // it is not a run of that op (v2.4.15 log merge).
                if (!element.TryGetProperty("scopeId", out _)) continue;
                string phase = element.TryGetProperty("phase", out System.Text.Json.JsonElement phaseElement)
                    ? (phaseElement.GetString() ?? string.Empty)
                    : string.Empty;
                if (phase == "start" || current == null)
                {
                    if (current is { Count: > 0 })
                        runs.Add((current[0].GetProperty("ts").GetString() + " " + Path.GetFileName(file), current));
                    current = new List<System.Text.Json.JsonElement>();
                }
                current.Add(element);
            }
            if (current is { Count: > 0 })
                runs.Add((current[0].GetProperty("ts").GetString() + " " + Path.GetFileName(file), current));
        }
        return runs.OrderBy(run => run.Started, StringComparer.Ordinal).Select(run => run.Lines).ToList();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL: {message}");
            throw new InvalidOperationException($"BackupPlan check failed: {message}");
        }
    }

    // (a) Backup set selection: redline members, dedup, ptable always present.
    private static void TestBackupSetSelection()
    {
        var set1 = PreOpBackup.BackupSet(new[] { "boot", "system", "vendor" });
        Check(set1.Count == 1 && string.Equals(set1[0], "ptable", StringComparison.OrdinalIgnoreCase),
            $"no-redline plan still backs up ptable only, got [{string.Join(",", set1)}]");

        var set2 = PreOpBackup.BackupSet(new[] { "oeminfo", "boot", "OEMINFO", "nvm" });
        Check(set2.Count == 3, $"redline plan dedups case-insensitively, got [{string.Join(",", set2)}]");
        Check(set2.Contains("oeminfo") && set2.Contains("nvm") && set2.Contains("ptable"),
            "redline plan contains oeminfo, nvm and ptable");

        var set3 = PreOpBackup.BackupSet(new[] { "ptable", "ptable", "boot" });
        Check(set3.Count == 1, $"duplicate ptable entries collapse, got [{string.Join(",", set3)}]");
    }

    // (b) Directory naming never overwrites an existing backup.
    private static void TestDirectoryNamingNeverOverwrites()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "kirin-backup-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var utc = new DateTime(2026, 11, 12, 8, 30, 0, DateTimeKind.Utc);
            string first = PreOpBackup.CreateTimestampDirectory(baseDir, utc);
            string second = PreOpBackup.CreateTimestampDirectory(baseDir, utc);
            string third = PreOpBackup.CreateTimestampDirectory(baseDir, utc);

            Check(first != second && second != third && first != third, "same-second runs get distinct directories");
            Check(Path.GetFileName(second).StartsWith(Path.GetFileName(first) + "-", StringComparison.Ordinal),
                $"suffix form is <stamp>-N, got {Path.GetFileName(second)}");
            Check(Directory.Exists(first) && Directory.Exists(second) && Directory.Exists(third),
                "all three backup directories survive");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    // (c) Manifest structure and per-partition status fields.
    private static void TestManifestStructureAndStatuses()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "kirin-backup-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dump = new Dictionary<string, (bool, string)>
            {
                ["oeminfo"] = (true, "OKAY"),
                ["ptable"] = (false, "invalid command"),
                ["nvm"] = (false, "FAILED (remote failure)") ,
            };
            var backup = new PreOpBackup((partition, path) =>
            {
                var (ok, msg) = dump[partition.ToLowerInvariant()];
                if (ok) File.WriteAllText(path, "payload-" + partition);
                return Task.FromResult((ok, msg));
            });

            var result = backup.RunAsync(new PreOpBackupOptions
            {
                Trigger = "flash-ota",
                WritePartitions = new[] { "oeminfo", "nvm", "boot" },
                BackupDir = baseDir,
                FastbootPath = "/usr/bin/fastboot",
            }).GetAwaiter().GetResult();

            Check(result.Attempted, "backup attempted");
            Check(result.Complete == false, "mixed results are incomplete");
            Check(result.OkCount == 1 && result.TotalCount == 3, $"1/3 partitions ok, got {result.OkCount}/{result.TotalCount}");
            Check(result.Directory != null && Directory.Exists(result.Directory), "backup directory exists");
            Check(result.ManifestPath != null && File.Exists(result.ManifestPath), "manifest exists");

            var oem = result.Partitions.First(p => p.Name == "oeminfo");
            Check(oem.Status == "ok" && oem.File != null && oem.Size > 0 && oem.Sha256 != null && oem.Sha256.Length == 64,
                "ok partition records file, size and sha256");
            var ptable = result.Partitions.First(p => p.Name == "ptable");
            Check(ptable.Status == "unsupported" && ptable.File == null && ptable.Reason != null,
                "invalid-command dump is unsupported with a reason");
            var nvm = result.Partitions.First(p => p.Name == "nvm");
            Check(nvm.Status == "failed" && nvm.Reason != null, "generic dump failure is failed with a reason");
            Check(!File.Exists(Path.Combine(result.Directory!, "nvm.img")), "failed dump leaves no stale file");

            string json = File.ReadAllText(result.ManifestPath!);
            Check(json.Contains("\"trigger\": \"flash-ota\"", StringComparison.Ordinal), "manifest records trigger");
            Check(json.Contains("\"sha256\":", StringComparison.Ordinal), "manifest records sha256");
            Check(json.Contains("\"serialNumber\": null", StringComparison.Ordinal), "unreadable device SN is null");
            Check(json.Contains("\"writePlan\":", StringComparison.Ordinal), "manifest records the write plan");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    // (d) All-unsupported still returns "may continue" with complete=false.
    private static void TestAllUnsupportedStillContinues()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "kirin-backup-unsupported-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backup = new PreOpBackup((_, _) => Task.FromResult((false, "invalid command")));
            var result = backup.RunAsync(new PreOpBackupOptions
            {
                Trigger = "gui-full-ota",
                WritePartitions = new[] { "oeminfo" },
                BackupDir = baseDir,
            }).GetAwaiter().GetResult();

            Check(result.Attempted, "backup attempted even on an unsupported loader");
            Check(result.Complete == false, "all-unsupported is incomplete");
            Check(result.OkCount == 0 && result.TotalCount == 2, $"0/2 ok, got {result.OkCount}/{result.TotalCount}");
            Check(result.Partitions.All(p => p.Status == "unsupported"), "every partition is unsupported");
            Check(result.Error == null, "no run-level error: the write may continue (best effort)");
            Check(result.ManifestPath != null, "manifest still written for the downgrade record");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    // (f) The backup run is one op: start + result, per-partition steps, explicit degradation.
    private static void TestOperationLogCoverage(string scratch)
    {
        string logRoot = Path.Combine(scratch, "oplog");
        string baseDir = Path.Combine(scratch, "backup-oplog");
        string? previousRoot = Environment.GetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DirectoryVariable);
        string? previousDisable = Environment.GetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DisableVariable);
        // v2.4.15 (log merge): events join the process-wide session, so a different log root only
        // takes effect after the current session is closed. Without this the op lands in the
        // session opened earlier with the previous root and this check reads an empty directory.
        Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();
        Environment.SetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DirectoryVariable, logRoot);
        Environment.SetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DisableVariable, null);
        try
        {
            var backup = new PreOpBackup((partition, savePath) =>
            {
                if (partition.Equals("oeminfo", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllText(savePath, "payload-" + partition);
                    return Task.FromResult((true, "OKAY"));
                }
                return Task.FromResult((false, "invalid command"));
            });

            // Run A: oeminfo dumps, nvm is a capability gap, the remaining partitions are then
            // recorded as unsupported instead of silently vanishing.
            var runA = backup.RunAsync(new PreOpBackupOptions
            {
                Trigger = "flash-ota",
                WritePartitions = new[] { "oeminfo", "nvm", "fsg", "modemst1" },
                KeepAllPartitions = true,
                BackupDir = Path.Combine(baseDir, "a"),
            }).GetAwaiter().GetResult();
            Check(!runA.Complete, "oplog run is still incomplete (behaviour unchanged)");

            // Run B: an exhausted budget must be an explicit skip, not an empty list.
            var runB = backup.RunAsync(new PreOpBackupOptions
            {
                Trigger = "backup",
                WritePartitions = new[] { "oeminfo", "nvm" },
                KeepAllPartitions = true,
                BackupDir = Path.Combine(baseDir, "b"),
                Budget = TimeSpan.Zero,
            }).GetAwaiter().GetResult();
            Check(runB.Attempted && runB.OkCount == 0, "an exhausted budget still returns a result (behaviour unchanged)");

            List<List<System.Text.Json.JsonElement>> runs = OpRuns(logRoot, "backup.preop");
            Check(runs.Count == 2, "each backup run wrote exactly one JSONL op: " + runs.Count);

            var lines = runs[0];
            Check(lines[0].GetProperty("op").GetString() == "backup.preop", "backup run uses the documented op name");
            Check(lines[0].GetProperty("phase").GetString() == "start", "backup op has a start line");
            var last = lines[^1];
            Check(last.GetProperty("phase").GetString() == "result", "backup op has a result line");
            Check(!last.GetProperty("ok").GetBoolean(), "an incomplete backup records ok=false");
            Check(last.GetProperty("verdictSource").GetString()!.StartsWith("partition-status:"),
                "backup verdict names the per-partition statuses");
            Check(last.GetProperty("notes").GetString()!.Contains("nvm=unsupported"),
                "the result names the unsupported partition, not just a warning line");

            var steps = lines.Where(line => line.GetProperty("phase").GetString() == "step")
                .Select(line => line.GetProperty("notes").GetString() ?? "")
                .ToList();
            Check(steps.Any(note => note.StartsWith("ok: oeminfo:") && note.Contains("sha256=")),
                "the successful dump step carries size and sha256");
            Check(steps.Any(note => note.StartsWith("degraded: nvm:") && note.Contains("invalid command")),
                "the loader capability gap is an explicit degraded step");
            Check(steps.Any(note => note.StartsWith("degraded: modemst1:")),
                "the partitions after the capability gap are recorded as unsupported, one by one");
            Check(steps.Any(note => note.Contains("manifest")), "the manifest write is a step of the same op");

            var runBSteps = runs[1].Where(line => line.GetProperty("phase").GetString() == "step")
                .Select(line => line.GetProperty("notes").GetString() ?? "")
                .ToList();
            Check(runBSteps.Any(note => note.StartsWith("skipped: oeminfo:")), "an exhausted budget is an explicit skip");
            Check(runs[1][^1].GetProperty("verdictSource").GetString()!.StartsWith("partition-status:0/"),
                "the skipped run names its zero-ok verdict");

            var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(runA.ManifestPath!));
            Check(manifest.RootElement.GetProperty("partitions").GetArrayLength() == runA.TotalCount,
                "the manifest still lists every attempted partition");
        }
        finally
        {
            Kirin_Tool.Services.Diagnostics.OperationLogger.ResetSessionForTests();
            Environment.SetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DirectoryVariable, previousRoot);
            Environment.SetEnvironmentVariable(Kirin_Tool.Services.Diagnostics.OperationLogger.DisableVariable, previousDisable);
        }
    }

    // (e) Atomic manifest rename: no .tmp residue, manifest is complete JSON.
    private static void TestAtomicManifestRename()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "kirin-backup-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var backup = new PreOpBackup((partition, path) =>
            {
                File.WriteAllText(path, "data");
                return Task.FromResult((true, "OKAY"));
            });
            var result = backup.RunAsync(new PreOpBackupOptions
            {
                Trigger = "backup",
                WritePartitions = Array.Empty<string>(),
                BackupDir = baseDir,
            }).GetAwaiter().GetResult();

            Check(result.Complete, "single ok dump is complete");
            Check(result.ManifestPath != null && File.Exists(result.ManifestPath), "manifest written");
            Check(!Directory.EnumerateFiles(result.Directory!, "*.tmp").Any(), "no .tmp residue after the rename");

            // The renamed file parses as JSON (System.Text.Json round-trip).
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(result.ManifestPath!));
            Check(doc.RootElement.TryGetProperty("partitions", out _), "manifest parses and has partitions");
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
