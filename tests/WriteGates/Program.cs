// Hardware-free gate checks for the write commands: even a fully acknowledged plan
// (--yes / --json) must exit 4 with applied=false, and flash-xml must refuse a
// package with missing images (exit 5) before the redline gate is even reached.
//
// Run from the repository root:  dotnet run --project tests/WriteGates -c Release
//
// Unlike tests/FlashPolicy a missing CLI binary is a hard failure here: this
// harness is meaningless without it and CI always builds KirinTool.Cli first.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

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
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " write gate checks passed.");
