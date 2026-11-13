// Hardware-free checks for the unified operation log (Services/Diagnostics/OperationLogger.cs)
// and for the ProcessRunner/FastbootClient integration lane D added on top of it.
//
// Every check runs against a throwaway KIRIN_LOG_DIR and /bin/echo //bin/sh stubs: no fastboot,
// no adb, no device. Run from the repository root:
//   dotnet run --project tests/OpLogging -c Release

using System.Text;
using System.Text.Json;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string work = Path.Combine(Path.GetTempPath(), "kirin-oplog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string? previousLogDir = Environment.GetEnvironmentVariable(OperationLogger.DirectoryVariable);
string? previousDisable = Environment.GetEnvironmentVariable(OperationLogger.DisableVariable);
Environment.SetEnvironmentVariable(OperationLogger.DirectoryVariable, work);
Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, null);

// Whole-file argument check from a JSON snapshots of one op: returns every JSONL line of the
// op named <op>, read from whichever file in <dir> claims it. One op = one file by design.
static List<JsonElement> LinesOf(string dir, string op)
{
    var lines = new List<JsonElement>();
    foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        var parsed = new List<JsonElement>();
        foreach (string raw in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var document = JsonDocument.Parse(raw);
            parsed.Add(document.RootElement.Clone());
        }
        if (parsed.Count > 0 && parsed[0].GetProperty("op").GetString() == op) lines.AddRange(parsed);
    }
    return lines;
}

static string? FileOf(string dir, string op)
{
    foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        string first = File.ReadLines(file).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        if (first.Length == 0) continue;
        using var document = JsonDocument.Parse(first);
        if (document.RootElement.GetProperty("op").GetString() == op) return file;
    }
    return null;
}

// Newest op file wins: several checks below run the same op repeatedly and must read the
// result of the run they just made, not whichever file the filesystem happens to list first.
static List<JsonElement> LatestOp(string dir, string op)
{
    string? newest = null;
    string stamp = "";
    foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        string first = File.ReadLines(file).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
        if (first.Length == 0) continue;
        string fileOp;
        string started;
        using (var document = JsonDocument.Parse(first))
        {
            fileOp = document.RootElement.GetProperty("op").GetString() ?? "";
            started = document.RootElement.GetProperty("ts").GetString() ?? "";
        }
        if (fileOp != op) continue;
        // Order by the op's own start stamp (millisecond) and break ties with the file name,
        // which carries the -2/-3 suffix the logger appends inside the same millisecond.
        string key = started + "|" + Path.GetFileName(file);
        if (string.CompareOrdinal(key, stamp) <= 0) continue;
        newest = file;
        stamp = key;
    }
    if (newest == null) throw new Exception("no JSONL file for op " + op);

    var parsed = new List<JsonElement>();
    foreach (string raw in File.ReadAllLines(newest))
    {
        if (string.IsNullOrWhiteSpace(raw)) continue;
        using var document = JsonDocument.Parse(raw);
        parsed.Add(document.RootElement.Clone());
    }
    return parsed;
}

static List<string> StepNotes(IEnumerable<JsonElement> lines) =>
    lines.Where(line => line.GetProperty("phase").GetString() == "step")
         .Select(line => line.GetProperty("notes").GetString() ?? "")
         .ToList();

