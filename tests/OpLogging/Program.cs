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
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string work = Path.Combine(Path.GetTempPath(), "kirin-oplog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string? previousLogDir = Environment.GetEnvironmentVariable(OperationLogger.DirectoryVariable);
string? previousDisable = Environment.GetEnvironmentVariable(OperationLogger.DisableVariable);
string? previousIdle = Environment.GetEnvironmentVariable(OperationLogger.SessionIdleVariable);
Environment.SetEnvironmentVariable(OperationLogger.DirectoryVariable, work);
Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, null);
// No idle rotation inside one test run: every op below joins the same session unless the test
// explicitly ends it, which is what makes "one session = one file" checkable.
Environment.SetEnvironmentVariable(OperationLogger.SessionIdleVariable, "0");

// Every JSONL line of the op named <op>, from any session file under <dir>. One session holds
// many ops now (that is the merge), so matching is per line, never per file.
static List<JsonElement> LinesOf(string dir, string op)
{
    var lines = new List<JsonElement>();
    foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        foreach (string raw in File.ReadAllLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("op", out JsonElement name) && name.GetString() == op)
                lines.Add(document.RootElement.Clone());
        }
    }
    return lines;
}

// One session = one session.jsonl: the session's own start line has no scopeId, every scope
// line does. These two helpers keep "the timeline" and "one operation's lines" apart.
static bool HasScope(JsonElement line) => line.TryGetProperty("scopeId", out _);
static List<JsonElement> ScopeLines(string dir, string op) => LinesOf(dir, op).Where(HasScope).ToList();
static List<JsonElement> SessionStartLines(string dir, string op) =>
    LinesOf(dir, op).Where(line => !HasScope(line) && line.GetProperty("phase").GetString() == "start").ToList();

static string? FileOf(string dir, string op)
{
    foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
    {
        foreach (string raw in File.ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.TryGetProperty("op", out JsonElement name) && name.GetString() == op) return file;
        }
    }
    return null;
}

