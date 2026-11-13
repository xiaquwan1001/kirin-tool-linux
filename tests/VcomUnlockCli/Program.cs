// Hardware-free parity harness for the CLI/MCP "vcom unlock" path.
//
// It stands a fake BootROM behind a real pty (`posix_openpt`), points the CLI at it with
// `--port`, and asserts the wire frames, the ACK policy mapping, the stage split (a
// `--stage-only rest` run must never re-send START) and the session log format.
// Nothing here touches a real device: no adb, no fastboot, no /dev/ttyUSB*, no 12d1:3609.
//
// Run from the repository root:  dotnet run --project tests/VcomUnlockCli -c Release

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// Always drive the newest build: a stale Release binary from an older checkout would make
// this harness assert yesterday's behaviour.
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

string loaderRoot = Path.Combine(root, "loaders");
if (!Directory.Exists(Path.Combine(loaderRoot, "hisi990"))) throw new Exception("FAIL bundled loaders/hisi990 missing");

string work = Path.Combine(Path.GetTempPath(), "kirin-vcomunlock-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
string logDir = Path.Combine(work, "logs");

(int Code, string Out, string Err) Run(params string[] argv)
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
    // Both pipes must be drained concurrently: the per-frame stderr trace easily exceeds the
    // 64 KiB pipe buffer, and a sequential ReadToEnd() would deadlock the child.
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
    {
        try { process.Kill(true); } catch { }
        throw new TimeoutException("CLI did not finish within 5 minutes");
    }
    return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
}

string Tail(string text, int lines = 6)
{
    string[] parts = text.Trim().Split('\n');
    return string.Join(" | ", parts.Skip(Math.Max(0, parts.Length - lines)));
}

JsonDocument ParseJson(string stdout)
{
    string text = stdout.Trim();
    int start = text.IndexOf('{');
    if (start < 0) throw new Exception("FAIL no JSON document on stdout: " + text);
    return JsonDocument.Parse(text[start..]);
}