try
{
    // ---- 1. JSONL field completeness: start / step / prompt / result ----
    {
        using (var scope = OperationLogger.Begin("test.fields", "fastboot", new[] { "flash", "boot", "/tmp/boot.img" }, "unit"))
        {
            Check(scope.FilePath != null && File.Exists(scope.FilePath!), "scope opens exactly one JSONL file");
            Check(scope.FilePath!.EndsWith(".jsonl", StringComparison.Ordinal) && Path.GetFileName(scope.FilePath!).Contains("-" + Environment.ProcessId), "file name is <utc-ts>-<pid>.jsonl");
            Check(Path.GetFileName(Path.GetDirectoryName(scope.FilePath)!) == "fastboot", "fastboot op lands under <root>/fastboot");
            scope.Step("issuing flash");
            scope.Prompt("Press POWER on the device", "yes");
            scope.Result(true, 0, 12, "OKAY [ 0.010s]", "", "exit-code", "tail-note");
        }

        List<JsonElement> lines = LinesOf(work, "test.fields");
        Check(lines.Count == 4, "one line per phase (start/step/prompt/result): " + lines.Count);
        Check(lines.Select(l => l.GetProperty("phase").GetString()).SequenceEqual(new[] { "start", "step", "prompt", "result" }), "phases are ordered start, step, prompt, result");

        JsonElement start = lines[0];
        Check(start.GetProperty("ts").GetString()!.EndsWith("Z") && start.GetProperty("ts").GetString()!.Contains('.'), "ts is ISO8601 UTC with milliseconds");
        Check(start.GetProperty("pid").GetInt32() == Environment.ProcessId, "pid is the writing process");
        Check(start.GetProperty("tool").GetString() == "fastboot", "tool is recorded");
        Check(start.GetProperty("argv").GetArrayLength() == 3 && start.GetProperty("argv")[2].GetString() == "/tmp/boot.img", "argv is a JSON array of the real arguments");
        Check(start.GetProperty("notes").GetString() == "unit", "notes from Begin is kept");

        JsonElement step = lines[1];
        Check(step.GetProperty("notes").GetString() == "issuing flash", "step carries the note");
        Check(!step.TryGetProperty("ok", out _), "step lines carry no verdict");

        JsonElement prompt = lines[2];
        Check(prompt.GetProperty("notes").GetString()!.Contains("POWER"), "prompt carries the question");
        Check(prompt.GetProperty("answer").GetString() == "answer=yes", "prompt carries the accepted answer");

        JsonElement result = lines[3];
        Check(result.GetProperty("exitCode").GetInt32() == 0, "result carries exitCode");
        Check(result.GetProperty("durationMs").GetInt32() == 12, "result carries durationMs");
        Check(result.GetProperty("stdoutTail").GetString()!.Contains("OKAY"), "result carries stdoutTail");
        Check(result.GetProperty("stderrTail").GetString() == "", "result carries stderrTail (empty is explicit)");
        Check(result.GetProperty("ok").GetBoolean(), "result carries ok");
        Check(result.GetProperty("verdictSource").GetString() == "exit-code", "result names its verdictSource");
        Check(result.GetProperty("notes").GetString() == "tail-note", "result carries notes");

        // Permissions: 0700 directory, 0600 file (the log may contain device identifiers).
        if (!OperatingSystem.IsWindows())
        {
            string directory = Path.GetDirectoryName(FileOf(work, "test.fields")!)!;
            var fileMode = File.GetUnixFileMode(FileOf(work, "test.fields")!);
            var dirMode = File.GetUnixFileMode(directory);
            Check((fileMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "JSONL file is not group/world readable (0600)");
            Check((dirMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0, "log directory is private (0700)");
        }
    }

    // ---- 2. Two interleaved scopes never mix lines or files ----
    {
        using var a = OperationLogger.Begin("test.interleave.A", "vcom", new[] { "/dev/ttyUSB0" });
        using var b = OperationLogger.Begin("test.interleave.B", "vcom", new[] { "/dev/ttyUSB1" });
        Check(a.FilePath != b.FilePath, "concurrent scopes get distinct files even in the same millisecond");
        for (int i = 0; i < 5; i++)
        {
            a.Step("a" + i);
            b.Step("b" + i);
        }
        a.Result(true, null, null, null, null, "vcom:ack");
        b.Result(false, null, null, null, "b failed", "vcom:no-ack");

        List<JsonElement> linesA = LinesOf(work, "test.interleave.A");
        List<JsonElement> linesB = LinesOf(work, "test.interleave.B");
        Check(linesA.All(l => l.GetProperty("op").GetString() == "test.interleave.A"), "scope A file only holds A ops");
        Check(linesB.All(l => l.GetProperty("op").GetString() == "test.interleave.B"), "scope B file only holds B ops");
        Check(linesA.Count == 7 && linesB.Count == 7, "both scopes wrote start + 5 steps + result (" + linesA.Count + "/" + linesB.Count + ")");
        Check(linesA.Where(l => l.GetProperty("phase").GetString() == "step").All(l => l.GetProperty("notes").GetString()!.StartsWith("a")), "A steps are not polluted by B");
        Check(linesA[^1].GetProperty("ok").GetBoolean() && !linesB[^1].GetProperty("ok").GetBoolean(), "each scope keeps its own verdict");
        Check(Path.GetFileName(Path.GetDirectoryName(a.FilePath!)!) == "vcom", "vcom ops land under <root>/vcom");
    }

    // ---- 3. Failure path: ok=false, exitCode, stderrTail and a named verdict ----
    {
        ProcessResult result = await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "echo boom >&2; echo partial-out; exit 3" }, timeoutSeconds: 30, op: "test.failure");
        Check(result.ExitCode == 3 && !result.IsSuccess, "ProcessRunner still reports the raw exit code");
        Check(result.Output.Contains("boom") && result.Output.Contains("partial-out"), "combined Output keeps stdout+stderr for existing callers");

        List<JsonElement> lines = LinesOf(work, "test.failure");
        JsonElement last = lines[^1];
        Check(last.GetProperty("phase").GetString() == "result", "failure scope ends with a result line");
        Check(!last.GetProperty("ok").GetBoolean(), "failure path writes result.ok=false");
        Check(last.GetProperty("exitCode").GetInt32() == 3, "failure path records the exit code");
        Check(last.GetProperty("stderrTail").GetString()!.Contains("boom"), "failure path records stderrTail as the evidence");
        Check(last.GetProperty("stdoutTail").GetString()!.Contains("partial-out"), "failure path still keeps stdoutTail");
        Check(last.GetProperty("verdictSource").GetString()!.StartsWith("exit-code"), "failure path names exit-code as the verdict source");
        Check(last.GetProperty("tool").GetString() == "internal", "non-fastboot/binary name is classified as internal");

        JsonElement startLine = lines[0];
        Check(startLine.GetProperty("argv").GetArrayLength() == 2 && startLine.GetProperty("argv")[1].GetString()!.Contains("boom >&2"), "argv keeps the script text it was given");
    }

    // ---- 4. Unlock code is a parameter, validated at 16 chars, and masked when caller-supplied ----
    {
        Check(FastbootClient.DefaultUnlockCode.Length == FastbootClient.UnlockCodeLength, "the vendor magic is still the 16-char default");
        Check(new string('U', 16) == FastbootClient.DefaultUnlockCode, "default unlock code is the audited 16xU magic (H39 kept as a default, not inlined)");

        var client = new FastbootClient("/bin/true");

        (bool okShort, string outShort) = await client.UnlockBootloaderAsync(null, "SHORT");
        Check(!okShort && outShort.Contains("16"), "a 15-char unlock code is rejected before fastboot runs");
        List<JsonElement> rejected = LinesOf(work, "fastboot.unlock");
        JsonElement rejectedResult = rejected[^1];
        Check(rejectedResult.GetProperty("phase").GetString() == "result" && !rejectedResult.GetProperty("ok").GetBoolean(), "rejected code writes ok=false");
        Check(rejectedResult.GetProperty("verdictSource").GetString() == "arg-validation", "rejected code names verdictSource=arg-validation");
        Check(!rejectedResult.TryGetProperty("exitCode", out _), "rejected code has no exitCode (nothing was executed)");
        Check(rejectedResult.GetProperty("stderrTail").GetString()!.Contains("nothing was sent"), "rejected code explains itself in stderrTail");

        (bool okLong, _) = await client.UnlockBootloaderAsync(null, new string('A', 17));
        Check(!okLong, "a 17-char unlock code is rejected too");

        // /bin/true accepts any argv: this exercises the real spawn + logging path without fastboot.
        (bool okDefault, _) = await client.UnlockBootloaderAsync(null, FastbootClient.DefaultUnlockCode);
        Check(okDefault, "a 16-char code reaches the spawn path (stub /bin/true exits 0)");
        List<JsonElement> spawned = LinesOf(work, "fastboot.unlock");
        JsonElement spawnResult = spawned[^1];
        Check(spawnResult.GetProperty("phase").GetString() == "result" && spawnResult.GetProperty("ok").GetBoolean(), "spawned unlock writes a result line");
        Check(spawnResult.GetProperty("exitCode").GetInt32() == 0, "spawned unlock records its exit code");
        // `tool` follows the binary name (/bin/true here, the bundled `fastboot` in production), while
        // the op stays the semantic `fastboot.unlock` the caller asked for.
        Check(spawnResult.GetProperty("tool").GetString() == "internal", "the /bin/true stub is classified by its binary name, not by the op name");
        Check(spawnResult.GetProperty("verdictSource").GetString() == "exit-code", "spawned unlock verdict is the exit code");
        // The default vendor magic is public and stays readable; a caller-supplied one is masked.
        List<JsonElement> startLines = LinesOf(work, "fastboot.unlock").Where(l => l.GetProperty("phase").GetString() == "start").ToList();
        Check(startLines.Any(l => l.GetProperty("argv").EnumerateArray().Any(a => a.GetString() == FastbootClient.DefaultUnlockCode)), "the default vendor magic stays readable in argv");

        (bool okCustom, _) = await client.UnlockBootloaderAsync(null, "AAAABBBBCCCCDDDD");
        Check(okCustom, "a caller-supplied 16-char code is accepted");
        List<JsonElement> customStart = LinesOf(work, "fastboot.unlock").Where(l => l.GetProperty("phase").GetString() == "start").ToList();
        Check(customStart.Any(l => l.GetProperty("argv").EnumerateArray().Any(a => a.GetString() == "***(16)")), "a caller-supplied code is masked as ***(16)");

        // Argument validation never leaks the rejected value into a readable field either.
        Check(!File.ReadAllText(FileOf(work, "fastboot.unlock")!).Contains("AAAABBBBCCCCDDDD"), "the custom code never appears verbatim on disk");

        // Streaming overload is the replacement for the removed bare Process: it logs identically.
        ProcessResult streamed = await ProcessRunner.RunStreamingAsync("/bin/sh", new[] { "-c", "echo streamed-line" }, progress: null, timeoutSeconds: 30, op: "test.streaming");
        Check(streamed.IsSuccess && streamed.Output.Contains("streamed-line"), "RunStreamingAsync returns the same ProcessResult shape");
        JsonElement streamingResult = LinesOf(work, "test.streaming")[^1];
        Check(streamingResult.GetProperty("ok").GetBoolean() && streamingResult.GetProperty("stdoutTail").GetString()!.Contains("streamed-line"), "RunStreamingAsync writes the same JSONL fields");
    }

    // ---- 5. ProcessRunner JSONL matches what the caller got back (/bin/echo stub) ----
    {
        ProcessResult echo = await ProcessRunner.RunAsync("/bin/echo", new[] { "hello", "world" }, timeoutSeconds: 30, op: "test.echo");
        Check(echo.ExitCode == 0 && echo.Output.Trim() == "hello world", "/bin/echo stub returns exit 0 and its text");

        List<JsonElement> lines = LinesOf(work, "test.echo");
        JsonElement start = lines[0];
        JsonElement last = lines[^1];
        Check(start.GetProperty("op").GetString() == "test.echo" && start.GetProperty("tool").GetString() == "internal", "explicit op is used, tool derived from the binary name");
        Check(start.GetProperty("argv").GetArrayLength() == 2 && start.GetProperty("argv")[0].GetString() == "hello", "argv matches the arguments the caller passed");
        Check(last.GetProperty("exitCode").GetInt32() == echo.ExitCode, "logged exitCode == returned ExitCode");
        Check(last.GetProperty("ok").GetBoolean() == echo.IsSuccess, "logged ok == returned IsSuccess");
        Check(last.GetProperty("stdoutTail").GetString()!.Contains("hello world"), "logged stdoutTail matches the returned Output");
        Check(last.GetProperty("durationMs").GetInt32() >= 0, "durationMs is measured, not guessed");
        Check(last.GetProperty("verdictSource").GetString() == "exit-code", "a clean run is decided by the exit code");

        // Timeout path: the log must say timeout, and the exception contract is unchanged.
        bool timedOut = false;
        try
        {
            await ProcessRunner.RunAsync("/bin/sh", new[] { "-c", "sleep 5" }, timeoutSeconds: 1, op: "test.timeout");
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        Check(timedOut, "timeout still surfaces as TimeoutException to the caller");
        JsonElement timeoutResult = LinesOf(work, "test.timeout")[^1];
        Check(!timeoutResult.GetProperty("ok").GetBoolean() && timeoutResult.GetProperty("verdictSource").GetString() == "timeout", "timeout is logged with verdictSource=timeout");
    }

    // ---- 6. Redaction and truncation are enforced by the logger itself ----
    {
        IReadOnlyList<string> redacted = OperationLogger.Redact(new[] { "--password", "hunter2", "--token=abc", "flash", "boot" });
        Check(redacted[1] == "***", "credential option value is masked");
        Check(redacted[2] == "--token=***", "an inline credential option masks only its value");
        Check(redacted[3] == "flash" && redacted[4] == "boot", "ordinary arguments survive redaction");

        IReadOnlyList<string> parsed = OperationLogger.ParseArgv("oem dump-emmc oeminfo \"/tmp/a b.bin\"");
        Check(parsed.Count == 4 && parsed[3] == "/tmp/a b.bin", "quoted path stays one argv token in the log");

        string big = new string('x', 5000);
        using (var scope = OperationLogger.Begin("test.tail", "internal", Array.Empty<string>()))
            scope.Result(false, 1, 1, big, big, "exit-code:1");
        JsonElement tailResult = LinesOf(work, "test.tail")[^1];
        Check(Encoding.UTF8.GetByteCount(tailResult.GetProperty("stdoutTail").GetString()!) <= 2048, "stdoutTail is capped at 2 KB");
        Check(Encoding.UTF8.GetByteCount(tailResult.GetProperty("stderrTail").GetString()!) <= 2048, "stderrTail is capped at 2 KB");

        // A scope that never reports a verdict still ends with an explicit, non-ok line.
        using (OperationLogger.Begin("test.noresult", "internal", Array.Empty<string>())) { }
        JsonElement noResult = LinesOf(work, "test.noresult")[^1];
        Check(noResult.GetProperty("phase").GetString() == "result" && !noResult.GetProperty("ok").GetBoolean(), "dispose without verdict writes ok=false");
        Check(noResult.GetProperty("verdictSource").GetString()!.StartsWith("unknown"), "dispose without verdict says why");
    }

    // ---- 7. Kill switch: KIRIN_LOG_DISABLE=1 writes nothing and changes no return value ----
    {
        Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, "1");
        using (var scope = OperationLogger.Begin("test.disabled", "internal", Array.Empty<string>()))
        {
            scope.Result(true);
            Check(scope.FilePath == null, "disabled logger opens no file");
        }
        ProcessResult echo = await ProcessRunner.RunAsync("/bin/echo", new[] { "still", "works" }, timeoutSeconds: 30, op: "test.disabled");
        Check(echo.IsSuccess && echo.Output.Contains("still works"), "disabling the log does not touch the process result");
    }

    // ---- 8. backup.preop: dumps, degradations and the manifest are steps of one op ----
    {
        // Section 7 turned the logger off; the write-path checks below need it back on.
        Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, null);
        string backupDir = Path.Combine(work, "backup");
        var backup = new PreOpBackup((partition, savePath) =>
        {
            if (partition.Equals("nvm", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult((false, "invalid command"));
            File.WriteAllText(savePath, "payload-" + partition);
            return Task.FromResult((true, "OKAY"));
        });

        PreOpBackupResult result = await backup.RunAsync(new PreOpBackupOptions
        {
            Trigger = "test-preop",
            WritePartitions = new[] { "oeminfo", "nvm", "boot" },
            BackupDir = backupDir,
            FastbootPath = "/bin/true",
        });

        List<JsonElement> lines = LatestOp(work, "backup.preop");
        Check(lines.Count > 5, "backup.preop records the run as one op: " + lines.Count + " lines");
        Check(lines[0].GetProperty("phase").GetString() == "start", "backup.preop opens with a start line");
        Check(lines[0].GetProperty("argv")[0].GetString() == "test-preop", "backup.preop argv names the trigger");
        Check(lines[0].GetProperty("op").GetString() == "backup.preop", "backup op name is the documented one");

        JsonElement last = lines[^1];
        Check(last.GetProperty("phase").GetString() == "result", "backup.preop ends with a result line");
        Check(!last.GetProperty("ok").GetBoolean(), "an incomplete backup is not ok");
        Check(last.GetProperty("verdictSource").GetString()!.StartsWith("partition-status:"),
            "backup verdict names the per-partition statuses: " + last.GetProperty("verdictSource").GetString());
        Check(last.GetProperty("notes").GetString()!.Contains("nvm=unsupported"),
            "backup result names the unsupported partition and why: " + last.GetProperty("notes").GetString());

        List<string> steps = StepNotes(lines);
        Check(steps.Any(s => s.StartsWith("ok: oeminfo:") && s.Contains("sha256=")),
            "a successful dump step carries size and sha256");
        Check(steps.Any(s => s.StartsWith("degraded: nvm:") && s.Contains("invalid command")),
            "an unsupported dump is an explicit degraded step, not a silent warning");
        Check(steps.Any(s => s.Contains("manifest:")), "the manifest write is a step of the same op");
        Check(result.Partitions.All(p => p.Status != null) && result.OkCount > 0, "the caller still gets the per-partition statuses");
    }

    // ---- 9. flash.image / backup.dump: a standalone flasher call is its own op ----
    {
        string image = Path.Combine(work, "boot.img");
        await File.WriteAllTextAsync(image, "boot-payload");

        var good = new FastbootFlasherService(new FastbootClient("/bin/true"));
        (bool okFlash, _) = await good.FlashPartitionAsync("boot", image);
        Check(okFlash, "/bin/true stub flash still reports exit-0 success");

        List<JsonElement> flashLines = LatestOp(work, "flash.image");
        Check(flashLines[0].GetProperty("phase").GetString() == "start", "flash.image starts with a start line");
        JsonElement flashResult = flashLines[^1];
        Check(flashResult.GetProperty("phase").GetString() == "result", "flash.image ends with a result line");
        Check(flashResult.GetProperty("verdictSource").GetString() == "exit-code", "single-image verdict is the exit code");
        Check(StepNotes(flashLines).Any(s => s.Contains("flash boot <- ") && s.Contains("boot.img")),
            "the step names the partition and the image file");

        var bad = new FastbootFlasherService(new FastbootClient("/bin/false"));
        (bool okBad, _) = await bad.FlashPartitionAsync("recovery", image);
        Check(!okBad, "/bin/false stub flash reports failure (control flow unchanged)");
        JsonElement badResult = LatestOp(work, "flash.image")[^1];
        Check(!badResult.GetProperty("ok").GetBoolean(), "a failed image write writes ok=false");
        Check(badResult.GetProperty("verdictSource").GetString()!.StartsWith("exit-code:"),
            "a failed image write names the exit code: " + badResult.GetProperty("verdictSource").GetString());

        // A loader that answers the dump verbs but writes no file: the op must say which evidence
        // decided the failure instead of looking like a generic exception.
        (bool okDump, _) = await good.DumpPartitionAsync("oeminfo", Path.Combine(work, "oeminfo-dump.img"));
        Check(!okDump, "a dump without a written file is still a failure");
        List<JsonElement> dumpLines = LatestOp(work, "backup.dump");
        Check(dumpLines[0].GetProperty("phase").GetString() == "start", "backup.dump starts with a start line");
        Check(!dumpLines[^1].GetProperty("ok").GetBoolean(), "backup.dump writes ok=false");
        Check(dumpLines[^1].GetProperty("verdictSource").GetString() == "file-missing",
            "backup.dump names file-missing as the evidence: " + dumpLines[^1].GetProperty("verdictSource").GetString());
        Check(StepNotes(dumpLines).Count(s => s.Contains("verb dump-")) == 2,
            "both dump verbs are recorded as steps");
    }

    // ---- 10. oeminfo.convert / oeminfo.write ----
    {
        static byte[] OemInfoImage(int version, int entryId)
        {
            var data = new byte[0x400];
            Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(data, 0);
            BitConverter.GetBytes(version).CopyTo(data, 8);
            BitConverter.GetBytes(entryId).CopyTo(data, 12);
            for (int i = 0x200; i < data.Length; i++) data[i] = 0xFF;
            return data;
        }

        string oeminfo = Path.Combine(work, "oeminfo.img");
        var service = new OemInfoService(new FastbootClient("/bin/true"));

        await File.WriteAllBytesAsync(oeminfo, OemInfoImage(9, 0x301));
        OemInfoConversionResult converted = await service.ConvertOemInfo(false, oeminfo, "MODEL-A", "hw/cn");
        Check(converted.IsSuccess && converted.ConvertedData != null, "a supported oeminfo version still converts");
        List<JsonElement> convertLines = LatestOp(work, "oeminfo.convert");
        Check(convertLines[0].GetProperty("phase").GetString() == "start", "oeminfo.convert starts with a start line");
        JsonElement convertResult = convertLines[^1];
        Check(convertResult.GetProperty("phase").GetString() == "result" && convertResult.GetProperty("ok").GetBoolean(),
            "oeminfo.convert ends ok on success");
        Check(convertResult.GetProperty("verdictSource").GetString() == "conversion:success",
            "conversion verdict names its rule: " + convertResult.GetProperty("verdictSource").GetString());
        List<string> convertSteps = StepNotes(convertLines);
        Check(convertSteps.Any(s => s.Contains("redline")), "conversion records the oeminfo redline judgement");
        Check(convertSteps.Any(s => s.Contains("converted:")), "conversion records input and output sizes");

        await File.WriteAllBytesAsync(oeminfo, OemInfoImage(7, 0x301));
        OemInfoConversionResult unsupported = await service.ConvertOemInfo(false, oeminfo, "M", "R");
        Check(!unsupported.IsSuccess, "an unsupported oeminfo version does not convert");
        JsonElement unsupportedResult = LatestOp(work, "oeminfo.convert")[^1];
        Check(!unsupportedResult.GetProperty("ok").GetBoolean(), "unsupported version writes ok=false");
        Check(unsupportedResult.GetProperty("verdictSource").GetString() == "conversion:unsupported",
            "unsupported version is a named verdict: " + unsupportedResult.GetProperty("verdictSource").GetString());
        Check(unsupportedResult.GetProperty("stderrTail").GetString()!.Contains("not supported"),
            "the unsupported verdict keeps the library message as evidence");

        await File.WriteAllBytesAsync(oeminfo, new byte[0x400]);
        OemInfoConversionResult refused = await service.ConvertOemInfo(false, oeminfo, "M", "R");
        Check(!refused.IsSuccess, "an image without the OEM_INFO magic is refused");
        Check(LatestOp(work, "oeminfo.convert")[^1].GetProperty("verdictSource").GetString() == "conversion:refused",
            "an undetectable image is refused with a named verdict");

        OemInfoConversionResult missing = await service.ConvertOemInfo(false, Path.Combine(work, "nope.img"), "M", "R");
        Check(!missing.IsSuccess, "a missing input file is still an error");
        Check(LatestOp(work, "oeminfo.convert")[^1].GetProperty("verdictSource").GetString() == "arg-validation",
            "a missing input is arg-validation (nothing was read)");

        // v2.4.15: the device half moved to DeviceWriteOps.FlashOemInfoAsync so it cannot bypass
        // the approval gate; the op name and verdict rules are unchanged, so this check stays.
        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(ApprovalRequests.For("oeminfo-flash",
            argv: new[] { "fastboot", "flash", "oeminfo", oeminfo }, targets: new[] { "oeminfo" }));
        var writeOps = new Kirin_Tool.Services.WriteOperations.DeviceWriteOps(new FastbootClient("/bin/true"));
        ProcessResult write = await writeOps.FlashOemInfoAsync(admission.RequireTicket(), oeminfo);
        Check(write.IsSuccess, "/bin/true stub oeminfo write still reports success");
        List<JsonElement> writeLines = LatestOp(work, "oeminfo.write");
        Check(writeLines[0].GetProperty("phase").GetString() == "start", "oeminfo.write starts with a start line");
        Check(writeLines[^1].GetProperty("phase").GetString() == "result"
            && writeLines[^1].GetProperty("verdictSource").GetString() == "exit-code",
            "oeminfo.write ends with an exit-code verdict");
        Check(StepNotes(writeLines).Any(s => s.Contains("redline")), "oeminfo.write records the redline nature of the partition");
    }

    // ---- 11. dload cleanup: the listing is recorded before anything is deleted ----
    {
        string dload = Path.Combine(work, "dload");
        Directory.CreateDirectory(Path.Combine(dload, "part0"));
        await File.WriteAllTextAsync(Path.Combine(dload, "list.txt"), "oeminfo 1\n");
        await File.WriteAllBytesAsync(Path.Combine(dload, "part0", "boot.img"), new byte[2048]);

        OpLog.CleanDirectory(dload, OpNames.UsbUpdateCleanup);
        Check(!Directory.Exists(dload), "cleanup still removes the dload directory");

        List<JsonElement> lines = LatestOp(work, "usbupdate.dload-cleanup");
        List<string> steps = StepNotes(lines);
        Check(steps.Any(s => s.StartsWith("before delete:") && s.Contains("2 files")),
            "cleanup records the file count and total bytes before deleting: " + string.Join(" | ", steps));
        Check(steps.Any(s => s.StartsWith("deleting:") && s.Contains("list.txt") && s.Contains("boot.img")),
            "cleanup records the path listing before deleting");
        JsonElement last = lines[^1];
        Check(last.GetProperty("phase").GetString() == "result" && last.GetProperty("ok").GetBoolean(),
            "cleanup ends with an explicit ok result");
        Check(last.GetProperty("verdictSource").GetString() == "directory-deleted", "cleanup verdict says the directory was deleted");
    }
}
finally
{
    Environment.SetEnvironmentVariable(OperationLogger.DirectoryVariable, previousLogDir);
    Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, previousDisable);
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " operation-logging checks passed; no device, fastboot or adb was contacted.");

/// <summary>Test double: approves everything, and types the confirm phrase when the gate asks.</summary>
internal sealed class ApproveEveryRequest : IApprovalPrompt
{
    public Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalPromptAnswer(
            ApprovalPromptOutcome.Approved,
            model.RequiresTypedConfirm ? model.ConfirmPhrase : null));
}
