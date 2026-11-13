// Hardware-free gate checks for the write commands: even a fully acknowledged plan
// (--yes / --json) must exit 4 with applied=false, and flash-xml must refuse a
// package with missing images (exit 5) before the redline gate is even reached.
//
// Run from the repository root:  dotnet run --project tests/WriteGates -c Release
//
// Unlike tests/FlashPolicy a missing CLI binary is a hard failure here: this
// harness is meaningless without it and CI always builds KirinTool.Cli first.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kirin_Tool.Services.Approval;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

// Newest build wins: a stale Release binary from an older checkout would assert old behaviour.
string? cli = new[] {
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
}.Where(File.Exists).OrderByDescending(path => File.GetLastWriteTimeUtc(path)).FirstOrDefault()
  ?? throw new Exception(
    "FAIL cli binary not found; build it first: dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release");

// Extra environment handed to the CLI process: the stub "fastboot" (KIRIN_FASTBOOT) and the
// behaviour the stub should imitate (STUB_MODE). Set per case, cleared after it.
var stubEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);

(int Code, string Out, string Err) Run(params string[] argv)
{
    bool dll = cli.EndsWith(".dll", StringComparison.Ordinal);
    var psi = new ProcessStartInfo(dll ? "dotnet" : cli)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = root,
    };
    if (dll) psi.ArgumentList.Add(cli);
    foreach (string a in argv) psi.ArgumentList.Add(a);
    foreach (var pair in stubEnvironment) psi.Environment[pair.Key] = pair.Value;
    using Process process = Process.Start(psi)!;
    string stdout = process.StandardOutput.ReadToEnd();
    string stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
}

// Assert the shared "confirmed but nothing written" invariant of the gated write
// commands: exit 4, applied=false, and a machine-readable gate string that says
// the write path did not run ("plan-only" before --yes, "confirmed" after it —
// the write path itself is not implemented in this build).
void CheckGatedPlan(string json, string label)
{
    using JsonDocument doc = JsonDocument.Parse(json);
    Check(doc.RootElement.GetProperty("exitCode").GetInt32() == 4, label + ": exit 4");
    Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, label + ": applied=false");
    string gate = doc.RootElement.GetProperty("gate").GetString() ?? "";
    Check(gate is "plan-only" or "confirmed", label + ": gate is a no-write gate (got '" + gate + "')");
}

