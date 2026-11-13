// Hardware-free harness for the v2.4.15 log merge (docs/log-merge.md).
//
// One operation = one session directory = one session.jsonl. This harness proves the merge with
// a real VCOM adapter + a real child process (no device: a shell stub named `fastboot`), then
// proves the read side (`logs` CLI) still serves the pre-2.4.15 files.
//
// Run from the repository root:  dotnet run --project tests/LogMerge -c Release

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

string? cli = new[]
{
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
}.Where(File.Exists)
 .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
 .FirstOrDefault() ?? throw new Exception(
    "FAIL cli binary not found; build it first: dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release");

string work = Path.Combine(Path.GetTempPath(), "kirin-logmerge-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string logDir = Path.Combine(work, "logs");
Directory.CreateDirectory(logDir);

string? previousLogDir = Environment.GetEnvironmentVariable(OperationLogger.DirectoryVariable);
string? previousDisable = Environment.GetEnvironmentVariable(OperationLogger.DisableVariable);
string? previousIdle = Environment.GetEnvironmentVariable(OperationLogger.SessionIdleVariable);
Environment.SetEnvironmentVariable(OperationLogger.DirectoryVariable, logDir);
Environment.SetEnvironmentVariable(OperationLogger.DisableVariable, null);
Environment.SetEnvironmentVariable(OperationLogger.SessionIdleVariable, "0");

(int Code, string Out, string Err) RunCli(params string[] argv)
{
    bool dll = cli.EndsWith(".dll", StringComparison.Ordinal);
    var psi = new ProcessStartInfo(dll ? "dotnet" : cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = true,
        WorkingDirectory = root,
    };
    if (dll) psi.ArgumentList.Add(cli);
    foreach (string a in argv) psi.ArgumentList.Add(a);
    psi.Environment["KIRIN_LOG_DIR"] = logDir;
    using Process process = Process.Start(psi)!;
    process.StandardInput.Close();
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(TimeSpan.FromMinutes(3)))
    {
        try { process.Kill(true); } catch { }
        throw new TimeoutException("CLI did not finish within 3 minutes");
    }
    return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
}

static List<JsonElement> ReadEvents(string path)
{
    var events = new List<JsonElement>();
    foreach (string raw in File.ReadAllLines(path))
    {
        if (string.IsNullOrWhiteSpace(raw)) continue;
        using JsonDocument document = JsonDocument.Parse(raw);
        events.Add(document.RootElement.Clone());
    }
    return events;
}