try
{
    // ---------- 1. parameter parsing, gate and dry-run ----------
    {
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--json");
        Check(code == 4, $"plan-only without --yes exits 4 (got {code}); stderr: {stderr.Trim()}");
        using JsonDocument doc = ParseJson(stdout);
        Check(doc.RootElement.GetProperty("gate").GetString() == "plan-only", "plan-only gate string");
        Check(!doc.RootElement.GetProperty("applied").GetBoolean(), "plan-only: applied=false");
        Check(doc.RootElement.GetProperty("plan").GetProperty("stages").GetArrayLength() == 5, "plan lists the 5 hisi990 stages");
        // CLI/MCP default is strict since v2.4.15 (docs/ack-default.md); the GUI keeps lenient.
        Check(doc.RootElement.GetProperty("plan").GetProperty("ackMode").GetString() == "strict", "plan defaults to the CLI/MCP ACK policy (strict)");

        (code, stdout, _) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--dry-run", "--json");
        Check(code == 4, $"--dry-run with --yes still exits 4 (got {code})");
        using JsonDocument dry = ParseJson(stdout);
        Check(dry.RootElement.GetProperty("gate").GetString() == "plan-only", "--dry-run gate=plan-only");
        Check(dry.RootElement.GetProperty("confirmed").GetBoolean(), "--dry-run reports the acknowledgement");

        (code, _, _) = Run("vcom-unlock", "--json");
        Check(code == 1, $"missing --cpu exits 1 (got {code})");
        (code, _, _) = Run("vcom-unlock", "--cpu", "hisi999", "--json");
        Check(code == 1, $"unsupported chip exits 1 (got {code})");
        (code, _, _) = Run("vcom-unlock", "--cpu", "hisi990", "--ack-mode", "maybe", "--json");
        Check(code == 1, $"bad --ack-mode exits 1 (got {code})");
        (code, _, _) = Run("vcom-unlock", "--cpu", "hisi990", "--stage-only", "half", "--json");
        Check(code == 1, $"bad --stage-only exits 1 (got {code})");
        (code, _, _) = Run("vcom-unlock", "--cpu", "hisi990", "--replug", "sometimes", "--json");
        Check(code == 1, $"bad --replug exits 1 (got {code})");

        // A confirmed run with no port at all must fail as "no device" (2), never as a write.
        (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--stage-only", "rest", "--port", Path.Combine(work, "no-such-tty"), "--loader-dir", loaderRoot);
        Check(code == 2, $"missing explicit port exits 2 (got {code}); stderr: {stderr.Trim()}");
        using JsonDocument noDevice = ParseJson(stdout);
        Check(noDevice.RootElement.GetProperty("gate").GetString() == "failed", "missing port: gate=failed");
        Check(noDevice.RootElement.GetProperty("failureKind").GetString() == "no-device", "missing port: failureKind=no-device");

        // Local file problem (empty loader directory) is exit 5, not a device failure.
        string emptyLoaders = Path.Combine(work, "empty-loaders");
        Directory.CreateDirectory(Path.Combine(emptyLoaders, "hisi990"));
        (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--port", Path.Combine(work, "no-such-tty"), "--loader-dir", emptyLoaders, "--stage-only", "null");
        Check(code == 5, $"empty loader directory exits 5 (got {code}); stderr: {stderr.Trim()}");
        using JsonDocument missingLoaders = ParseJson(stdout);
        Check(missingLoaders.RootElement.GetProperty("failureKind").GetString() == "local-file", "empty loader dir: failureKind=local-file");
    }

    // ---------- 2. ACK policy mapping (strict / lenient / off) ----------
    {
        // DEFAULT policy (no --ack-mode at all): strict since v2.4.15 (docs/ack-default.md).
        // An automated caller must not be able to read exit 0 out of a device that never answered.
        (int defCode, string defOut, string defErr) = RunFake("silent", "hisi990",
            "--ack-timeout", "1", "--stage-only", "null", "--yes");
        Check(defCode == 3, $"the default ACK policy fails on a silent bootrom (got {defCode}); stderr: {defErr.Trim()}");
        using (JsonDocument doc = ParseJson(defOut))
        {
            Check(doc.RootElement.GetProperty("ackMode").GetString() == "strict", "the default ackMode is strict (CLI/MCP)");
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "default (strict) + silence: verified=false");
            Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "default (strict) + silence: gate=failed");
            Check(doc.RootElement.GetProperty("failureKind").GetString() == "device-rejected", "default (strict) + silence: failureKind=device-rejected");
            Check(doc.RootElement.GetProperty("ack").GetProperty("timeouts").GetInt32() > 0, "default (strict) + silence: timeouts counted");
        }

        // strict + a fake that answers 0xAA -> success
        (int code, string stdout, string stderr) = RunFake("ack-ok", "hisi990", "--ack-mode", "strict",
            "--ack-timeout", "2", "--stage-only", "null", "--yes");
        Check(code == 0, $"strict ACK against a 0xAA bootrom succeeds (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "strict success: gate=applied");
            Check(doc.RootElement.GetProperty("ack").GetProperty("ok").GetInt32() > 0, "strict success: ACKs counted");
            Check(doc.RootElement.GetProperty("ack").GetProperty("timeouts").GetInt32() == 0, "strict success: no ACK timeouts");
        }

        // strict + silence -> device rejection (3)
        (code, stdout, stderr) = RunFake("silent", "hisi990", "--ack-mode", "strict",
            "--ack-timeout", "1", "--stage-only", "null", "--yes");
        Check(code == 3, $"strict ACK against a silent bootrom fails with 3 (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(doc.RootElement.GetProperty("failureKind").GetString() == "device-rejected", "strict silence: failureKind=device-rejected");
            Check(doc.RootElement.GetProperty("ack").GetProperty("timeouts").GetInt32() > 0, "strict silence: timeouts counted");
        }

        // strict + 0x55 -> device rejection (3)
        (code, stdout, stderr) = RunFake("bad-ack", "hisi990", "--ack-mode", "strict",
            "--ack-timeout", "1", "--stage-only", "null", "--yes");
        Check(code == 3, $"strict ACK with a 0x55 answer fails with 3 (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
            Check(doc.RootElement.GetProperty("ack").GetProperty("bad").GetInt32() > 0, "strict non-ACK: bad ACKs counted");

        // Explicit --ack-mode lenient (the GUI policy) + silence -> exit 0 but an honest
        // verdict: the upload happened, the device never confirmed it (v2.4.14 behaviour kept,
        // only the default moved to strict).
        (code, stdout, stderr) = RunFake("silent", "hisi990", "--ack-mode", "lenient",
            "--ack-timeout", "1", "--stage-only", "null", "--yes");
        Check(code == 0, $"explicit lenient still exits 0 through silence (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "lenient silence: verified=false (never a silent success)");
            Check(doc.RootElement.GetProperty("gate").GetString() == "applied-unverified", "lenient silence: gate=applied-unverified");
            Check(doc.RootElement.GetProperty("reason").GetString()!.StartsWith("ack_missing:"), "lenient silence: reason=ack_missing:<n>");
            Check(doc.RootElement.GetProperty("ackMode").GetString() == "lenient", "explicit lenient is reported as such");
            Check(doc.RootElement.GetProperty("ack").GetProperty("timeouts").GetInt32() > 0, "lenient silence: timeouts recorded but not fatal");
            Check(doc.RootElement.GetProperty("stages").GetArrayLength() == 1, "lenient silence: the null stage still completed");
        }

        // off + silence -> success without waiting at all
        var watch = Stopwatch.StartNew();
        (code, stdout, stderr) = RunFake("silent", "hisi990", "--ack-mode", "off",
            "--ack-timeout", "5", "--stage-only", "null", "--yes");
        watch.Stop();
        Check(code == 0, $"ack-mode off ignores missing ACKs (got {code}); stderr: {stderr.Trim()}");
        Check(watch.ElapsedMilliseconds < 5000, $"ack-mode off never waits for an ACK ({watch.ElapsedMilliseconds} ms)");
        using (JsonDocument doc = ParseJson(stdout))
            Check(doc.RootElement.GetProperty("ack").GetProperty("expected").GetInt32() == 0, "ack-mode off: zero ACKs expected");
    }

    // ---------- 3. stage split: a "rest" run must never re-send START ----------
    {
        var fake = new FakeBootrom("ack-ok");
        try
        {
            (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
                "--ack-mode", "strict", "--ack-timeout", "2", "--stage-only", "null", "--no-frame-log",
                "--port", fake.SlavePath, "--loader-dir", loaderRoot);
            Check(code == 0, $"--stage-only null succeeds (got {code}); stderr: {stderr.Trim()}");
            List<Frame> firstRun = fake.Snapshot();
            Check(firstRun.Count > 0 && firstRun[0].Type == "START", $"null run starts with the START frame (got {firstRun.FirstOrDefault()?.Type})");
            Check(firstRun.Any(frame => frame.Type == "HEAD" && frame.Address == 0x22000), "null run uploads the null stage head at 0x22000");
            Check(!firstRun.Any(frame => frame.Type == "HEAD" && frame.Address == 0x60000000), "null run never reaches the UCE stage");

            fake.Reset();
            (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
                "--ack-mode", "strict", "--ack-timeout", "2", "--stage-only", "rest", "--no-frame-log",
                "--port", fake.SlavePath, "--loader-dir", loaderRoot);
            Check(code == 0, $"--stage-only rest succeeds (got {code}); stderr: {stderr.Trim()}");
            List<Frame> restRun = fake.Snapshot();
            Check(restRun.Count > 0, "rest run produced frames");
            Check(!restRun.Any(frame => frame.Type == "START"), "rest run never re-sends the START frame");
            Check(restRun[0].Type == "HEAD" && restRun[0].Address == 0x22000 && restRun[0].Length > 4,
                $"rest run starts with the XLOADER payload head at 0x22000 (got {restRun[0].Type} 0x{restRun[0].Address:X8} len={restRun[0].Length})");
            Check(restRun.Any(frame => frame.Type == "HEAD" && frame.Address == 0x60000000), "rest run reaches the UCE stage");
            Check(restRun.Any(frame => frame.Type == "HEAD" && frame.Address == 0x1A400000), "rest run reaches the FASTBOOT stage");
            Check(restRun.Any(frame => frame.Type == "HEAD" && frame.Address == 0x1E400000), "rest run reaches the BL2 stage");
            using JsonDocument doc = ParseJson(stdout);
            Check(doc.RootElement.GetProperty("stageSelection").GetString() == "rest", "rest run reports stageSelection=rest");
            Check(doc.RootElement.GetProperty("stages").GetArrayLength() == 4, "rest run reports 4 stages (null excluded)");
        }
        finally { fake.Dispose(); }
    }

    // ---------- 4. full sequential run: frame order identical to the GUI's ----------
    {
        var fake = new FakeBootrom("ack-ok");
        try
        {
            var watch = Stopwatch.StartNew();
            (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
                "--ack-mode", "strict", "--ack-timeout", "5", "--stage-only", "all", "--replug", "wait:1",
                "--no-frame-log", "--port", fake.SlavePath, "--loader-dir", loaderRoot);
            watch.Stop();
            Check(code == 0, $"full run succeeds (got {code}); stderr tail: {Tail(stderr)}");
            Console.WriteLine($"      full run took {watch.ElapsedMilliseconds} ms");

            List<Frame> frames = fake.Snapshot();
            Check(frames.Count > 0 && frames[0].Type == "START", "full run starts with START");
            Check(frames[0].Bytes.SequenceEqual(new byte[] { 0xFE, 0x00, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x01, 0x1D, 0x0F }),
                "START frame bytes are the GUI constant");
            foreach (int address in new[] { 0x60000000, 0x1A400000, 0x1E400000 })
                Check(frames.Any(frame => frame.Type == "HEAD" && frame.Address == address), $"full run uploads 0x{address:X8}");

            // pwn (exploit) marker of the XLOADER stage: head(0x22000,4) -> head(0x673c8,4) -> data(seq=1,4B) -> tail(2)
            Check(frames.Any(frame => frame.Type == "HEAD" && frame.Address == 0x22000 && frame.Length == 4), "pwn: head(0x22000, len=4)");
            Check(frames.Any(frame => frame.Type == "HEAD" && frame.Address == 0x673c8 && frame.Length == 4), "pwn: head(0x673c8, len=4) [hisi990 resume address]");
            // DATA frames carry no address on the wire; the 4-byte payload after the resume
            // head is the xloader entry point (data(seq=1) of the exploit sequence).
            Check(frames.Any(frame => frame.Type == "DATA" && frame.Sequence == 1 && frame.PayloadLength == 4),
                "pwn: data(seq=1, 4-byte xloader entry)");
            Check(frames.Any(frame => frame.Type == "TAIL" && frame.Sequence == 2), "pwn: tail(seq=2)");

            int dataFrames = frames.Count(frame => frame.Type == "DATA");
            Check(dataFrames > 4000, $"full run streams every 1 KiB chunk ({dataFrames} DATA frames)");

            using JsonDocument doc = ParseJson(stdout);
            JsonElement stages = doc.RootElement.GetProperty("stages");
            Check(stages.GetArrayLength() == 5, "full run reports 5 stages");
            string[] order = stages.EnumerateArray().Select(stage => stage.GetProperty("stage").GetString()!).ToArray();
            Check(order.SequenceEqual(new[] { "null", "XLOADER", "UCE", "FASTBOOT", "BL2" }), "stage order is null, XLOADER, UCE, FASTBOOT, BL2 (got " + string.Join(",", order) + ")");
            Check(stages.EnumerateArray().All(stage => stage.GetProperty("bytes").GetInt64() > 0), "every stage reports uploaded bytes");
            Check(doc.RootElement.GetProperty("conclusion").GetString() == "unlocked-fastboot-loaded", "conclusion says the loaders were loaded");
            Check(doc.RootElement.GetProperty("ack").GetProperty("bad").GetInt32() == 0, "no bad ACKs in the full run");
        }
        finally { fake.Dispose(); }
    }

    // ---------- 5. replug discipline ----------
    {
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--port", Path.Combine(work, "absent-tty"), "--loader-dir", loaderRoot, "--stage-only", "all");
        Check(code == 4, $"a full run on hisi990 without a replug policy is refused with 4 (got {code}); stderr: {stderr.Trim()}");
        using JsonDocument doc = ParseJson(stdout);
        Check(doc.RootElement.GetProperty("gate").GetString() == "refused", "refused gate for the missing replug policy");
        Check((doc.RootElement.GetProperty("message").GetString() ?? "").Contains("replug"), "refusal explains the cable replug");
    }

    // ---------- 6. session log format (dry-run writes the same header) ----------
    {
        string[] logs = Directory.Exists(logDir) ? Directory.GetFiles(logDir, "vcom-*.log") : Array.Empty<string>();
        Check(logs.Length > 0, "vcom session log files were created");
        string[] dryRunLogs = logs.Where(path => File.ReadAllLines(path).Any(line => line.Contains("event=plan"))).ToArray();
        Check(dryRunLogs.Length > 0, "a dry run still writes a log header (event=plan)");

        string[] lines = File.ReadAllLines(dryRunLogs[0]);
        Check(lines[0].StartsWith("20", StringComparison.Ordinal) && lines[0].Contains(".", StringComparison.Ordinal),
            "log lines carry a millisecond UTC timestamp: " + lines[0]);
        Check(lines.Any(line => line.Contains("pid=")), "log lines carry the pid");
        Check(lines.Any(line => line.Contains("event=session.open")), "log records session.open");
        Check(lines.Any(line => line.Contains("event=command.start")), "log records command.start");
        Check(lines.Any(line => line.Contains("event=session.close")), "log records session.close");

        string[] frames = logs.SelectMany(File.ReadAllLines).Where(line => line.Contains("event=frame.write")).ToArray();
        Check(frames.Length > 0, "log records frame.write lines");
        Check(frames.Any(line => line.Contains("type=START")), "log records the START frame");
        Check(frames.Any(line => line.Contains("type=HEAD") && line.Contains("addr=0x")), "log records HEAD frames with their address");
        Check(frames.Any(line => line.Contains("type=DATA") && line.Contains("seq=") && line.Contains("cumulativeBytes=")),
            "log records DATA frames with sequence and cumulative bytes");
        string[] acks = logs.SelectMany(File.ReadAllLines).Where(line => line.Contains("event=frame.ack")).ToArray();
        Check(acks.Length > 0, "log records frame.ack lines");
        Check(acks.Any(line => line.Contains("result=ok") && line.Contains("raw=0xAA")), "log records the raw ACK byte");
        string[] all = logs.SelectMany(File.ReadAllLines).ToArray();
        Check(all.Any(line => line.Contains("event=stage.start")), "log records stage.start");
        Check(all.Any(line => line.Contains("event=stage.end")), "log records stage.end");
        Check(all.Any(line => line.Contains("event=unlock.done")), "log records the conclusion");
        Check(all.Any(line => line.Contains("event=port.open")), "log records port.open with the port name");
        Check(all.Any(line => line.Contains("bulkOnly=true")), "log records the framing mode (bulkOnly)");

        if (!OperatingSystem.IsWindows())
        {
            string mode = File.GetUnixFileMode(logDir).ToString();
            Check(!File.GetUnixFileMode(logDir).HasFlag(UnixFileMode.GroupRead | UnixFileMode.OtherRead),
                $"log directory is owner-only ({mode})");
        }
    }

    Console.WriteLine($"{checks} VCOM unlock parity checks passed; no hardware accessed.");
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

// Runs the CLI against a freshly created fake BootROM pty.
(int Code, string Out, string Err) RunFake(string behaviour, string cpu, params string[] argv)
{
    using var fake = new FakeBootrom(behaviour);
    var full = new List<string>(argv) { "--port", fake.SlavePath, "--loader-dir", loaderRoot };
    return Run(new[] { "vcom-unlock", "--cpu", cpu, "--json" }.Concat(full).ToArray());
}

/// <summary>A frame as the fake BootROM saw it on the wire.</summary>
sealed record Frame(string Type, int Index, byte[] Bytes, int Address, int Length, int Sequence)
{
    public int PayloadLength => Type == "DATA" ? Bytes.Length - 5 : 0;
}

/// <summary>
/// Mock of the 12d1:3609 BootROM on a pty: parses the same frames `VcomFlasher` emits
/// (START/HEAD/DATA/TAIL) and answers HEAD/DATA/TAIL with the configured ACK byte.
/// START is not acknowledged — the client never reads an ACK for it, and answering it
/// would shift every later ACK by one byte.
/// </summary>
sealed class FakeBootrom : IDisposable
{
    private const int O_RDWR = 2;
    private const int O_NOCTTY = 0x100;

    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int ptsname_r(int fd, byte[] buffer, nint bufferLength);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, byte[] buffer, nint count);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int argument);
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int tcgetattr(int fd, IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern int tcsetattr(int fd, int actions, IntPtr termios);
    [DllImport("libc", SetLastError = true)] private static extern void cfmakeraw(IntPtr termios);

    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    private readonly int _master;
    private readonly int _slaveKeepAlive;
    private readonly byte? _ack;
    private readonly Thread _thread;
    private readonly List<Frame> _frames = new();
    private readonly object _gate = new();
    private volatile bool _stopping;

    public FakeBootrom(string behaviour)
    {
        _ack = behaviour switch
        {
            "ack-ok" => (byte)0xAA,
            "bad-ack" => (byte)0x55,
            "silent" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(behaviour)),
        };

        _master = posix_openpt(O_RDWR | O_NOCTTY);
        if (_master < 0) throw new InvalidOperationException("posix_openpt failed: " + Marshal.GetLastPInvokeError());
        // The child must not inherit the master side (that would hide EOF/hangup handling).
        fcntl(_master, F_SETFD, FD_CLOEXEC);
        if (grantpt(_master) != 0) throw new InvalidOperationException("grantpt failed");
        if (unlockpt(_master) != 0) throw new InvalidOperationException("unlockpt failed");

        var nameBuffer = new byte[256];
        if (ptsname_r(_master, nameBuffer, nameBuffer.Length) != 0) throw new InvalidOperationException("ptsname_r failed");
        SlavePath = Encoding.ASCII.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0)).TrimEnd('\0');

        // Put the slave in raw 8-bit mode so no line discipline mangles the frames.
        int slave = open(SlavePath, O_RDWR | O_NOCTTY);
        if (slave >= 0)
        {
            IntPtr termios = Marshal.AllocHGlobal(256);
            try
            {
                if (tcgetattr(slave, termios) == 0)
                {
                    cfmakeraw(termios);
                    tcsetattr(slave, 0, termios);
                }
            }
            finally { Marshal.FreeHGlobal(termios); }
        }
        // Keep the raw-mode slave open for the whole life of the fake, exactly like the
        // reference pty mocks do: closing every slave fd resets the line discipline state
        // and the next opener sees canonical mode again.
        _slaveKeepAlive = slave;

        _thread = new Thread(Serve) { IsBackground = true, Name = "fake-bootrom" };
        _thread.Start();
    }

    public string SlavePath { get; }

    public List<Frame> Snapshot()
    {
        lock (_gate) return _frames.ToList();
    }

    public void Reset()
    {
        lock (_gate) _frames.Clear();
    }

    private void Serve()
    {
        var buffer = new byte[8192];
        while (!_stopping)
        {
            nint got = read(_master, buffer, buffer.Length);
            if (got <= 0) return;
            byte[] frame = buffer[..(int)got];
            if (!Parse(frame)) return;
        }
    }

    private bool Parse(byte[] frame)
    {
        int address = 0, length = 0, sequence = 0;
        string type;
        switch (frame[0])
        {
            case 0xFE:
                if (frame.Length < 14) return false;
                length = (frame[4] << 24) | (frame[5] << 16) | (frame[6] << 8) | frame[7];
                address = (frame[8] << 24) | (frame[9] << 16) | (frame[10] << 8) | frame[11];
                type = frame.SequenceEqual(new byte[] { 0xFE, 0x00, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x01, 0x1D, 0x0F })
                    ? "START"
                    : "HEAD";
                break;
            case 0xDA:
                type = "DATA";
                sequence = frame[1];
                break;
            case 0xED:
                type = "TAIL";
                sequence = frame[1];
                break;
            default:
                Console.WriteLine("      fake bootrom: unknown opcode 0x" + frame[0].ToString("X2"));
                return false;
        }

        int index;
        lock (_gate)
        {
            index = _frames.Count;
            _frames.Add(new Frame(type, index, frame, address, length, sequence));
        }

        if (type == "START") return true;
        if (_ack is { } ack)
        {
            byte[] reply = { ack };
            nint written = write(_master, reply, 1);
            if (written != 1)
                Console.WriteLine($"      fake bootrom: ACK write failed ({written}, errno={Marshal.GetLastPInvokeError()}) for {type}");
        }
        return true;
    }

    public void Dispose()
    {
        _stopping = true;
        try { close(_master); } catch { }
        _thread.Join(TimeSpan.FromSeconds(2));
        if (_slaveKeepAlive >= 0) { try { close(_slaveKeepAlive); } catch { } }
    }
}