string work = Path.Combine(Path.GetTempPath(), "kirin-writegates-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    // ---- 1. vcom-unlock: plan-only without --yes, and a dry run that never touches the port ----
    // The command itself is a REAL write now (CliWriteKind.RealWrite), so the gate is asserted
    // with --dry-run: exit 4, gate=plan-only, and no device access. That keeps this harness
    // hardware-free even when a real VCOM device happens to be attached.
    {
        (int code, string stdout, string stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--json");
        Check(code == 4, "vcom-unlock without --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "vcom-unlock");

        (code, stdout, stderr) = Run("vcom-unlock", "--cpu", "hisi990", "--yes", "--dry-run", "--json");
        Check(code == 4, "vcom-unlock --dry-run exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "vcom-unlock --dry-run");
    }

    // ---- 2. oeminfo: acknowledged rewrite of a synthetic OEMINFO image ----
    // One 0x400 block: "OEM_INFO" magic, version 6, model entry id 0x5b (a target id
    // of OemInfoEditor's version-6 map) so the local in-memory conversion succeeds.
    {
        byte[] oeminfo = new byte[0x400];
        Encoding.ASCII.GetBytes("OEM_INFO").CopyTo(oeminfo, 0);
        BitConverter.GetBytes(6).CopyTo(oeminfo, 8);
        BitConverter.GetBytes(0x5b).CopyTo(oeminfo, 12);
        byte[] model = Encoding.ASCII.GetBytes("ELS-AN00");
        BitConverter.GetBytes(model.Length).CopyTo(oeminfo, 20);
        model.CopyTo(oeminfo, 0x200);
        string input = Path.Combine(work, "oeminfo.img");
        File.WriteAllBytes(input, oeminfo);

        (int code, string stdout, string stderr) = Run("oeminfo", "--model", "ELS-TN00", "--region", "cn",
            "--input", input, "--yes", "--json");
        Check(code == 4, "oeminfo --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "oeminfo");
    }

    // ---- 3. downgrade-enable: acknowledged version-field erase plan ----
    {
        (int code, string stdout, string stderr) = Run("downgrade-enable", "--yes", "--json");
        Check(code == 4, "downgrade-enable --yes exits 4 (got " + code + "); stderr: " + stderr.Trim());
        CheckGatedPlan(stdout, "downgrade-enable");
    }

    // ---- 4. flash-xml: missing images abort (exit 5) before the redline gate ----
    // Both images are missing on purpose and one identifier is redline: the run must
    // fail with the missing-image refusal, never reach the redline refusal (exit 4).
    {
        string xmlPath = Path.Combine(work, "flashing.xml");
        File.WriteAllText(xmlPath,
            "<flashing>\n  <fastbootimage>\n" +
            "    <image name=\"boot\" identifier=\"boot\">BOOT.img</image>\n" +
            "    <image name=\"oeminfo\" identifier=\"oeminfo\">OEMINFO.img</image>\n" +
            "  </fastbootimage>\n</flashing>\n");

        (int code, string stdout, string stderr) = Run("flash-xml", xmlPath, "--json");
        Check(code == 5, "flash-xml with missing images exits 5 (got " + code + ")");
        Check(stderr.Contains("missing"), "flash-xml refusal names the missing images: " + stderr.Trim());
        Check(!stderr.Contains("redline"), "flash-xml fails on missing images before the redline gate");

        using JsonDocument doc = JsonDocument.Parse(stdout);
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "flash-xml: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "refused", "flash-xml: gate=refused");
        Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("missing"), "flash-xml: refusal reason names the missing images");
        Check(doc.RootElement.GetProperty("exitCode").GetInt32() == 5, "flash-xml: JSON exitCode matches the process exit code");
        Check(doc.RootElement.GetProperty("missingImages").GetArrayLength() == 2, "flash-xml: both missing images reported");
    }
    // ---- 5. oeminfo-flash: the device write half of oeminfo needs the redline gate ----
    // The CLI `oeminfo` command stays plan-only (exit 4); writing the converted image is a
    // separate capability (D-01) and is refused without the redline override + typed phrase.
    {
        string image = Path.Combine(work, "converted-oeminfo.img");
        File.WriteAllBytes(image, new byte[0x400]);

        (int code, string stdout, string stderr) = Run("oeminfo-flash", "--file", image, "--yes", "--json");
        Check(code == 4, "oeminfo-flash without --allow-redline exits 4 (got " + code + "): " + stderr.Trim());
        using JsonDocument doc = JsonDocument.Parse(stdout);
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "oeminfo-flash refusal: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "refused", "oeminfo-flash refusal: gate=refused");
        Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("redline"), "oeminfo-flash refusal: reason names the redline rule");

        (code, stdout, stderr) = Run("oeminfo-flash", "--file", image, "--yes", "--allow-redline", "--json");
        Check(code == 4, "oeminfo-flash with a non-interactive stdin still exits 4 (got " + code + ")");
        Check(stderr.Contains("confirm phrase"), "oeminfo-flash refuses because the typed phrase cannot be given here");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

// ---------------------------------------------------------------- 5. D-02: the JSON verdict
// The stub "fastboot" answers what the case needs, so the assertions are about the code under
// test and not about a physical device: a failing loader must never produce applied:true.
string stubDir = Path.Combine(Path.GetTempPath(), "kirin-writegates-stub-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(stubDir);
string stub = Path.Combine(stubDir, "stub-fastboot");
File.WriteAllText(stub, """
#!/usr/bin/env python3
import os, pathlib, sys
mode = os.environ.get("STUB_MODE", "ok")
state = pathlib.Path(os.path.abspath(__file__)).with_suffix(".state")
args = sys.argv[1:]
def answer(text, code=0):
    print(text)
    sys.exit(code)
if args[:1] == ["devices"]:
    answer("TEST fastboot")
if args[:1] == ["reboot"]:
    answer("FAILED (remote: reboot not allowed)", 1) if mode == "reboot-fail" else answer("OKAY")
if args[:1] == ["flash"]:
    answer("FAILED (remote: flash write failure)", 1) if mode in ("flash-fail", "flash-all-fail") else answer("OKAY")
if args[:1] == ["oem"] and len(args) > 1 and args[1].startswith("oeminfoerase-"):
    answer("FAILED (remote: erase not allowed)", 1) if mode == "erase-fail" else answer("OKAY")
if args[:1] == ["getvar"] and len(args) > 1:
    variable = args[1]
    if variable.startswith("nve:SN@"):
        if mode == "write-fail":
            answer("FAILED (remote: variable write not allowed)", 1)
        state.write_text(variable.split("@", 1)[1])
        answer("OKAY")
    if variable == "nve:SN":
        if mode == "readback-fail":
            answer("FAILED (remote: unknown variable)", 1)
        if mode == "readback-mismatch":
            answer("(bootloader) nve:SN:OLDSERIAL7\nOKAY")
        answer("(bootloader) nve:SN:" + (state.read_text() if state.exists() else "NONE") + "\nOKAY")
answer("OKAY")
""");
if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
stubEnvironment["KIRIN_FASTBOOT"] = stub;

// ---- 5a. reboot: the device refuses the command ----
{
    stubEnvironment["STUB_MODE"] = "reboot-fail";
    (int code, string stdout, string stderr) = Run("reboot", "--yes", "--json");
    Check(code == 3, "reboot against a refusing loader exits 3 (got " + code + "): " + stderr.Trim());
    using JsonDocument doc = JsonDocument.Parse(stdout);
    Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "reboot failure: applied=false (no hardcoded success)");
    Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "reboot failure: gate=failed");
    Check(doc.RootElement.GetProperty("reason").GetString()!.Contains("FAILED"), "reboot failure: reason quotes the loader answer");
    Check(doc.RootElement.GetProperty("exitCode").GetInt32() == code, "reboot failure: JSON exitCode matches the process exit code");
}

// ---- 5b. reboot: the device accepts the command ----
{
    stubEnvironment["STUB_MODE"] = "ok";
    (int code, string stdout, string stderr) = Run("reboot", "--yes", "--json");
    Check(code == 0, "reboot against an accepting loader exits 0 (got " + code + "): " + stderr.Trim());
    using JsonDocument doc = JsonDocument.Parse(stdout);
    Check(doc.RootElement.GetProperty("applied").GetBoolean(), "reboot success: applied=true");
    Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "reboot success: gate=applied");
    Check(!doc.RootElement.TryGetProperty("reason", out JsonElement reason) || reason.ValueKind == JsonValueKind.Null,
        "reboot success: no reason");
}

// ---------------------------------------------------------------- 6. D-03: write-sn read-back
// The command is driven in-process because a redline write needs the confirm phrase typed on an
// interactive stdin: the harness installs an approving gate instead of faking a terminal. The
// device is still the stub above, so nothing here touches real hardware.
{
    var core = typeof(Kirin_Tool.Services.FastbootClient).Assembly;
    Type runnerType = core.GetType("Kirin_Tool.Cli.CliRunner")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliRunner is gone");
    var table = ((System.Collections.IEnumerable)runnerType
        .GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
        .GetValue(null)!).Cast<object>().ToList();
    object command = table.Single(candidate =>
        (string)candidate.GetType().GetProperty("Name")!.GetValue(candidate)! == "write-sn");
    MethodInfo execute = command.GetType().GetMethod("ExecuteAsync")
        ?? throw new Exception("FAIL WriteSnCommand.ExecuteAsync is gone");
    Type parserType = core.GetType("Kirin_Tool.Cli.ArgParser")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.ArgParser is gone");
    MethodInfo parse = parserType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
    Type contextType = core.GetType("Kirin_Tool.Cli.CliContext")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliContext is gone");
    ConstructorInfo contextCtor = contextType.GetConstructors()
        .Single(ctor => ctor.GetParameters().Length == 5);

    async Task<(int Code, JsonDocument Json)> RunWriteSn(string mode, string value)
    {
        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            // In-process, so the stub reads its behaviour from this process' environment.
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", stub);
            Environment.SetEnvironmentVariable("STUB_MODE", mode);
            object parsed = parse.Invoke(null, new object[]
            {
                new[] { "--value", value, "--yes", "--allow-redline", "--json" },
                new[] { "value" },
                new[] { "yes", "allow-redline", "json" },
            })!;
            var stdout = new StringWriter();
            object context = contextCtor.Invoke(new object[] { stdout, new StringWriter(), true, true, root });
            var call = (Task<int>)execute.Invoke(command, new object[] { parsed, context, CancellationToken.None })!;
            int code = await call;
            return (code, JsonDocument.Parse(stdout.ToString()));
        }
        finally
        {
            ApprovalGates.ResetToFailClosed();
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", null);
            Environment.SetEnvironmentVariable("STUB_MODE", null);
        }
    }

    // 6a. the write command itself fails
    {
        (int code, JsonDocument doc) = await RunWriteSn("write-fail", "NEWSERIAL9");
        Check(code == 3, "write-sn with a failed write exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "write-sn failed write: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "write-sn failed write: gate=failed");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn failed write: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString()!.StartsWith("command_failed", StringComparison.Ordinal),
            "write-sn failed write: reason=command_failed");
    }

    // 6b. the write is accepted but the device still reads the old value
    {
        (int code, JsonDocument doc) = await RunWriteSn("readback-mismatch", "NEWSERIAL9");
        Check(code == 3, "write-sn with a read-back mismatch exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "write-sn mismatch: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "write-sn mismatch: gate=failed");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn mismatch: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString() == "readback_mismatch:OLDSERIAL7",
            "write-sn mismatch: reason names the value the device kept (got '" + doc.RootElement.GetProperty("reason").GetString() + "')");
        Check(doc.RootElement.GetProperty("readBack").GetString() == "OLDSERIAL7", "write-sn mismatch: readBack is reported");
        Check(doc.RootElement.GetProperty("expected").GetString() == "NEWSERIAL9", "write-sn mismatch: expected is reported");
    }

    // 6c. the read-back itself is unavailable: that is not a match
    {
        (int code, JsonDocument doc) = await RunWriteSn("readback-fail", "NEWSERIAL9");
        Check(code == 3, "write-sn with an unavailable read-back exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("verified").GetBoolean() == false, "write-sn unavailable read-back: verified=false");
        Check(doc.RootElement.GetProperty("reason").GetString() == "readback_unavailable",
            "write-sn unavailable read-back: reason=readback_unavailable");
    }

    // 6d. identical read-back only
    {
        (int code, JsonDocument doc) = await RunWriteSn("ok", "NEWSERIAL9");
        Check(code == 0, "write-sn with an identical read-back exits 0 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean(), "write-sn verified: applied=true");
        Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "write-sn verified: gate=applied");
        Check(doc.RootElement.GetProperty("verified").GetBoolean(), "write-sn verified: verified=true");
        Check(doc.RootElement.GetProperty("readBack").GetString() == "NEWSERIAL9", "write-sn verified: readBack equals the written value");
    }
}
// ---------------------------------------------------------------- 7. D-13: downgrade-erase
// Enable Downgrade used to erase four oeminfo version fields straight from the GUI with no approval
// and while the registry called it PlanOnly. Two things are pinned here: the erase command reports the
// loader's answer honestly (a refused erase is never applied), and the write service refuses a ticket
// that was minted for another capability.
{
    var core = typeof(Kirin_Tool.Services.FastbootClient).Assembly;
    Type runnerType = core.GetType("Kirin_Tool.Cli.CliRunner")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliRunner is gone");
    var table = ((System.Collections.IEnumerable)runnerType
        .GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
        .GetValue(null)!).Cast<object>().ToList();
    object command = table.Single(candidate =>
        (string)candidate.GetType().GetProperty("Name")!.GetValue(candidate)! == "downgrade-erase");
    MethodInfo execute = command.GetType().GetMethod("ExecuteAsync")
        ?? throw new Exception("FAIL DowngradeEraseCommand.ExecuteAsync is gone");
    Type parserType = core.GetType("Kirin_Tool.Cli.ArgParser")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.ArgParser is gone");
    MethodInfo parse = parserType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
    Type contextType = core.GetType("Kirin_Tool.Cli.CliContext")
        ?? throw new Exception("FAIL Kirin_Tool.Cli.CliContext is gone");
    ConstructorInfo contextCtor = contextType.GetConstructors()
        .Single(ctor => ctor.GetParameters().Length == 5);

    // The erase is redline + irreversible, so its approval needs the typed phrase: the in-process
    // gate below types it exactly like a human would. Nothing here is reachable from the product.
    async Task<(int Code, JsonDocument Json)> RunDowngradeErase(string mode)
    {
        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", stub);
            Environment.SetEnvironmentVariable("STUB_MODE", mode);
            object parsed = parse.Invoke(null, new object[]
            {
                new[] { "--yes", "--allow-redline", "--json" },
                Array.Empty<string>(),
                new[] { "yes", "allow-redline", "json" },
            })!;
            var stdout = new StringWriter();
            object context = contextCtor.Invoke(new object[] { stdout, new StringWriter(), true, true, root });
            var call = (Task<int>)execute.Invoke(command, new object[] { parsed, context, CancellationToken.None })!;
            int code = await call;
            return (code, JsonDocument.Parse(stdout.ToString()));
        }
        finally
        {
            ApprovalGates.ResetToFailClosed();
            Environment.SetEnvironmentVariable("KIRIN_FASTBOOT", null);
            Environment.SetEnvironmentVariable("STUB_MODE", null);
        }
    }

    // 7a. the loader refuses every version-field erase
    {
        (int code, JsonDocument doc) = await RunDowngradeErase("erase-fail");
        Check(code == 3, "downgrade-erase against a refusing loader exits 3 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, "downgrade-erase failure: applied=false");
        Check(doc.RootElement.GetProperty("gate").GetString() == "failed", "downgrade-erase failure: gate=failed");
        JsonElement failureResult = doc.RootElement.GetProperty("result");
        Check(failureResult.GetProperty("successfulSteps").GetInt32() == 0, "downgrade-erase failure: 0/4 steps succeeded");
        Check(failureResult.GetProperty("verdictSource").GetString() == "step-outcomes:0/4-okay",
            "downgrade-erase failure: verdictSource names the rule the verdict came from");
    }

    // 7b. all four erases answer OKAY
    {
        (int code, JsonDocument doc) = await RunDowngradeErase("ok");
        Check(code == 0, "downgrade-erase against an accepting loader exits 0 (got " + code + ")");
        Check(doc.RootElement.GetProperty("applied").GetBoolean(), "downgrade-erase success: applied=true");
        Check(doc.RootElement.GetProperty("gate").GetString() == "applied", "downgrade-erase success: gate=applied");
        Check(doc.RootElement.GetProperty("result").GetProperty("successfulSteps").GetInt32() == 4,
            "downgrade-erase success: 4/4 steps succeeded");
    }

    // 7c. no ticket, or a ticket minted for another capability, never reaches the erase
    {
        var ops = new Kirin_Tool.Services.WriteOperations.DeviceWriteOps(new Kirin_Tool.Services.FastbootClient(stub));
        bool noTicket = false;
        try { await ops.EnableDowngradeAsync(null!); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("downgrade-erase", StringComparison.Ordinal)) { noTicket = true; }
        Check(noTicket, "DeviceWriteOps.EnableDowngradeAsync: without an approved ticket it throws instead of erasing");

        ApprovalGates.Install(new GuiApprovalGate(new ApproveEveryRequest()));
        try
        {
            ApprovalAdmission admission = await ApprovalGuard.AdmitAsync(
                ApprovalRequests.For("reboot", argv: new[] { "fastboot", "reboot" }, targets: Array.Empty<string>(),
                    sessionId: "downgrade-ticket-" + Guid.NewGuid().ToString("n")), CancellationToken.None);
            bool wrongTicket = false;
            try { await ops.EnableDowngradeAsync(admission.RequireTicket()); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("cannot authorize", StringComparison.Ordinal)) { wrongTicket = true; }
            Check(wrongTicket, "DeviceWriteOps.EnableDowngradeAsync: a 'reboot' ticket cannot authorize the erase");
        }
        finally { ApprovalGates.ResetToFailClosed(); }
    }
}

stubEnvironment.Clear();
try { Directory.Delete(stubDir, true); } catch { }

Console.WriteLine(checks + " write gate checks passed.");

/// <summary>
/// Test prompt: approves every request and, when the request is a redline one, types the exact
/// per-capability phrase a human would have to type. Nothing here is reachable from the product.
/// </summary>
sealed class ApproveEveryRequest : IApprovalPrompt
{
    public Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken) =>
        Task.FromResult(new ApprovalPromptAnswer(
            ApprovalPromptOutcome.Approved,
            model.RequiresTypedConfirm ? model.ConfirmPhrase : null));
}
