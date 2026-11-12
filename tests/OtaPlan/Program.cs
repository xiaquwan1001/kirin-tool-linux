// Hardware-free checks for the flash-ota plan (OtaFlashRunner.BuildPlan, exercised
// through the CLI's "flash-ota --json" plan output because BuildPlan/OtaPlan are
// internal and adding InternalsVisibleTo is a production-code change this harness
// deliberately avoids):
//   - --partitions / --exclude filtering (by archive name, with --json skipped list);
//   - multiple "super" fragments merge into one entry with a warning;
//   - a gzip-magic payload survives extraction byte-for-byte (extract -> gunzip == source).
// NOTE: the archive-name -> loader-name alias table (HISIUFS_GPT -> ptable, UFSFW ->
// ufs_fw, ...) is NOT applied by BuildPlan; it lives in ResolveNamesAsync, which needs
// a real device (fastboot getvar ptable). It is therefore not assertable here; see
// docs/audit/2026-11-12/fix-F4.md.
//
// Run from the repository root:  dotnet run --project tests/OtaPlan -c Release

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); Console.WriteLine("PASS " + name); checks++; }

string? cli = new[] {
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
}.FirstOrDefault(File.Exists) ?? throw new Exception(
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

// Minimal UPDATE.APP writer (same container shape tests/Extractor uses).
byte[] Fixture(params (string Name, byte[] Data)[] entries)
{
    using var stream = new MemoryStream();
    using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
    {
        writer.Write(new byte[12]); // container prefix
        foreach ((string name, byte[] data) in entries)
        {
            writer.Write(new byte[] { 0x55, 0xaa, 0x5a, 0xa5 });
            writer.Write(102); writer.Write(0); writer.Write(Encoding.ASCII.GetBytes("HWTEST00")); writer.Write(0);
            writer.Write((uint)data.Length); writer.Write(new byte[32]);
            byte[] raw = new byte[32]; Encoding.ASCII.GetBytes(name).CopyTo(raw, 0); writer.Write(raw);
            writer.Write(new byte[10]); writer.Write(data);
            while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
        }
    }
    return stream.ToArray();
}

// Run "flash-ota <app> --json", assert the plan-only gate held, and return the
// planned partition names in order.
List<string> Plan(string app, string label)
{
    (int code, string stdout, string stderr) = Run("flash-ota", app, "--json");
    Check(code == 4, label + " exits 4: " + stderr.Trim());
    var planned = new List<string>();
    using JsonDocument doc = JsonDocument.Parse(stdout);
    Check(doc.RootElement.GetProperty("applied").GetBoolean() == false, label + ": applied=false");
    Check(doc.RootElement.GetProperty("gate").GetString() == "plan-only", label + ": gate=plan-only");
    foreach (JsonElement entry in doc.RootElement.GetProperty("plan").GetProperty("partitions").EnumerateArray())
        planned.Add(entry.GetProperty("partition").GetString() ?? "");
    return planned;
}

string work = Path.Combine(Path.GetTempPath(), "kirin-otaplan-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    // ---- 1. --partitions / --exclude filters use the archive entry names ----
    {
        string app = Path.Combine(work, "filter.app");
        File.WriteAllBytes(app, Fixture(("BOOT", new byte[] { 1 }), ("CUST", new byte[] { 2 }), ("VBMETA", new byte[] { 3 })));

        var all = Plan(app, "all entries are planned in archive order");
        Check(all.SequenceEqual(new[] { "BOOT", "CUST", "VBMETA" }), "plan keeps archive names and order: [" + string.Join(", ", all) + "]");

        (int code, string stdout, _) = Run("flash-ota", app, "--partitions", "BOOT", "--json");
        Check(code == 4, "--partitions plan exits 4");
        using JsonDocument doc = JsonDocument.Parse(stdout);
        var names = new List<string>();
        foreach (JsonElement e in doc.RootElement.GetProperty("plan").GetProperty("partitions").EnumerateArray())
            names.Add(e.GetProperty("partition").GetString() ?? "");
        Check(names.SequenceEqual(new[] { "BOOT" }), "--partitions BOOT keeps only BOOT");
        var skipped = new List<string>();
        foreach (JsonElement e in doc.RootElement.GetProperty("plan").GetProperty("skipped").EnumerateArray())
            skipped.Add(e.GetString() ?? "");
        Check(new HashSet<string>(skipped, StringComparer.Ordinal).SetEquals(new[] { "CUST", "VBMETA" }), "--partitions reports the rest as skipped: [" + string.Join(", ", skipped) + "]");

        (code, stdout, _) = Run("flash-ota", app, "--exclude", "CUST,VBMETA", "--json");
        Check(code == 4, "--exclude plan exits 4");
        using JsonDocument doc2 = JsonDocument.Parse(stdout);
        var kept = new List<string>();
        foreach (JsonElement e in doc2.RootElement.GetProperty("plan").GetProperty("partitions").EnumerateArray())
            kept.Add(e.GetProperty("partition").GetString() ?? "");
        Check(kept.SequenceEqual(new[] { "BOOT" }), "--exclude CUST,VBMETA keeps only BOOT");
    }

    // ---- 2. multiple super fragments merge into one entry with a warning ----
    {
        string app = Path.Combine(work, "super.app");
        byte[] s1 = new byte[100], s2 = new byte[60];
        File.WriteAllBytes(app, Fixture(("BOOT", new byte[] { 1 }), ("SUPER", s1), ("SUPER", s2), ("CUST", new byte[] { 2 })));

        (int code, string stdout, _) = Run("flash-ota", app, "--json");
        Check(code == 4, "super plan exits 4");
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement plan = doc.RootElement.GetProperty("plan");
        var supers = new List<JsonElement>();
        var others = new List<string>();
        foreach (JsonElement e in plan.GetProperty("partitions").EnumerateArray())
        {
            // the merged entry is emitted as "super" (lower case, like the GPT name);
            // compare case-insensitively because UPDATE.APP stores SUPER.
            if (string.Equals(e.GetProperty("partition").GetString() ?? "", "SUPER", StringComparison.OrdinalIgnoreCase)) supers.Add(e.Clone());
            else others.Add(e.GetProperty("partition").GetString() ?? "");
        }
        Check(supers.Count == 1, "split super merges into a single entry (got " + supers.Count + ")");
        Check(supers[0].GetProperty("size").GetInt64() == s1.Length + s2.Length, "merged super size is the fragment sum");
        Check(supers[0].GetProperty("source").GetString() == "Merged super", "merged super entry marks its source as 'Merged super'");
        Check(others.SequenceEqual(new[] { "BOOT", "CUST" }), "merge keeps the other partitions in order");
        bool warningSeen = false;
        foreach (JsonElement w in plan.GetProperty("warnings").EnumerateArray())
            warningSeen |= (w.GetString() ?? "").Contains("super is split across 2");
        Check(warningSeen, "merge emits a 'super is split across 2' warning");
    }

    // ---- 3. gzip-magic payload: extracted bytes gunzip back to the source ----
    {
        byte[] original = new byte[5000];
        new Random(20261112).NextBytes(original);
        byte[] gz;
        using (var compressed = new MemoryStream())
        {
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(original);
            gz = compressed.ToArray();
        }
        Check(gz[0] == 0x1f && gz[1] == 0x8b, "fixture payload really carries the gzip magic");

        string app = Path.Combine(work, "gzip.app");
        File.WriteAllBytes(app, Fixture(("GZBOOT", gz)));

        (int code, string stdout, _) = Run("flash-ota", app, "--json");
        Check(code == 4, "gzip payload plan exits 4");
        using JsonDocument doc = JsonDocument.Parse(stdout);
        JsonElement entry = doc.RootElement.GetProperty("plan").GetProperty("partitions")[0];
        Check(entry.GetProperty("size").GetInt64() == gz.Length, "plan size is the stored (compressed) length");

        string outDir = Path.Combine(work, "extracted");
        (code, _, string extractErr) = Run("extract", app, outDir);
        Check(code == 0, "extract of the gzip payload succeeds (got " + code + "): " + extractErr.Trim());
        using FileStream extracted = File.OpenRead(Path.Combine(outDir, "GZBOOT.img"));
        using var gunzip = new GZipStream(extracted, CompressionMode.Decompress);
        using var roundTrip = new MemoryStream();
        gunzip.CopyTo(roundTrip);
        Check(roundTrip.ToArray().SequenceEqual(original), "extracted gzip payload decompresses byte-for-byte to the source");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " ota plan checks passed.");
