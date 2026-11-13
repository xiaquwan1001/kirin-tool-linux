// Hardware-free harness for the verdict-honesty layer (docs/verdict-honesty.md).
//
// Everything here answers one question: can this repo claim a success it cannot show evidence
// for? Cases (numbering follows docs/verdict-honesty.md §8):
//   1. a fully silent bootrom               -> verified=false, and non-zero with --require-ack
//   2. exactly one frame missing an ACK     -> verified=false, reason=ack_missing:1
//   3. an incomplete loader set             -> MissingLoader, zero frames sent
//   4. --require-backup in an unwritable dir-> refusal (exit 4) instead of a silent pass
//   5. a misspelled option (--getvr)        -> usage error, exit 1 (--lenient-args still works)
//   6. the NVE SN write-back comparison     -> only an identical read-back counts as success
// Nothing here touches a real device: no adb, no fastboot, no /dev/ttyUSB*; the "device" is a
// pty held by FakeBootrom and the CLI is pointed at it with --port.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Verdict;

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
}.Where(File.Exists).OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
  ?? throw new Exception("FAIL cli binary not found; build it first: dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release");

string loaderRoot = Path.Combine(root, "loaders");
if (!Directory.Exists(Path.Combine(loaderRoot, "hisi990"))) throw new Exception("FAIL bundled loaders/hisi990 missing");

