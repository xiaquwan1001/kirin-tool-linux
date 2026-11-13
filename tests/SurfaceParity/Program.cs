// Hardware-free parity gate over the three faces of this tool: GUI buttons (MainWindow*.cs),
// the headless CLI command table (CliRunner.Commands) and the MCP tool schema generated from it.
//
// The single source of truth is Services/Capabilities/CapabilityRegistry.cs. This harness is the
// only thing that keeps it honest; every check below fails with an actionable list (what is
// missing, where to register it) instead of a bare assert.
//
// Run from the repository root (the CLI must be built first, it is driven out-of-process):
//   dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release
//   dotnet run --project tests/SurfaceParity -c Release
//
// Gates:
//   A  every registry CliCommand exists in CliRunner.Commands
//   A2 every CLI command is referenced by at least one capability
//   A3 the union of a command's declared Params equals its real parameter set
//   A4 registry WriteKind matches the command's own CliWriteKind
//   B  every registry McpTool exists in the generated MCP tool list
//   B2 every generated MCP tool is referenced by the registry
//   C  Write/PlanOnly tools require "confirm" in the MCP schema; ReadOnly tools must not offer it
//   C2 ReadOnly capabilities must not declare CLI write flags (yes / allow-redline)
//   D  (reverse of A/A2/B/B2, reported in one place so a new command cannot slip in unregistered)
//   E  every GUI handler (Handle* and device-action *_Click) is claimed by a capability or listed
//      in gui-ui-only.txt with a reason; registry GuiHandler names must really exist
//   E3 whitelist entries must exist and must not also be registered (no double bookkeeping)
//   F  a GUI-only capability must explain itself; ReadOnly may never stay GUI-only
//   G  end-to-end: a real "mcp" subprocess tools/list must match the registry McpTool set

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kirin_Tool.Services;
using Kirin_Tool.Services.Capabilities;

string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
var failures = new List<string>();
int checks = 0;

void Check(bool ok, string name)
{
    if (ok) Console.WriteLine("PASS " + name);
    else { Console.WriteLine("FAIL " + name); failures.Add(name); }
    checks++;
}

// ---------------------------------------------------------------- registry
IReadOnlyList<Capability> registry = CapabilityRegistry.All;

// ---------------------------------------------------------------- CLI face (reflection)
Assembly assembly = typeof(FastbootClient).Assembly;
Type cliRunner = assembly.GetType("Kirin_Tool.Cli.CliRunner")
    ?? throw new Exception("FAIL cannot reflect Kirin_Tool.Cli.CliRunner - renamed or moved? update tests/SurfaceParity/Program.cs");
