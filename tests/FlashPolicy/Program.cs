// Hardware-free checks for the shared write policy (Utils/FlashPolicy.cs) plus a
// CLI-level assertion that a redline partition shows up in the flash-ota plan.
//
// Run from the repository root:  dotnet run --project tests/FlashPolicy -c Release

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Kirin_Tool.Utils;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }
void Skip(string name, string why) { Console.WriteLine("SKIP " + name + " - " + why); }

// ---- 1. ShouldSkip: package-descriptor metadata never reaches the writer ----
foreach (string name in new[] {
    "sha256rsa", "crc", "curver", "verlist", "package_type", "base_verlist", "base_ver",
    "cust_verlist", "cust_ver", "preload_verlist", "preload_ver", "ptable_cust", "ptable_preload" })
    Check(FlashPolicy.ShouldSkip(name), "skip " + name);
Check(FlashPolicy.ShouldSkip("HISI_SHA256RSA"), "skip is case-insensitive");
Check(FlashPolicy.ShouldSkip(" "), "blank entry is skipped (never written)");
foreach (string name in new[] { "boot", "system", "super", "vendor", "userdata" })
    Check(!FlashPolicy.ShouldSkip(name), "keep " + name);

// ---- 2. IsRedline: substring set + exact-name calibration set ----
foreach (string name in new[] {
    "oeminfo", "nvme", "modem_secure", "persist", "modemnvm_1", "certification",
    "hisee_encos", "veritykey",
    "nvm", "modemst1", "modemst2", "fsg" })
    Check(FlashPolicy.IsRedline(name), "redline " + name);
foreach (string name in new[] { "NVM", "ModemSt2", "OEMINFO", "oeminfo_bak" })
    Check(FlashPolicy.IsRedline(name), "redline is case-insensitive: " + name);
foreach (string name in new[] { "boot", "super", "userdata", "modem_fw", "modem_driver" })
    Check(!FlashPolicy.IsRedline(name), "not redline: " + name);
Check(!FlashPolicy.IsRedline(""), "blank name is not redline");
// Deliberate: the substring set is authoritative for anything derived from the nvme
// area, so a backup copy of it is still device-keyed data. Documented, not accidental.
Check(FlashPolicy.IsRedline("nvme_bak2"), "nvme_bak2 hits the nvme substring (intentional)");
// The exact-name list matches by equality only, so a longer name that merely starts
// with a calibration name is not swept in by it (only the substring tokens can match).
Check(!FlashPolicy.IsRedline("nvm_info"), "exact-name list does not substring-match nvm");

// ---- 3. RedlineHits: dedupe, first-seen order, blanks ignored ----
{
    string[] input = { "boot", "oeminfo", " persist ", "OEMINFO", "", "   ", "nvm", "modemst1", "ModemSt1", "super" };
    IReadOnlyList<string> hits = FlashPolicy.RedlineHits(input);
    Check(hits.SequenceEqual(new[] { "oeminfo", "persist", "nvm", "modemst1" }), "redline hits deduped and ordered: [" + string.Join(", ", hits) + "]");
    Check(FlashPolicy.RedlineHits(Array.Empty<string>()).Count == 0, "no hits on an empty plan");
    Check(FlashPolicy.RedlineHits(new[] { "boot", "super" }).Count == 0, "no hits on a clean plan");
}

// ---- 4. CLI level: the redline partition is visible in the flash-ota plan ----
// A synthetic UPDATE.APP (same container shape tests/Extractor writes) with one
// ordinary partition and one redline partition. No device and no fastboot involvement:
// the command stops at the plan gate, so nothing is written.
string work = Path.Combine(Path.GetTempPath(), "kirin-flashpolicy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string app = Path.Combine(work, "UPDATE.APP");
    using (var writer = new BinaryWriter(File.Create(app)))
    {
        writer.Write(new byte[12]); // container prefix
        foreach ((string name, byte[] data) in new[] { ("BOOT", new byte[] { 1, 2, 3, 4 }), ("OEMINFO", new byte[] { 5, 6, 7, 8 }) })
        {
            writer.Write(new byte[] { 0x55, 0xaa, 0x5a, 0xa5 });
            writer.Write(102); writer.Write(0); writer.Write(Encoding.ASCII.GetBytes("HWTEST00")); writer.Write(0);
            writer.Write((uint)data.Length); writer.Write(new byte[32]);
            byte[] raw = new byte[32]; Encoding.ASCII.GetBytes(name).CopyTo(raw, 0); writer.Write(raw);
            writer.Write(new byte[10]); writer.Write(data);
            while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
        }
    }

    string? cli = new[] {
        Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
        Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
        Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
        Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
    }.FirstOrDefault(File.Exists);

    if (cli == null)
    {
        Skip("cli redline plan", "build KirinTool.Cli first (dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release)");
    }
    else
    {
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

        (int helpCode, string helpOut, _) = Run("flash-ota", "--help");
        Check(helpCode == 0 && helpOut.Contains("--allow-redline"), "flash-ota --help documents --allow-redline");

        (int planCode, string planOut, string planErr) = Run("flash-ota", app, "--json", "--work-dir", Path.Combine(work, "stage"));
        Check(planCode == 4, "flash-ota refuses a redline plan without --allow-redline (exit " + planCode + ")");
        Check(planErr.Contains("refused: plan writes redline partitions (OEMINFO)"), "refusal names the redline partition: " + planErr.Trim());
        Check(planErr.Contains("--allow-redline"), "refusal points at --allow-redline");

        using JsonDocument plan = JsonDocument.Parse(planOut);
        JsonElement planNode = plan.RootElement.GetProperty("plan");
        Check(plan.RootElement.GetProperty("gate").GetString() == "plan-only", "plan is plan-only, nothing written");
        Check(plan.RootElement.GetProperty("applied").GetBoolean() == false, "applied=false: the gate refused before any write");
        JsonElement redlines = planNode.GetProperty("redlines");
        Check(redlines.GetArrayLength() == 1 && redlines[0].GetString() == "OEMINFO", "plan lists OEMINFO as redline");
        bool bootSeen = false, oeminfoFlagged = false;
        foreach (JsonElement entry in planNode.GetProperty("partitions").EnumerateArray())
        {
            string name = entry.GetProperty("partition").GetString() ?? "";
            if (name == "BOOT") bootSeen = !entry.GetProperty("redline").GetBoolean();
            if (name == "OEMINFO") oeminfoFlagged = entry.GetProperty("redline").GetBoolean();
        }
        Check(bootSeen && oeminfoFlagged, "per-partition redline flags are correct (BOOT false, OEMINFO true)");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " flash policy checks passed.");