string work = Path.Combine(Path.GetTempPath(), "kirin-verdict-" + Guid.NewGuid().ToString("N"));
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
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
    {
        try { process.Kill(true); } catch { }
        throw new TimeoutException("CLI did not finish within 5 minutes");
    }
    return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
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
    // ---------- 0. the verdict arithmetic itself (no device, no process) ----------
    {
        var ok = new VcomAckCounts(FramesSent: 10, FramesExpectingAck: 8, Acked: 8, Bad: 0, Timeouts: 0, NotExpectingAck: 2);
        var verified = VcomVerdict.From(ok, VcomAckMode.Lenient);
        Check(verified.Verified && verified.Reason == "ack_ok" && verified.Gate == "applied", "all ACKs answered -> verified, gate=applied");

        var timeouts = new VcomAckCounts(10, 8, 6, 1, 1, 2);
        var unverified = VcomVerdict.From(timeouts, VcomAckMode.Lenient);
        Check(!unverified.Verified, "one bad + one timeout -> not verified");
        Check(unverified.Reason == "ack_missing:2", "reason counts every frame without a usable ACK (got " + unverified.Reason + ")");
        Check(Math.Abs(unverified.MissingRatio - 0.25) < 1e-9, "missingRatio is missing/framesExpectingAck (0.25)");
        Check(unverified.Gate == "applied-unverified" && unverified.Conclusion == "applied-unverified",
            "an unverified upload has its own gate value, not 'applied'");

        var off = VcomVerdict.From(ok, VcomAckMode.Off);
        Check(!off.Verified && off.Reason == "ack_not_requested", "ack-mode off can never be verified");

        var failed = VcomVerdict.Failed(timeouts, UnlockVerdictText.FailureReason(UnlockFailureKind.MissingLoader));
        Check(!failed.Applied && !failed.Verified && failed.Reason == "missing_loader", "a run that never started is failed/missing_loader");
        Check(failed.Gate == "failed", "a run that never started gates as failed");

        Check(UnlockVerdictText.Evidence(ok).Contains("missing 0/8"), "evidence line carries acked/missing counters");
    }

    // ---------- 0b. the ledger attributes frames to stages, not addresses ----------
    {
        var ledger = new VcomAckLedger();
        ledger.OnFrame(new VcomFrameInfo("START", 14, null, null, null, "not-expected"));
        ledger.EnterStage("null");
        ledger.OnFrame(new VcomFrameInfo("HEAD", 14, 0x22000, null, 0xAA, "ok"));
        ledger.OnFrame(new VcomFrameInfo("DATA", 1029, 0x22000, 1, 0x55, "bad"));
        ledger.OnFrame(new VcomFrameInfo("TAIL", 5, 0x22000, 2, null, "not-expected"));
        // The null and XLOADER stages share 0x22000: only EnterStage separates them.
        ledger.EnterStage("XLOADER");
        ledger.OnFrame(new VcomFrameInfo("HEAD", 14, 0x22000, null, 0xAA, "ok"));

        Check(ledger.Total.FramesSent == 5, "ledger counts every frame including START");
        Check(ledger.Total.FramesExpectingAck == 3 && ledger.Total.Acked == 2 && ledger.Total.Bad == 1,
            "ledger separates acked from bad and ignores never-acknowledged frames");
        Check(ledger.ForStage("null").Missing == 1, "null stage owns its bad ACK");
        Check(ledger.ForStage("XLOADER").Missing == 0 && ledger.ForStage("XLOADER").Acked == 1,
            "XLOADER stage is attributed by name even at the same 0x22000 address");
        Check(ledger.ForStage("(pre-stage)").Acked == 0, "START is not attributed to a loader stage");
    }

    // ---------- 1. a fully silent bootrom ----------
    {
        // 1a. DEFAULT policy: strict since v2.4.15 (docs/ack-default.md). The automated entry
        //     point (CLI/MCP) now refuses the run instead of reporting exit 0; "applied but
        //     unverified" is no longer reachable without asking for --ack-mode lenient.
        using var silentDefault = new FakeBootrom("silent");
        (int defCode, string defOut, string defErr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--ack-timeout", "1", "--stage-only", "null", "--no-frame-log",
            "--port", silentDefault.SlavePath, "--loader-dir", loaderRoot);
        Check(defCode == 3, $"the default ACK policy (strict) fails on silence (got {defCode}); stderr: {defErr.Trim()}");
        using (JsonDocument doc = ParseJson(defOut))
        {
            Check(doc.RootElement.GetProperty("ackMode").GetString() == "strict", "the default ackMode is strict");
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "default (strict) + silence: verified=false");
            Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "default (strict) + silence: gate=failed");
            Check(doc.RootElement.GetProperty("failureKind").GetString() == "device-rejected",
                "default (strict) + silence: failureKind=device-rejected");
            Check(doc.RootElement.GetProperty("ack").GetProperty("timeouts").GetInt32() > 0,
                "default (strict) + silence: the ACK evidence is still reported");
        }

        // 1a'. explicit --ack-mode lenient (= the GUI policy): honest verdict, exit 0 kept --
        //      "the frames were sent" is still true, and the v2.4.14 gate contract is frozen.
        using var silent = new FakeBootrom("silent");
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--ack-mode", "lenient", "--ack-timeout", "1", "--stage-only", "null", "--no-frame-log",
            "--port", silent.SlavePath, "--loader-dir", loaderRoot);
        Check(code == 0, $"explicit lenient + silence keeps the documented exit 0 (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "lenient + silence reports verified=false");
            Check(doc.RootElement.GetProperty("applied").GetBoolean(), "lenient + silence still reports applied=true (the frames were written)");
            Check(doc.RootElement.GetProperty("gate").GetString() == "applied-unverified", "lenient + silence gate=applied-unverified");
            Check(doc.RootElement.GetProperty("conclusion").GetString() == "loaders-uploaded-unverified",
                "conclusion says uploaded-but-unverified, never 'loaded'");
            Check(doc.RootElement.GetProperty("reason").GetString()!.StartsWith("ack_missing:"), "reason=ack_missing:<n>");
            Check(doc.RootElement.GetProperty("ack").GetProperty("missing").GetInt32() > 0, "missing counter is exposed");
            Check(doc.RootElement.GetProperty("ack").GetProperty("missingRatio").GetDouble() == 1d,
                "missingRatio=1 when nothing answered");
            Check(doc.RootElement.GetProperty("stages")[0].GetProperty("missing").GetInt32() > 0,
                "per-stage missing counter is exposed");
        }

        // 1b. --require-ack turns the same run into a failure *after* the transfer.
        //     It must be combined with an explicit --ack-mode lenient: the CLI/MCP default is
        //     strict since v2.4.15, and strict aborts the upload at the first missing ACK, so
        //     the "record everything, fail at the end" contract only exists under lenient.
        using var silent2 = new FakeBootrom("silent");
        (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json", "--require-ack",
            "--ack-mode", "lenient",
            "--ack-timeout", "1", "--stage-only", "null", "--no-frame-log",
            "--port", silent2.SlavePath, "--loader-dir", loaderRoot);
        Check(code == 3, $"--require-ack + silence exits 3 (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "--require-ack + silence: verified=false");
            Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "--require-ack + silence: gate=failed");
            Check(doc.RootElement.GetProperty("failureKind").GetString() == "ack-missing", "--require-ack + silence: failureKind=ack-missing");
            Check(doc.RootElement.GetProperty("requireAck").GetBoolean(), "the JSON records that --require-ack was in force");
        }

        // 1c. ack-mode off never reads an ACK, so it can never be verified either.
        using var silent3 = new FakeBootrom("silent");
        (code, stdout, _) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json", "--ack-mode", "off",
            "--stage-only", "null", "--no-frame-log", "--port", silent3.SlavePath, "--loader-dir", loaderRoot);
        Check(code == 0, $"ack-mode off still completes (got {code})");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "ack-mode off: verified=false");
            Check(doc.RootElement.GetProperty("reason").GetString() == "ack_not_requested", "ack-mode off: reason=ack_not_requested");
        }
    }

    // ---------- 2. exactly one frame missing its ACK ----------
    {
        using var flaky = new FakeBootrom("one-bad");
        // Explicit lenient, for the same reason as 1b: the default (strict) would abort the
        // transfer on the first bad ACK instead of counting it and failing at the end.
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json", "--require-ack",
            "--ack-mode", "lenient",
            "--ack-timeout", "2", "--stage-only", "null", "--no-frame-log",
            "--port", flaky.SlavePath, "--loader-dir", loaderRoot);
        Check(code == 3, $"one bad ACK + --require-ack exits 3 (got {code}); stderr: {stderr.Trim()}");
        using (JsonDocument doc = ParseJson(stdout))
        {
            Check(doc.RootElement.GetProperty("reason").GetString() == "ack_missing:1", "exactly one missing ACK is reported as ack_missing:1");
            Check(doc.RootElement.GetProperty("ack").GetProperty("bad").GetInt32() == 1, "the bad ACK is counted once");
            double ratio = doc.RootElement.GetProperty("ack").GetProperty("missingRatio").GetDouble();
            Check(ratio > 0 && ratio < 1, "missingRatio is a ratio, not a boolean (" + ratio + ")");
            Check(!doc.RootElement.GetProperty("verified").GetBoolean(), "one missing ACK is enough to be unverified");
        }
    }

    // ---------- 3. an incomplete loader set sends nothing ----------
    {
        string partial = Path.Combine(work, "partial-loaders");
        Directory.CreateDirectory(Path.Combine(partial, "hisi990"));
        foreach (string file in Directory.GetFiles(Path.Combine(loaderRoot, "hisi990")))
        {
            string name = Path.GetFileName(file);
            if (name == "uce.ktl") continue;            // the stage that must be missing
            File.Copy(file, Path.Combine(partial, "hisi990", name));
        }
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--stage-only", "rest", "--no-frame-log", "--loader-dir", partial);
        Check(code == 5, $"a missing loader exits 5 (got {code}); stderr: {stderr.Trim()}");
        using JsonDocument doc = ParseJson(stdout);
        Check(doc.RootElement.GetProperty("failureKind").GetString() == "missing-loader", "failureKind=missing-loader");
        Check(doc.RootElement.GetProperty("framesSent").GetInt32() == 0, "no frame was sent (framesSent=0)");
        Check(doc.RootElement.GetProperty("reason").GetString() == "missing_loader", "reason=missing_loader");
        Check(doc.RootElement.GetProperty("stages").EnumerateArray()
                .Any(stage => stage.GetProperty("status").GetString() == "missing"),
            "the missing stage is reported as 'missing', not as 'skipped'");
        Check(doc.RootElement.GetProperty("stages").EnumerateArray()
                .All(stage => stage.GetProperty("status").GetString() != "skipped"),
            "no stage is reported as 'skipped' any more");
        Check(stderr.Contains("uce.ktl"), "the failure names the missing file: " + stderr.Trim().Split('\n')[0]);

        // A loader that exists but does not decrypt must be refused the same way.
        string broken = Path.Combine(work, "broken-loaders");
        Directory.CreateDirectory(Path.Combine(broken, "hisi990"));
        foreach (string file in Directory.GetFiles(Path.Combine(loaderRoot, "hisi990")))
            File.Copy(file, Path.Combine(broken, "hisi990", Path.GetFileName(file)));
        File.WriteAllText(Path.Combine(broken, "hisi990", "uce.ktl"), "not a ktl payload");
        (code, stdout, _) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--json",
            "--stage-only", "rest", "--no-frame-log", "--loader-dir", broken);
        Check(code == 5, $"an undecryptable loader exits 5 (got {code})");
        using (JsonDocument bad = ParseJson(stdout))
        {
            Check(bad.RootElement.GetProperty("failureKind").GetString() == "missing-loader", "undecryptable loader: failureKind=missing-loader");
            Check(bad.RootElement.GetProperty("framesSent").GetInt32() == 0, "undecryptable loader: no frame was sent");
        }
    }

    // ---------- 4. --require-backup refuses instead of silently passing ----------
    {
        var flaky = new PreOpBackup((name, path) => Task.FromResult((true, "ok")));
        string unwritable = Path.Combine("/proc", "kirin-tool-verdict-" + Guid.NewGuid().ToString("N"));
        PreOpBackupResult noDir = await flaky.RunAsync(new PreOpBackupOptions
        {
            Trigger = "verdict-test",
            WritePartitions = new[] { "oeminfo" },
            BackupDir = unwritable,
        });
        Check(!noDir.Attempted, "an uncreatable backup directory is reported as not attempted");
        Check(noDir.Error != null && noDir.Error.Contains("cannot create backup directory"),
            "the reason says the directory could not be created: " + noDir.Error);

        BackupGateVerdict refusal = BackupGate.Evaluate(requireBackup: true, noDir);
        Check(refusal.Refuse, "--require-backup refuses when the backup was never taken");
        Check(refusal.ExitCode == 4, "the refusal carries the gate exit code 4");
        Check(refusal.Reason.Contains("never taken"), "the refusal explains that the backup was never taken: " + refusal.Reason);

        BackupGateVerdict lenient = BackupGate.Evaluate(requireBackup: false, noDir);
        Check(!lenient.Refuse, "without --require-backup the same condition stays a warning (unchanged behaviour)");

        var complete = new PreOpBackupResult(true, true, "/tmp/x", "/tmp/x/manifest.json",
            new[] { new PreOpBackupPartitionResult("ptable", "ptable.img", 1, "00", "ok", null) }, null);
        Check(!BackupGate.Evaluate(requireBackup: true, complete).Refuse, "--require-backup passes a complete backup");

        var partial = new PreOpBackupResult(true, false, "/tmp/x", "/tmp/x/manifest.json",
            new[] { new PreOpBackupPartitionResult("ptable", "ptable.img", 1, "00", "ok", null),
                    new PreOpBackupPartitionResult("oeminfo", null, null, null, "failed", "loader refused") }, null);
        BackupGateVerdict partialRefusal = BackupGate.Evaluate(requireBackup: true, partial);
        Check(partialRefusal.Refuse && partialRefusal.Reason.Contains("incomplete"), "--require-backup refuses an incomplete backup");

        // e2e: --no-backup and --require-backup contradict each other and cannot be silently ignored.
        string xml = Path.Combine(work, "flash.xml");
        File.WriteAllText(xml, "<root><partitions><partition name=\"boot\" file=\"boot.img\"/></partitions></root>");
        (int code, string _, string stderr) = Run("flash-xml", xml, "--yes", "--json", "--no-backup", "--require-backup");
        Check(code == 1, $"contradictory backup flags exit 1 (got {code}); stderr: {stderr.Trim()}");
        Check(stderr.Contains("cannot be combined"), "the contradiction is explained: " + stderr.Trim().Split('\n')[0]);
    }

    // ---------- 5. a misspelled option is an error, not a flag ----------
    {
        (int code, string _, string stderr) = Run("getvar", "--getvr", "product");
        Check(code == 1, $"getvar --getvr exits 1 (got {code}); stderr: {stderr.Trim()}");
        Check(stderr.Contains("unknown option --getvr"), "the error names the unknown option: " + stderr.Trim().Split('\n')[0]);

        (code, _, stderr) = Run("info", "--jso");
        Check(code == 1, $"info --jso exits 1 (got {code})");
        Check(stderr.Contains("unknown option --jso"), "a misspelled global flag is reported too");

        // A misspelled gate flag must not be able to disable a gate silently.
        (code, _, stderr) = Run("flash-xml", "whatever.xml", "--yes", "--require-backu");
        Check(code == 1, $"flash-xml --require-backu (typo) exits 1 (got {code})");
        Check(stderr.Contains("unknown option --require-backu"), "the typo that used to disable the gate is refused");

        // --lenient-args keeps the old behaviour available, with a warning.
        (code, _, stderr) = Run("info", "--lenient-args", "--jso");
        Check(code != 1, $"--lenient-args still accepts an unknown option (got {code})");
        Check(stderr.Contains("--lenient-args"), "lenient mode warns about the option it accepted: " + stderr.Trim().Split('\n')[0]);

        // --help wins over a misspelled option.
        (code, _, _) = Run("vcom-unlock", "--cpu", "hisi990", "--hel");
        Check(code == 1, $"a typo'd --help is still an unknown option (got {code})");
        (code, string help, _) = Run("vcom-unlock", "--help", "--cpu", "hisi990");
        Check(code == 0 && help.Contains("--require-ack"), "--help documents the new --require-ack switch");
    }

    // ---------- 6. the NVE serial-number write only succeeds on an identical read-back ----------
    {
        NveWriteVerdict same = NveWriteCheck.Compare("SN1234567890", "SN1234567890", true);
        Check(same.Verified && same.Reason == NveWriteCheck.ReadbackOk, "an identical read-back is the only success path");

        NveWriteVerdict stale = NveWriteCheck.Compare("SN1234567890", "SN0000000001", true);
        Check(!stale.Verified, "a zero exit code alone does not confirm the write");
        Check(stale.Reason == "readback_mismatch:SN0000000001", "the mismatch reason carries the value read back");
        Check(stale.Explain.Contains("did NOT take effect"), "the message says the write did not take effect: " + stale.Explain);
        Check(stale.ReadBackDisplay == "SN0000000001", "the read-back value is shown to the user");

        NveWriteVerdict failed = NveWriteCheck.Compare("SN1", "SN1", false);
        Check(!failed.Verified && failed.Reason == NveWriteCheck.CommandFailed, "a failed write command is never verified");

        NveWriteVerdict unreadable = NveWriteCheck.Compare("SN1", null, true, "no device");
        Check(!unreadable.Verified && unreadable.Reason == NveWriteCheck.ReadbackUnavailable,
            "an unreadable value is 'not confirmed', never 'matches'");
        Check(unreadable.ReadBackDisplay == "(no value read back)", "an absent read-back is displayed as such");

        NveWriteVerdict empty = NveWriteCheck.Compare("SN1", "   ", true);
        Check(!empty.Verified, "an empty read-back is not a match");
    }

    Console.WriteLine($"{checks} verdict-honesty checks passed; no hardware accessed.");
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