PropertyInfo commandsProperty = cliRunner.GetProperty("Commands", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
    ?? throw new Exception("FAIL Kirin_Tool.Cli.CliRunner.Commands is gone - the CLI command table must stay reflectable for this gate");
var commands = ((System.Collections.IEnumerable)commandsProperty.GetValue(null)!).Cast<object>().ToList();

// WriteKind/IsWrite are default-implemented on ICliCommand, so they are not members of the
// concrete command classes: resolve against the interface when the class has nothing.
Type cliCommandInterface = assembly.GetType("Kirin_Tool.Cli.Commands.ICliCommand")
    ?? throw new Exception("FAIL cannot reflect Kirin_Tool.Cli.Commands.ICliCommand - renamed or moved? update tests/SurfaceParity/Program.cs");

object? Prop(object instance, string name)
{
    PropertyInfo? property = instance.GetType().GetProperty(name) ?? cliCommandInterface.GetProperty(name);
    return property?.GetValue(instance)
        ?? throw new Exception("FAIL cannot read '" + name + "' from " + instance.GetType().Name
            + " - the ICliCommand contract changed; update tests/SurfaceParity/Program.cs");
}

var cli = commands.Select(command => new
{
    Name = (string)Prop(command, "Name")!,
    McpName = (string)Prop(command, "McpName")!,
    Summary = (string)Prop(command, "Summary")!,
    WriteKind = Prop(command, "WriteKind")!.ToString()!,
    Params = ((System.Collections.IEnumerable)Prop(command, "Params")!)
        .Cast<object>()
        .Select(parameter => (string)Prop(parameter, "Name")!)
        .ToList(),
}).ToList();

// ---------------------------------------------------------------- MCP face (generated schema)
Type mcpServer = assembly.GetType("Kirin_Tool.Cli.Mcp.McpServer")
    ?? throw new Exception("FAIL cannot reflect Kirin_Tool.Cli.Mcp.McpServer - renamed or moved? update tests/SurfaceParity/Program.cs");
MethodInfo buildTools = mcpServer.GetMethod("BuildTools", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new Exception("FAIL McpServer.BuildTools() is gone - the MCP tool table must stay reflectable for this gate");
JsonArray tools = (JsonArray)buildTools.Invoke(null, null)!;

var mcp = tools.Select(node => new
{
    Name = (string)node!["name"]!,
    Required = node["inputSchema"]?["required"] is JsonArray required
        ? required.Select(value => (string)value!).ToList()
        : new List<string>(),
    Properties = node["inputSchema"]?["properties"] is JsonObject properties
        ? properties.Select(pair => pair.Key).ToList()
        : new List<string>(),
}).ToList();

// ---------------------------------------------------------------- GUI face (source scan)
string[] mainWindowFiles = Directory.GetFiles(root, "MainWindow*.cs", SearchOption.TopDirectoryOnly);
if (mainWindowFiles.Length == 0)
    throw new Exception("FAIL no MainWindow*.cs next to the repository root - run this harness from the repo root");

var guiHandlers = new SortedSet<string>(StringComparer.Ordinal);
var handlerDeclaration = new Regex(@"private\s+(?:async\s+)?(?:void|Task)\s+(?<name>[A-Za-z0-9_]+)\s*\(", RegexOptions.CultureInvariant);
foreach (string file in mainWindowFiles)
{
    foreach (Match match in handlerDeclaration.Matches(File.ReadAllText(file)))
    {
        string name = match.Groups["name"].Value;
        if (name.StartsWith("Handle", StringComparison.Ordinal) || name.EndsWith("_Click", StringComparison.Ordinal))
            guiHandlers.Add(name);
    }
}
Check(guiHandlers.Count > 0, "GUI scan found handlers in MainWindow*.cs (" + guiHandlers.Count + ")");

// Whitelist: pure UI plumbing only (file pickers, selection toggles, navigation, external links).
var whitelist = new Dictionary<string, string>(StringComparer.Ordinal);
string whitelistPath = Path.Combine(root, "tests/SurfaceParity/gui-ui-only.txt");
if (File.Exists(whitelistPath))
{
    foreach (string raw in File.ReadAllLines(whitelistPath))
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        int hash = line.IndexOf('#');
        string name = (hash < 0 ? line : line[..hash]).Trim();
        string reason = hash < 0 ? string.Empty : line[(hash + 1)..].Trim();
        if (name.Length > 0) whitelist[name] = reason;
    }
}

// ---------------------------------------------------------------- A: registry -> CLI
foreach (Capability capability in registry)
{
    if (capability.CliCommand is null) continue;
    bool exists = cli.Any(command => command.Name == capability.CliCommand);
    Check(exists, "A " + capability.Id + ": CLI command '" + capability.CliCommand + "' exists"
        + (exists ? "" : " <- missing; add it to Kirin_Tool.Cli.CliRunner.Commands or fix the registry row"));
}

// ---------------------------------------------------------------- A2/D: CLI -> registry
var cliNames = cli.Select(command => command.Name).ToHashSet(StringComparer.Ordinal);
foreach (string name in cliNames.OrderBy(value => value, StringComparer.Ordinal))
{
    bool registered = registry.Any(capability => capability.CliCommand == name);
    Check(registered, "D CLI command '" + name + "' is registered in CapabilityRegistry"
        + (registered ? "" : " <- unregistered capability; add a row to Services/Capabilities/CapabilityRegistry.cs"));
}

// ---------------------------------------------------------------- A3: params
foreach (var command in cli)
{
    var declaring = registry.Where(capability => capability.CliCommand == command.Name).ToList();
    if (declaring.Count == 0) continue;
    var declared = declaring.SelectMany(capability => capability.Params).ToHashSet(StringComparer.Ordinal);
    var actual = command.Params.ToHashSet(StringComparer.Ordinal);
    var missing = actual.Except(declared).OrderBy(value => value, StringComparer.Ordinal).ToList();
    Check(missing.Count == 0, "A3 CLI '" + command.Name + "': every parameter is declared in the registry"
        + (missing.Count == 0 ? "" : " <- undeclared: " + string.Join(", ", missing) + " (registry row: " + string.Join(" / ", declaring.Select(c => c.Id)) + ")"));
    var phantom = declared.Except(actual).OrderBy(value => value, StringComparer.Ordinal).ToList();
    Check(phantom.Count == 0, "A3 CLI '" + command.Name + "': registry declares no phantom parameter"
        + (phantom.Count == 0 ? "" : " <- not in the command: " + string.Join(", ", phantom)));
}

// ---------------------------------------------------------------- A4: write kinds agree
foreach (Capability capability in registry.Where(capability => capability.CliCommand is not null))
{
    var command = cli.FirstOrDefault(candidate => candidate.Name == capability.CliCommand);
    if (command == null) continue;
    string expected = capability.WriteKind switch
    {
        CapabilityWriteKind.ReadOnly => "ReadOnly",
        CapabilityWriteKind.PlanOnly => "PlanOnly",
        _ => "RealWrite",
    };
    Check(expected == command.WriteKind, "A4 " + capability.Id + ": registry WriteKind " + capability.WriteKind + " == CLI " + command.WriteKind
        + (expected == command.WriteKind ? "" : " <- registry says " + expected + ", ICliCommand.WriteKind says " + command.WriteKind));
}

// ---------------------------------------------------------------- B/B2: MCP tool table
var mcpNames = mcp.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
foreach (Capability capability in registry)
{
    if (capability.McpTool is null) continue;
    bool exists = mcpNames.Contains(capability.McpTool);
    Check(exists, "B " + capability.Id + ": MCP tool '" + capability.McpTool + "' is generated"
        + (exists ? "" : " <- missing; the MCP table comes from ICliCommand.McpName, so fix that or the registry row"));
}
foreach (string name in mcpNames.OrderBy(value => value, StringComparer.Ordinal))
{
    bool registered = registry.Any(capability => capability.McpTool == name);
    Check(registered, "B2 MCP tool '" + name + "' is registered in CapabilityRegistry"
        + (registered ? "" : " <- unregistered capability; add a row to Services/Capabilities/CapabilityRegistry.cs"));
}

// ---------------------------------------------------------------- C: confirm gating
foreach (Capability capability in registry)
{
    if (capability.McpTool is null) continue;
    var tool = mcp.FirstOrDefault(candidate => candidate.Name == capability.McpTool);
    if (tool == null) continue;
    if (capability.WriteKind == CapabilityWriteKind.ReadOnly)
    {
        Check(!tool.Required.Contains("confirm") && !tool.Properties.Contains("confirm"),
            "C " + capability.Id + ": read-only MCP tool offers no 'confirm'"
            + (tool.Properties.Contains("confirm") ? " <- it is a write tool; fix ICliCommand.WriteKind / CliWriteKind" : ""));
    }
    else
    {
        Check(tool.Required.Contains("confirm"), "C " + capability.Id + ": MCP tool requires 'confirm'"
            + (tool.Required.Contains("confirm") ? "" : " <- write tool without a confirm gate; check ICliCommand.WriteKind and McpServer.BuildTool"));
    }
}

// ---------------------------------------------------------------- C2: read-only tools cannot write
var writeFlags = new[] { "yes", "allow-redline", "confirm" };
foreach (Capability capability in registry.Where(capability => capability.WriteKind == CapabilityWriteKind.ReadOnly))
{
    var offending = capability.Params.Where(parameter => writeFlags.Contains(parameter, StringComparer.Ordinal)).ToList();
    Check(offending.Count == 0, "C2 " + capability.Id + ": read-only capability declares no write flag"
        + (offending.Count == 0 ? "" : " <- declares " + string.Join(", ", offending)));
    bool claimsWrite = capability.SideEffects.Any(effect => effect.Contains("真实写入", StringComparison.Ordinal));
    Check(!claimsWrite, "C2 " + capability.Id + ": read-only capability declares no write side effect"
        + (claimsWrite ? " <- SideEffects 里写了真实写入" : ""));
}

// ---------------------------------------------------------------- E: GUI handlers
var registeredHandlers = registry
    .Where(capability => capability.GuiHandler is not null)
    .Select(capability => capability.GuiHandler!)
    .ToHashSet(StringComparer.Ordinal);

foreach (string handler in registeredHandlers.OrderBy(value => value, StringComparer.Ordinal))
{
    bool exists = guiHandlers.Contains(handler);
    Check(exists, "E registry GuiHandler '" + handler + "' exists in MainWindow*.cs"
        + (exists ? "" : " <- handler renamed/removed; update the registry row"));
}

foreach (string handler in guiHandlers)
{
    if (registeredHandlers.Contains(handler)) continue;
    bool whitelisted = whitelist.TryGetValue(handler, out string? reason) && !string.IsNullOrWhiteSpace(reason);
    Check(whitelisted, "E GUI handler '" + handler + "' is registered or whitelisted with a reason"
        + (whitelisted ? "" : " <- unregistered GUI capability; either register it in CapabilityRegistry (with a GuiOnlyReason) or add a justified line to tests/SurfaceParity/gui-ui-only.txt"));
}

// ---------------------------------------------------------------- E3: whitelist hygiene
foreach (var entry in whitelist.OrderBy(pair => pair.Key, StringComparer.Ordinal))
{
    Check(guiHandlers.Contains(entry.Key), "E3 whitelist entry '" + entry.Key + "' names a real GUI handler"
        + (guiHandlers.Contains(entry.Key) ? "" : " <- stale whitelist line; delete it"));
    Check(string.IsNullOrWhiteSpace(entry.Value) == false, "E3 whitelist entry '" + entry.Key + "' carries a reason");
    Check(!registeredHandlers.Contains(entry.Key), "E3 whitelist entry '" + entry.Key + "' is not also registered"
        + (registeredHandlers.Contains(entry.Key) ? " <- double bookkeeping; drop the whitelist line" : ""));
}

// ---------------------------------------------------------------- F: GUI-only debt
foreach (Capability capability in registry.Where(capability => capability.IsGuiOnly))
{
    Check(capability.GuiHandler is not null, "F " + capability.Id + ": GUI-only capability names its handler");
    if (capability.WriteKind == CapabilityWriteKind.ReadOnly)
    {
        Check(false, "F " + capability.Id + ": read-only capability must not stay GUI-only"
            + " <- read-only work has no excuse (design doc §2.1); add a CLI command (MCP follows automatically)");
    }
    else
    {
        bool reason = !string.IsNullOrWhiteSpace(capability.GuiOnlyReason);
        Check(reason, "F " + capability.Id + ": GUI-only write capability explains itself"
            + (reason ? "" : " <- GuiOnlyReason 为空；写债务必须写明理由与出处"));
    }
}

// ---------------------------------------------------------------- id hygiene
Check(registry.Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count() == registry.Count,
    "registry capability ids are unique");
foreach (Capability capability in registry)
{
    Check(capability.NameZh.Length > 0 && capability.SideEffects.Count > 0,
        "registry row '" + capability.Id + "' has a Chinese name and declared side effects");
}

// ---------------------------------------------------------------- G: end-to-end MCP tools/list
string? cliBinary = new[]
{
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli"),
    Path.Combine(root, "KirinTool.Cli/bin/Release/net10.0/KirinTool.Cli.dll"),
    Path.Combine(root, "KirinTool.Cli/bin/Debug/net10.0/KirinTool.Cli.dll"),
}.FirstOrDefault(File.Exists);

if (cliBinary == null)
{
    Check(false, "G mcp tools/list end-to-end"
        + " <- CLI binary not found; build it first: dotnet build KirinTool.Cli/KirinTool.Cli.csproj -c Release");
}
else
{
    bool dll = cliBinary.EndsWith(".dll", StringComparison.Ordinal);
    var startInfo = new ProcessStartInfo(dll ? "dotnet" : cliBinary)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = root,
    };
    if (dll) startInfo.ArgumentList.Add(cliBinary);
    startInfo.ArgumentList.Add("mcp");
    using Process process = Process.Start(startInfo)!;
    process.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");
    process.StandardInput.Close();
    string stdout = process.StandardOutput.ReadToEnd();
    _ = process.StandardError.ReadToEnd();
    process.WaitForExit(60000);

    var live = new HashSet<string>(StringComparer.Ordinal);
    foreach (string line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        JsonNode? node;
        try { node = JsonNode.Parse(line); } catch (JsonException) { continue; }
        if (node?["result"]?["tools"] is not JsonArray liveTools) continue;
        foreach (JsonNode? tool in liveTools)
            if (tool?["name"] is JsonValue name) live.Add((string)name!);
    }

    Check(live.Count > 0 && live.SetEquals(mcpNames),
        "G live 'mcp' tools/list == generated tool table (" + live.Count + " tools)"
        + (live.SetEquals(mcpNames) ? ""
            : " <- live-only: [" + string.Join(", ", live.Except(mcpNames).OrderBy(v => v, StringComparer.Ordinal))
              + "] generated-only: [" + string.Join(", ", mcpNames.Except(live).OrderBy(v => v, StringComparer.Ordinal)) + "]"));
}

// ---------------------------------------------------------------- summary
Console.WriteLine();
Console.WriteLine("faces: " + registry.Count + " capabilities, " + cli.Count + " CLI commands, "
    + mcp.Count + " MCP tools, " + guiHandlers.Count + " GUI handlers ("
    + registry.Count(capability => !capability.IsGuiOnly) + " with CLI+MCP, "
    + registry.Count(capability => capability.IsGuiOnly) + " GUI-only)");
foreach (Capability gap in registry.Where(capability => capability.IsGuiOnly))
    Console.WriteLine("  GUI-only: " + gap.Id + " (" + gap.NameZh + ") - " + gap.GuiOnlyReason);

if (failures.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine(failures.Count + " parity check(s) failed / " + checks + " total:");
    foreach (string failure in failures) Console.WriteLine("  - " + failure);
    throw new Exception("FAIL " + failures.Count + " surface-parity check(s) failed (see the list above)");
}

Console.WriteLine(checks + " surface parity checks passed.");