try
{
    // ---------- 1. VCOM session + the fastboot command that follows share ONE session.jsonl ----------
    string sessionDirectory;
    string sessionId;
    {
        // A shell stub literally named `fastboot`, so ProcessRunner files it as tool=fastboot
        // without any real binary or device.
        string stubDirectory = Path.Combine(work, "bin");
        Directory.CreateDirectory(stubDirectory);
        string fakeFastboot = Path.Combine(stubDirectory, "fastboot");
        File.WriteAllText(fakeFastboot, "#!/bin/sh\necho 'OKAY [  0.010s]'\necho 'Finished. Total time: 0.011s'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fakeFastboot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var vcom = VcomSessionLogger.Create("hisi990");
        sessionDirectory = vcom.SessionDirectory ?? throw new Exception("FAIL VCOM adapter did not open a session directory");
        sessionId = vcom.SessionId!;
        Check(File.Exists(vcom.FilePath!), "the VCOM adapter keeps its human transcript (vcom-trace.txt)");
        Check(vcom.MainLogPath != null && File.Exists(vcom.MainLogPath), "the VCOM adapter writes into the session's session.jsonl");
        Check(Path.GetDirectoryName(vcom.MainLogPath!) == sessionDirectory, "transcript and timeline live in the same directory");

        vcom.Event("port.open", ("port", "/dev/pts/9"), ("explicit", true), ("bulkOnly", true));
        vcom.Event("frame.write", ("type", "HEAD"), ("addr", "0x00100000"), ("len", 1024), ("bytes", 1029), ("expectAck", true));
        vcom.Event("frame.write", ("type", "DATA"), ("addr", "0x00100000"), ("seq", 3), ("len", 1034), ("bytes", 1039), ("cumulativeBytes", 4096), ("expectAck", true));
        vcom.Event("frame.ack", ("type", "DATA"), ("result", "ok"), ("raw", (byte)0xAA));
        vcom.Event("frame.ack", ("type", "DATA"), ("result", "timeout"), ("waitedMs", 10000), ("fatal", false));
        vcom.Progress("writing bl2.ktl");
        vcom.Event("prompt", ("text", "Replug the cable into the normal USB port"));
        vcom.Event("prompt.answer", ("accepted", true));
        vcom.Dispose();

        // The fastboot step of the same operation: same process, after the cable replug.
        ProcessResult flash = await ProcessRunner.RunAsync(fakeFastboot, new[] { "oem", "unlock" }, timeoutSeconds: 30, op: "fastboot.unlock");
        Check(flash.IsSuccess, "the fake fastboot stub ran and the log merge did not change its result");
        OperationLogger.EndSession(true, "vcom:ack", "upload + unlock in one session");

        string mainLog = Path.Combine(sessionDirectory, OperationSession.MainLogFileName);
        List<JsonElement> events = ReadEvents(mainLog);
        Check(events.All(e => e.GetProperty("sessionId").GetString() == sessionId), "every line of the timeline carries the same sessionId");
        Check(events.Any(e => e.GetProperty("op").GetString() == "fastboot.unlock"), "the fastboot command landed in the SAME session.jsonl as the VCOM frames");
        Check(events.Any(e => e.GetProperty("op").GetString() == "vcom.session"), "the VCOM frames are in that file too");

        List<int> seq = events.Select(e => e.GetProperty("seq").GetInt32()).ToList();
        Check(seq.SequenceEqual(seq.OrderBy(v => v).Distinct()), "seq is monotonic and unique across both legs (" + seq.Count + " lines)");

        JsonElement frameWrite = events.First(e => e.TryGetProperty("step", out JsonElement s) && s.GetString() == "frame.write" &&
            e.GetProperty("frame").GetProperty("type").GetString() == "DATA");
        Check(frameWrite.GetProperty("phase").GetString() == "step", "a VCOM frame line maps to phase=step");
        Check(frameWrite.GetProperty("tool").GetString() == "vcom", "the frame keeps tool=vcom");
        Check(frameWrite.GetProperty("frame").GetProperty("frameSeq").GetInt32() == 3, "the DATA frame sequence lives in frame.frameSeq, not in the row seq");
        Check(frameWrite.GetProperty("frame").GetProperty("addr").GetString() == "0x00100000", "the frame address is structured");

        JsonElement frameAck = events.First(e => e.TryGetProperty("step", out JsonElement s) && s.GetString() == "frame.ack");
        Check(frameAck.GetProperty("ack").GetProperty("raw").GetString() == "0xAA", "the raw ACK byte survives as 0xAA");
        Check(events.Any(e => e.TryGetProperty("step", out JsonElement s) && s.GetString() == "frame.ack" &&
            e.GetProperty("ack").GetProperty("result").GetString() == "timeout"), "an ACK timeout is a step, not a verdict");
        Check(events.Any(e => e.TryGetProperty("phase", out JsonElement p) && p.GetString() == "prompt" &&
            e.GetProperty("step").GetString() == "prompt"), "the cable prompt maps to phase=prompt");
        Check(events.Any(e => e.TryGetProperty("notes", out JsonElement n) && n.GetString() == "writing bl2.ktl"), "progress text is a step note");

        JsonElement fastbootLine = events.Last(e => e.GetProperty("op").GetString() == "fastboot.unlock");
        Check(fastbootLine.GetProperty("tool").GetString() == "fastboot", "the fastboot leg is identified as tool=fastboot");
        Check(fastbootLine.TryGetProperty("parentId", out _), "child commands carry a parentId (the stage/session they belong to)");

        // ---------- 4. summary.json on a normal end ----------
        string summaryPath = Path.Combine(sessionDirectory, OperationSession.SummaryFileName);
        Check(File.Exists(summaryPath), "summary.json is written when the session ends");
        using (JsonDocument summary = JsonDocument.Parse(File.ReadAllText(summaryPath)))
        {
            JsonElement body = summary.RootElement;
            Check(body.GetProperty("ok").GetBoolean(), "summary.json records the session verdict");
            Check(body.GetProperty("counts").GetProperty("frames").GetInt32() == 2, "summary.json counts the VCOM frames");
            Check(body.GetProperty("counts").GetProperty("acks").GetInt32() == 2, "summary.json counts the ACKs");
            Check(body.GetProperty("counts").GetProperty("ackBad").GetInt32() == 1, "summary.json counts the non-ACK answers");
            Check(body.GetProperty("counts").GetProperty("children").GetInt32() >= 1, "summary.json counts the child operations");
            Check(body.GetProperty("paths").GetProperty("trace").GetString()!.EndsWith(OperationSession.TraceFileName), "summary.json points at the transcript");
        }

        // ---------- 5. permissions ----------
        if (!OperatingSystem.IsWindows())
        {
            var fileMode = File.GetUnixFileMode(Path.Combine(sessionDirectory, OperationSession.MainLogFileName));
            var traceMode = File.GetUnixFileMode(Path.Combine(sessionDirectory, OperationSession.TraceFileName));
            var summaryMode = File.GetUnixFileMode(summaryPath);
            var dirMode = File.GetUnixFileMode(sessionDirectory);
            Check((fileMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "session.jsonl is 0600");
            Check((traceMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "vcom-trace.txt is 0600");
            Check((summaryMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "summary.json is 0600");
            Check((dirMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0, "session directory is 0700");
        }
    }

    // ---------- 4b. summary.json is written on the failure path too ----------
    {
        OperationLogger.ResetSessionForTests();
        string failedDirectory;
        using (OperationLogger.BeginSession("fastboot.unlock", "fastboot", new[] { "oem", "unlock" }))
        {
            using (var scope = OperationLogger.Begin("fastboot.unlock", "fastboot", new[] { "oem", "unlock" }))
            {
                scope.Step("device answered FAILED");
                scope.Result(false, 1, 7, "", "FAILED (remote: 'Command not allowed')", "exit-code:1");
            }
            failedDirectory = OperationLogger.CurrentSession!.Directory;
            OperationLogger.EndSession();
        }

        string failedSummary = Path.Combine(failedDirectory, OperationSession.SummaryFileName);
        Check(File.Exists(failedSummary), "a failed session still writes summary.json");
        using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(failedSummary));
        Check(!summary.RootElement.GetProperty("ok").GetBoolean(), "the failed summary says ok=false");
        Check(summary.RootElement.GetProperty("verdictSource").GetString()!.StartsWith("exit-code"), "the failed summary keeps the verdict source");
        Check(summary.RootElement.GetProperty("counts").GetProperty("childrenFailed").GetInt32() == 1, "the failed summary counts the failed child");
    }

    // ---------- 2. legacy files keep working through the same read path ----------
    {
        // Pre-2.4.15 layout, written by hand: one op per file under <root>/<category>/.
        string legacyCategory = Path.Combine(logDir, "fastboot");
        Directory.CreateDirectory(legacyCategory);
        string legacyJsonl = Path.Combine(legacyCategory, "20260101T000000Z-4242.jsonl");
        File.WriteAllLines(legacyJsonl, new[]
        {
            "{\"ts\":\"2026-01-01T00:00:00.000Z\",\"pid\":4242,\"phase\":\"start\",\"op\":\"fastboot.flash\",\"tool\":\"fastboot\",\"argv\":[\"flash\",\"boot\"]}",
            "{\"ts\":\"2026-01-01T00:00:01.000Z\",\"pid\":4242,\"phase\":\"result\",\"op\":\"fastboot.flash\",\"tool\":\"fastboot\",\"exitCode\":0,\"ok\":true,\"verdictSource\":\"exit-code\"}",
        });

        // Pre-2.4.15 flat VCOM transcript.
        string legacyTrace = Path.Combine(logDir, "vcom-20260101T000000Z-hisi990.log");
        File.WriteAllLines(legacyTrace, new[]
        {
            "2026-01-01T00:00:00.000Z pid=4242 seq=1 event=session.open tool=Kirin-Tool version=2.4.14.0",
            "2026-01-01T00:00:01.000Z pid=4242 seq=2 event=frame.write type=DATA addr=0x00100000 len=1034 seq=3 bytes=1039",
            "2026-01-01T00:00:02.000Z pid=4242 seq=3 event=frame.ack type=DATA result=ok raw=0xAA",
        });

        IReadOnlyList<LogSessionDescriptor> listed = LogSessionStore.List(logDir);
        LogSessionDescriptor legacyJsonlDescriptor = listed.FirstOrDefault(d => d.Kind == "legacy-jsonl")!;
        LogSessionDescriptor legacyTextDescriptor = listed.FirstOrDefault(d => d.Kind == "legacy-vcom-text")!;
        Check(legacyJsonlDescriptor != null, "logs list still finds the pre-2.4.15 <category>/*.jsonl files");
        Check(legacyJsonlDescriptor.Legacy, "the legacy JSONL is flagged legacy");
        Check(legacyJsonlDescriptor.Op == "fastboot.flash" && legacyJsonlDescriptor.Ok == true, "the legacy JSONL keeps its op and verdict");
        Check(legacyTextDescriptor != null && legacyTextDescriptor.Legacy, "logs list still finds the pre-2.4.15 vcom-*.log transcript");

        string legacyShown = LogSessionStore.Show(legacyJsonlDescriptor, includeTrace: false);
        using (JsonDocument document = JsonDocument.Parse(legacyShown))
        {
            Check(document.RootElement.GetProperty("session").GetProperty("kind").GetString() == "legacy-jsonl", "logs show reports the legacy kind");
            Check(document.RootElement.GetProperty("events").GetArrayLength() == 2, "logs show replays every legacy line as an event");
            Check(document.RootElement.GetProperty("events")[1].GetProperty("ok").GetBoolean(), "the legacy verdict is visible through the merged shape");
        }

        string textShown = LogSessionStore.Show(legacyTextDescriptor, includeTrace: true);
        using (JsonDocument document = JsonDocument.Parse(textShown))
        {
            JsonElement events = document.RootElement.GetProperty("events");
            Check(events.GetArrayLength() == 3, "the legacy text transcript is parsed into events");
            Check(events[0].GetProperty("phase").GetString() == "start", "event=session.open maps to phase=start");
            Check(events[1].GetProperty("step").GetString() == "frame.write", "event=frame.write maps to step=frame.write");
            Check(events[1].TryGetProperty("addr", out JsonElement legacyAddr) && legacyAddr.GetString() == "0x00100000", "the legacy frame fields stay readable (addr)");
            Check(document.RootElement.GetProperty("trace").GetArrayLength() == 3, "logs show --trace keeps the raw transcript lines");
        }
    }

    // ---------- 3. the logs CLI: list / show / path, all JSON ----------
    {
        (int code, string stdout, string stderr) = RunCli("logs", "list", "--json");
        Check(code == 0, "logs list exits 0 (stderr: " + stderr.Trim() + ")");
        using (JsonDocument document = JsonDocument.Parse(stdout))
        {
            JsonElement body = document.RootElement;
            Check(body.GetProperty("root").GetString() == logDir, "logs list --json reports the log root");
            Check(body.GetProperty("count").GetInt32() >= 3, "logs list --json counts sessions and legacy files");
            Check(body.GetProperty("legacy").GetInt32() >= 2, "logs list --json counts the legacy files separately");

            JsonElement item = body.GetProperty("items").EnumerateArray()
                .First(e => e.GetProperty("sessionId").GetString() == sessionId);
            foreach (string field in new[] { "sessionId", "kind", "legacy", "directory", "mainLog", "op", "tool", "startedUtc", "pid", "bytes", "eventCount", "ok" })
                Check(item.TryGetProperty(field, out _), "logs list item carries " + field);
            Check(item.GetProperty("mainLog").GetString()!.EndsWith(OperationSession.MainLogFileName), "the list points at session.jsonl");
            Check(item.GetProperty("sessionId").GetString() == sessionId, "the newest session is listed with its id");
        }

        (code, stdout, stderr) = RunCli("logs", "show", "latest", "--json");
        Check(code == 0, "logs show latest exits 0 (stderr: " + stderr.Trim() + ")");
        using (JsonDocument document = JsonDocument.Parse(stdout))
        {
            JsonElement session = document.RootElement.GetProperty("session");
            Check(session.GetProperty("kind").GetString() == "session", "logs show latest resolves to a merged session");
            Check(document.RootElement.GetProperty("summary").ValueKind == JsonValueKind.Object, "logs show returns the summary object");
            Check(document.RootElement.GetProperty("events").GetArrayLength() > 3, "logs show returns the whole timeline");
        }

        (code, stdout, _) = RunCli("logs", "path", sessionId);
        Check(code == 0, "logs path <session> exits 0");
        using (JsonDocument document = JsonDocument.Parse(stdout))
        {
            JsonElement body = document.RootElement;
            Check(body.GetProperty("sessionId").GetString() == sessionId, "logs path echoes the session id");
            Check(body.GetProperty("dir").GetString() == sessionDirectory, "logs path returns the session directory");
            Check(body.GetProperty("mainLog").GetString() == Path.Combine(sessionDirectory, OperationSession.MainLogFileName), "logs path returns the main log");
            Check(body.GetProperty("summary").GetString() == Path.Combine(sessionDirectory, OperationSession.SummaryFileName), "logs path returns the summary");
        }

        (code, stdout, _) = RunCli("logs", "path", Path.Combine(logDir, "vcom-20260101T000000Z-hisi990.log"));
        Check(code == 0, "logs path accepts a legacy file path (migration is a no-op)");
        using (JsonDocument document = JsonDocument.Parse(stdout))
            Check(document.RootElement.GetProperty("legacy").GetBoolean(), "the legacy file is reported as legacy");

        (code, _, _) = RunCli("logs", "show", "no-such-session-anywhere");
        Check(code == 5, "logs show with an unknown selector exits 5 (file/verify), not 0");

        (code, _, _) = RunCli("logs", "frobnicate");
        Check(code == 1, "an unknown logs verb is a usage error (exit 1)");
    }

    // ---------- 2c. the MCP tool is the same read-only command ----------
    {
        bool dll = cli.EndsWith(".dll", StringComparison.Ordinal);
        var psi = new ProcessStartInfo(dll ? "dotnet" : cli)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        if (dll) psi.ArgumentList.Add(cli);
        psi.ArgumentList.Add("mcp");
        psi.Environment["KIRIN_LOG_DIR"] = logDir;
        using Process process = Process.Start(psi)!;
        process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
        process.StandardInput.Close();
        string stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        JsonElement logsTool = default;
        bool found = false;
        foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
            if (node?["result"]?["tools"] is not JsonArray tools) continue;
            foreach (JsonNode? tool in tools)
            {
                if (tool?["name"]?.GetValue<string>() == "logs")
                {
                    logsTool = JsonDocument.Parse(tool.ToJsonString()).RootElement.Clone();
                    found = true;
                }
            }
        }
        Check(found, "MCP exposes a logs tool (read-only)");
        Check(logsTool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean(), "the logs tool is marked read-only");
        bool requiresConfirm = logsTool.GetProperty("inputSchema").TryGetProperty("required", out JsonElement required) &&
            required.ValueKind == JsonValueKind.Array &&
            required.EnumerateArray().Any(value => value.GetString() == "confirm");
        Check(!requiresConfirm, "the logs tool needs no confirm");
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

Console.WriteLine(checks + " log-merge checks passed; no device, fastboot or adb was contacted.");