try
{
    // ---- 1. JSONL field completeness: start / step / prompt / result ----
    {
        using (var scope = OperationLogger.Begin("test.fields", "fastboot", new[] { "flash", "boot", "/tmp/boot.img" }, "unit"))
        {
            Check(scope.FilePath != null && File.Exists(scope.FilePath!), "scope opens the session's main JSONL");
            Check(Path.GetFileName(scope.FilePath!) == OperationSession.MainLogFileName, "the main log is session.jsonl inside a session directory");
            string sessionDirectory = Path.GetDirectoryName(scope.FilePath!)!;
            Check(Path.GetFileName(Path.GetDirectoryName(sessionDirectory)!) == OperationSession.SessionsDirectoryName, "sessions live under <root>/sessions");
            Check(Path.GetFileName(sessionDirectory).Contains("-" + Environment.ProcessId), "session directory is <yyyyMMdd-HHMMSS>-<pid>-<op>");
            Check(scope.SessionId == Path.GetFileName(sessionDirectory), "OperationScope.SessionId == session directory name");
            Check(scope.SessionDirectory == sessionDirectory, "OperationScope.SessionDirectory exposes the directory for the GUI");
            scope.Step("issuing flash");
            scope.Prompt("Press POWER on the device", "yes");
            scope.Result(true, 0, 12, "OKAY [ 0.010s]", "", "exit-code", "tail-note");
        }

        List<JsonElement> lines = ScopeLines(work, "test.fields");
        Check(lines.Count == 4, "one line per phase (start/step/prompt/result): " + lines.Count);
        Check(lines.Select(l => l.GetProperty("phase").GetString()).SequenceEqual(new[] { "start", "step", "prompt", "result" }), "phases are ordered start, step, prompt, result");

        List<JsonElement> sessionOpen = SessionStartLines(work, "test.fields");
        Check(sessionOpen.Count == 1, "the session writes exactly one session-level start line");
        Check(sessionOpen[0].GetProperty("step").GetString() == "session.open", "the session start line is step=session.open");
        Check(sessionOpen[0].GetProperty("sessionId").GetString() == lines[0].GetProperty("sessionId").GetString(), "session and scope share the sessionId");
        Check(sessionOpen[0].GetProperty("seq").GetInt32() < lines[0].GetProperty("seq").GetInt32(), "the session start line precedes the first scope line");
        Check(lines.All(l => l.GetProperty("sessionId").GetString() == sessionOpen[0].GetProperty("sessionId").GetString()), "every scope line carries the sessionId");

        JsonElement start = lines[0];
        Check(start.GetProperty("ts").GetString()!.EndsWith("Z") && start.GetProperty("ts").GetString()!.Contains('.'), "ts is ISO8601 UTC with milliseconds");
        Check(start.GetProperty("pid").GetInt32() == Environment.ProcessId, "pid is the writing process");
        Check(start.GetProperty("tool").GetString() == "fastboot", "tool is recorded");
        Check(start.GetProperty("scopeId").GetString()!.StartsWith("scope-", StringComparison.Ordinal), "start line carries a scopeId");
        Check(start.GetProperty("parentId").GetString() == start.GetProperty("sessionId").GetString(), "start line points at its session via parentId");
        Check(start.GetProperty("seq").GetInt32() > 0, "every line carries a monotonic seq");
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
        Check(a.FilePath == b.FilePath, "concurrent scopes write into the SAME session.jsonl (that is the merge)");
        Check(a.SessionId == b.SessionId, "concurrent scopes share one sessionId");
        Check(a.Id != b.Id, "each scope still has its own scopeId");
        for (int i = 0; i < 5; i++)
        {
            a.Step("a" + i);
            b.Step("b" + i);
        }
        a.Result(true, null, null, null, null, "vcom:ack");
        b.Result(false, null, null, null, "b failed", "vcom:no-ack");

        List<JsonElement> linesA = ScopeLines(work, "test.interleave.A");
        List<JsonElement> linesB = ScopeLines(work, "test.interleave.B");
        Check(linesA.All(l => l.GetProperty("op").GetString() == "test.interleave.A"), "every A line keeps op=A");
        Check(linesB.All(l => l.GetProperty("op").GetString() == "test.interleave.B"), "every B line keeps op=B");
        Check(linesA.Count == 7 && linesB.Count == 7, "both scopes wrote start + 5 steps + result (" + linesA.Count + "/" + linesB.Count + ")");
        Check(linesA.Where(l => l.GetProperty("phase").GetString() == "step").All(l => l.GetProperty("notes").GetString()!.StartsWith("a")), "A steps are not polluted by B");
        Check(linesA[^1].GetProperty("ok").GetBoolean() && !linesB[^1].GetProperty("ok").GetBoolean(), "each scope keeps its own verdict");
        Check(linesA.Select(l => l.GetProperty("scopeId").GetString()).Distinct().Count() == 1, "A lines share one scopeId");

        // One timeline: seq is unique and strictly increasing in file order across both scopes.
        List<int> seqs = File.ReadAllLines(a.FilePath!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt32())
            .ToList();
        Check(seqs.SequenceEqual(seqs.OrderBy(value => value).Distinct()), "seq is unique and strictly increasing in file order");
        Check(seqs.Count >= linesA.Count + linesB.Count + 1, "the session file holds the session start line plus both scopes (" + seqs.Count + " lines)");

        // Ending a session starts a new directory: the timeline is bounded by the session, not by the process.
        OperationLogger.EndSession(true, "test:interleave");
        using var c = OperationLogger.Begin("test.interleave.C", "vcom");
        Check(c.FilePath != a.FilePath, "after EndSession the next scope opens a new session directory");
        Check(File.Exists(Path.Combine(Path.GetDirectoryName(a.FilePath!)!, OperationSession.SummaryFileName)),
            "the ended session wrote summary.json");
        OperationLogger.EndSession(true, "test:interleave-c");
        Check(!Directory.Exists(Path.Combine(work, "vcom")), "new writes no longer create the legacy <root>/vcom directory");
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
    // ---- 8. Session summary on every path + capability/approval correlation ----
    {
        Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, null);
        OperationLogger.ResetSessionForTests();

        // 8a. A failing child still leaves a summary.json behind (the audit trail never ends
        //     in a half-written directory).
        string failedSession;
        using (OperationLogger.BeginSession("test.summary.fail", "fastboot", new[] { "flash", "boot" }))
        {
            using (var scope = OperationLogger.Begin("test.summary.fail.child", "fastboot", new[] { "oem", "unlock" }))
            {
                scope.Step("device answered FAILED");
                scope.Result(false, 1, 5, "", "FAILED (remote: 'Command not allowed')", "exit-code:1");
            }
            failedSession = OperationLogger.CurrentSession!.Directory;
            OperationLogger.EndSession();
            using var _ = OperationLogger.Begin("test.noop", "internal");
        }

        string summaryPath = Path.Combine(failedSession, OperationSession.SummaryFileName);
        Check(File.Exists(summaryPath), "summary.json is written even when the child operation failed");
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(summaryPath)))
        {
            JsonElement summary = document.RootElement;
            Check(!summary.GetProperty("ok").GetBoolean(), "summary.json says ok=false for a failed child");
            Check(summary.GetProperty("verdictSource").GetString()!.Length > 0, "summary.json names the verdict source");
            Check(summary.GetProperty("counts").GetProperty("childrenFailed").GetInt32() == 1, "summary.json counts the failed children");
            Check(summary.GetProperty("paths").GetProperty("mainLog").GetString()!.EndsWith(OperationSession.MainLogFileName), "summary.json points at the main log");
            Check(summary.GetProperty("sessionId").GetString() == Path.GetFileName(failedSession), "summary.json sessionId matches the directory");
        }
        Check(!File.ReadAllLines(Path.Combine(failedSession, OperationSession.MainLogFileName)).Any(line => line.Contains("\"stdoutTail\":null")), "no null tails are written");

        if (!OperatingSystem.IsWindows())
        {
            var summaryMode = File.GetUnixFileMode(summaryPath);
            var sessionMode = File.GetUnixFileMode(failedSession);
            Check((summaryMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "summary.json is 0600");
            Check((sessionMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0, "session directory is 0700");
        }

        // 8b. capabilityId / approvalId ride along every line written inside the context: this is
        //     how a fastboot command is joined back to the registry row and the approval decision.
        OperationLogger.ResetSessionForTests();
        using (OperationLogger.CapabilityScope("unlock-bootloader", "fingerprint-abc123"))
        {
            using var scope = OperationLogger.Begin("test.capability", "fastboot", new[] { "oem", "unlock" });
            scope.Result(true, 0, 3, "OKAY", "", "exit-code");
            List<JsonElement> contextLines = ScopeLines(work, "test.capability");
            Check(contextLines.Count == 2, "capability scope writes start + result");
            Check(contextLines.All(l => l.GetProperty("capabilityId").GetString() == "unlock-bootloader"), "lines carry capabilityId inside CapabilityScope");
            Check(contextLines.All(l => l.GetProperty("approvalId").GetString() == "fingerprint-abc123"), "lines carry approvalId inside CapabilityScope");
        }
        using (var plain = OperationLogger.Begin("test.capability.outside", "fastboot"))
        {
            plain.Result(true, 0, 1, "", "", "exit-code");
            Check(!ScopeLines(work, "test.capability.outside")[0].TryGetProperty("capabilityId", out _),
                "no capabilityId leaks outside the context");
        }
        OperationLogger.EndSession(true, "test:capability");
    }

    // ---- 9. Approval decisions land on the same timeline (via the shared audit sink) ----
    {
        OperationLogger.ResetSessionForTests();
        var session = OperationLogger.BeginSession("test.approval", "fastboot", new[] { "oem", "unlock" });
        var sink = new OperationSessionApprovalSink();
        sink.Record(new Kirin_Tool.Services.Approval.ApprovalAuditEntry
        {
            Event = "approval.decision",
            CapabilityId = "unlock-bootloader",
            TitleZh = "解锁 bootloader",
            Origin = "cli",
            Decision = "denied",
            TouchesRedline = true,
            Reversibility = "irreversible",
            Fingerprint = "fingerprint-xyz",
            DecisionId = "fingerprint-xyz",
            Argv = new[] { "oem", "unlock" },
        });
        var approvalLines = LinesOf(work, "test.approval").Where(l => l.GetProperty("phase").GetString() == "step").ToList();
        Check(approvalLines.Any(l => l.GetProperty("step").GetString() == "approval.decision"), "the approval decision is a step on the session timeline");
        JsonElement decision = approvalLines.First(l => l.GetProperty("step").GetString() == "approval.decision");
        Check(decision.GetProperty("capabilityId").GetString() == "unlock-bootloader", "the approval line carries capabilityId");
        Check(decision.GetProperty("approvalId").GetString() == "fingerprint-xyz", "the approval line carries the approval fingerprint");
        Check(decision.GetProperty("approval").GetProperty("decision").GetString() == "denied", "the approval payload keeps the decision");
        Check(session.SessionId == decision.GetProperty("sessionId").GetString(), "the approval joined the open session instead of opening a new one");
        OperationLogger.EndSession(true, "test:approval");
    }
}
finally
{
    Environment.SetEnvironmentVariable(OperationLogger.DirectoryVariable, previousLogDir);
    Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, previousDisable);
    Environment.SetEnvironmentVariable(OperationLogger.SessionIdleVariable, previousIdle);
    OperationLogger.EndSession(true, "test:cleanup");
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " operation-logging checks passed; no device, fastboot or adb was contacted.");