/// <summary>
/// Mock 12d1:3609 BootROM on a pty. <c>ack-ok</c> answers 0xAA, <c>one-bad</c> answers 0x55 for
/// the second acknowledged frame and 0xAA afterwards, <c>silent</c> never answers. START, TAIL and
/// the exploit HEAD are not acknowledged by the client, so only HEAD/DATA get an answer.
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
    private readonly string _behaviour;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private int _answered;
    private volatile bool _stopping;

    public FakeBootrom(string behaviour)
    {
        _behaviour = behaviour;
        _master = posix_openpt(O_RDWR | O_NOCTTY);
        if (_master < 0) throw new InvalidOperationException("posix_openpt failed: " + Marshal.GetLastPInvokeError());
        fcntl(_master, F_SETFD, FD_CLOEXEC);
        if (grantpt(_master) != 0) throw new InvalidOperationException("grantpt failed");
        if (unlockpt(_master) != 0) throw new InvalidOperationException("unlockpt failed");

        var nameBuffer = new byte[256];
        if (ptsname_r(_master, nameBuffer, nameBuffer.Length) != 0) throw new InvalidOperationException("ptsname_r failed");
        SlavePath = Encoding.ASCII.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0)).TrimEnd('\0');

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
        _slaveKeepAlive = slave;

        _thread = new Thread(Serve) { IsBackground = true, Name = "fake-bootrom" };
        _thread.Start();
    }

    public string SlavePath { get; }

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
        bool expectsAck;
        switch (frame[0])
        {
            case 0xFE:
                if (frame.Length < 14) return false;
                // START (the fixed 14-byte frame) is never acknowledged by the client.
                expectsAck = !frame.SequenceEqual(new byte[] { 0xFE, 0x00, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x02, 0x01, 0x1D, 0x0F });
                break;
            case 0xDA:
                expectsAck = true;
                break;
            case 0xED:
                // ExtractMode TAIL: the client does not wait for an ACK, so answering would leave
                // a stale byte in the pty.
                expectsAck = false;
                break;
            default:
                return false;
        }

        if (!expectsAck) return true;

        int index;
        lock (_gate) index = _answered++;

        byte? answer = _behaviour switch
        {
            "ack-ok" => (byte)0xAA,
            "one-bad" => index == 1 ? (byte)0x55 : (byte)0xAA,
            "silent" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(_behaviour)),
        };
        if (answer is { } value)
        {
            byte[] reply = { value };
            write(_master, reply, 1);
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
