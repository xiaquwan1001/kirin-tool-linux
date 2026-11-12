// Hardware-free protocol checks for "KirinTool.Cli mcp" (newline-delimited JSON-RPC
// over stdio). One process, six requests matched by id:
//   (a) tools/list answers without a prior initialize;
//   (b) unknown tool -> isError=true, _meta.exitCode=1;
//   (c) confirm as a JSON string "true" -> JSON-RPC error -32602 (type validation);
//   (d) allow-redline argument -> -32602 (redline override is CLI-only);
//   (e) flash_ota without confirm -> gate-only plan result: exit 4, applied=false, gate="plan-only";
//   (f) flash_ota with an OEMINFO entry, confirm:true, no allow-redline -> gate="refused", isError=true.
// (c), (d) and (f) pin the post-F1 MCP contract: -32602 for argument validation and
// isError=true for gate refusals with a structured refused JSON.
//
// Run from the repository root:  dotnet run --project tests/McpProtocol -c Release

using System.Diagnostics;
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

// Minimal UPDATE.APP writer, same container shape tests/Extractor uses: 12-byte
// container prefix, then per entry a 102-byte header around a 55 AA 5A A5 marker.
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

string work = Path.Combine(Path.GetTempPath(), "kirin-mcpprotocol-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);
try
{
    string cleanApp = Path.Combine(work, "clean.app");
    File.WriteAllBytes(cleanApp, Fixture(("BOOT", new byte[] { 1, 2, 3, 4 })));

    string redlineApp = Path.Combine(work, "redline.app");
    File.WriteAllBytes(redlineApp, Fixture(("BOOT", new byte[] { 1 }), ("OEMINFO", new byte[] { 5, 6, 7, 8 })));

    string Req(int id, object parameters) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "tools/call", ["params"] = parameters,
        });

    string args1 = JsonSerializer.Serialize(new Dictionary<string, object> { ["files"] = cleanApp });
    string args2 = JsonSerializer.Serialize(new Dictionary<string, object> { ["files"] = redlineApp, ["confirm"] = true });

    string[] requests =
    {
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
        Req(2, new Dictionary<string, object> { ["name"] = "definitely_not_a_tool", ["arguments"] = new { confirm = true } }),
        Req(3, new Dictionary<string, object> { ["name"] = "downgrade_enable", ["arguments"] = new Dictionary<string, object> { ["confirm"] = "true" } }),
        Req(4, new Dictionary<string, object> { ["name"] = "flash_ota", ["arguments"] = new Dictionary<string, object> { ["files"] = cleanApp, ["allow-redline"] = true, ["confirm"] = true } }),
        Req(5, new Dictionary<string, object> { ["name"] = "flash_ota", ["arguments"] = new Dictionary<string, object> { ["files"] = cleanApp } }),
        Req(6, new Dictionary<string, object> { ["name"] = "flash_ota", ["arguments"] = new Dictionary<string, object> { ["files"] = redlineApp, ["confirm"] = true } }),
    };

    var psi = new ProcessStartInfo(cli.EndsWith(".dll", StringComparison.Ordinal) ? "dotnet" : cli)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = root,
    };
    if (cli.EndsWith(".dll", StringComparison.Ordinal)) psi.ArgumentList.Add(cli);
    psi.ArgumentList.Add("mcp");
    using Process process = Process.Start(psi)!;
    foreach (string request in requests) process.StandardInput.WriteLine(request);
    process.StandardInput.Close();

    var responses = new Dictionary<int, JsonElement>();
    string? line;
    while ((line = process.StandardOutput.ReadLine()) != null)
    {
        using JsonDocument doc = JsonDocument.Parse(line);
        if (doc.RootElement.TryGetProperty("id", out JsonElement id))
            responses[id.GetInt32()] = doc.RootElement.Clone();
    }
    process.WaitForExit();
    string stderrAll = process.StandardError.ReadToEnd();
    Check(process.ExitCode == 0, "mcp exits 0 after stdin close (got " + process.ExitCode + ")\n" + stderrAll);
    Check(responses.Count == requests.Length, "one response per request (got " + responses.Count + ")");

    JsonElement R(int id) => responses[id];

    // (a) tools/list without initialize
    {
        JsonElement result = R(1).GetProperty("result");
        var names = new List<string>();
        foreach (JsonElement tool in result.GetProperty("tools").EnumerateArray())
            names.Add(tool.GetProperty("name").GetString() ?? "");
        Check(names.Contains("flash_ota"), "tools/list exposes flash_ota without initialize");
    }

    // (b) unknown tool
    {
        JsonElement result = R(2).GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean(), "unknown tool: isError=true");
        Check(result.GetProperty("_meta").GetProperty("exitCode").GetInt32() == 1, "unknown tool: _meta.exitCode=1");
    }

    // (c) confirm as a JSON string is a parameter type error, not a silent false
    Check(R(3).TryGetProperty("error", out JsonElement err3) && err3.GetProperty("code").GetInt32() == -32602,
        "confirm:\"true\" rejected with JSON-RPC error -32602");

    // (d) redline override is CLI-only
    Check(R(4).TryGetProperty("error", out JsonElement err4) && err4.GetProperty("code").GetInt32() == -32602,
        "allow-redline over MCP rejected with JSON-RPC error -32602");

    // (e) flash_ota without confirm: plan-only gate result, nothing written
    {
        JsonElement result = R(5).GetProperty("result");
        Check(!result.GetProperty("isError").GetBoolean(), "flash_ota without confirm: isError=false (gate exit 4 is a result)");
        Check(result.GetProperty("_meta").GetProperty("exitCode").GetInt32() == 4, "flash_ota without confirm: _meta.exitCode=4");
        JsonElement plan = result.GetProperty("structuredContent");
        Check(plan.GetProperty("applied").GetBoolean() == false, "flash_ota without confirm: applied=false");
        Check(plan.GetProperty("gate").GetString() == "plan-only", "flash_ota without confirm: gate=plan-only");
    }

    // (f) confirmed redline plan: refused and reported as a protocol-level error
    {
        JsonElement result = R(6).GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean(), "flash_ota with redline + confirm: isError=true");
        JsonElement plan = result.GetProperty("structuredContent");
        Check(plan.ValueKind == JsonValueKind.Object && plan.TryGetProperty("gate", out JsonElement gate)
            && gate.GetString() == "refused", "flash_ota with redline + confirm: gate=refused");
        if (plan.ValueKind == JsonValueKind.Object && plan.TryGetProperty("applied", out JsonElement applied))
            Check(!applied.GetBoolean(), "flash_ota with redline + confirm: applied=false");
    }
}
finally
{
    try { Directory.Delete(work, true); } catch { }
}

Console.WriteLine(checks + " MCP protocol checks passed.");
