// Hardware-free assertions for the approval gate (docs/approval-gate.md).
//
//   dotnet run --project tests/ApprovalGate
//
// The gate is the one place where "who may write" is decided, so every branch is pinned here:
// approve / deny / timeout, the typed confirm phrase for redline items, the CLI refusal on a
// non-interactive stdin, the MCP refusal when no GUI can be asked, the audit line, and the
// idempotency of a repeated identical request.

using System.Reflection;
using System.Text.Json;
using Kirin_Tool.Services.Approval;
using Kirin_Tool.Services.Capabilities;
using Kirin_Tool.Services.WriteOperations;

var failures = new List<string>();
int checks = 0;

void Check(bool ok, string name)
{
    if (ok) Console.WriteLine("PASS " + name);
    else { Console.WriteLine("FAIL " + name); failures.Add(name); }
    checks++;
}

// ---------------------------------------------------------------- registry shape
IReadOnlyList<Capability> registry = CapabilityRegistry.All;
var writes = registry.Where(capability => capability.WriteKind == CapabilityWriteKind.Write).ToList();
Check(writes.Count >= 8, "registry declares " + writes.Count + " write capabilities");
foreach (Capability capability in writes)
{
    Check(capability.CliCommand is not null && capability.McpTool is not null && capability.GuiHandler is not null,
        "registry " + capability.Id + ": all three faces exist (no GUI-only write)");
    Check(capability.NeedsApproval, "registry " + capability.Id + ": declares an ApprovalKind");
    Check(!string.IsNullOrWhiteSpace(capability.ServiceMethod), "registry " + capability.Id + ": names the shared service method");
}
Check(CapabilityRegistry.GuiOnly.All(capability => capability.WriteKind != CapabilityWriteKind.Write),
    "no write capability is GUI-only any more");

// ---------------------------------------------------------------- 1. GUI approve / deny / timeout
var auditMemory = new MemoryApprovalAuditSink();
var cache = new SessionApprovalCache();
string sessionId = cache.SessionId;

ApprovalRequest Reboot() => ApprovalRequests.For("reboot", argv: new[] { "fastboot", "reboot" }, sessionId: sessionId);

var approvePrompt = new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved));
var approveGate = new GuiApprovalGate(approvePrompt, new SessionApprovalCache(), auditMemory);
ApprovalDecision approved = await approveGate.RequestAsync(Reboot(), CancellationToken.None);
Check(approved.IsApproved && approved is ApprovalDecision.Approved { Scope: ApprovalScope.Once }, "GUI gate: approve once -> Approved(Once)");
Check(approvePrompt.LastModel?.RequiresTypedConfirm == false, "GUI gate: an ordinary write needs no typed phrase");

var denyGate = new GuiApprovalGate(new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Denied, null, "nope")), null, auditMemory);
ApprovalDecision denied = await denyGate.RequestAsync(Reboot(), CancellationToken.None);
Check(denied is ApprovalDecision.Denied, "GUI gate: deny -> Denied");

var timeoutGate = new GuiApprovalGate(new NeverPrompt(), null, auditMemory);
ApprovalRequest quick = Reboot() with { TimeoutSeconds = 1 };
ApprovalDecision timedOut = await timeoutGate.RequestAsync(quick, CancellationToken.None);
Check(timedOut is ApprovalDecision.TimedOut, "GUI gate: unanswered dialog -> TimedOut (never an implicit yes)");

// ---------------------------------------------------------------- 2. redline needs the typed phrase
ApprovalRequest Sn() => ApprovalRequests.For("write-sn",
    argv: new[] { "fastboot", "getvar", "nve:SN@NEWSERIAL1234" }, targets: new[] { "nve:SN" }, sessionId: sessionId);
Check(Sn().RequiresTypedConfirm, "write-sn request requires a typed confirm");
Check(Sn().ConfirmPhrase == "I UNDERSTAND write-sn", "confirm phrase is per capability: " + Sn().ConfirmPhrase);
Check(!Sn().Argv.Any(token => token.Contains("NEWSERIAL1234")), "argv is redacted before it reaches the dialog/audit");

var redlineNoPhrase = new GuiApprovalGate(new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved)), null, auditMemory);
Check(await redlineNoPhrase.RequestAsync(Sn(), CancellationToken.None) is ApprovalDecision.Denied,
    "redline + approve without the typed phrase -> Denied");

var redlineWrongPhrase = new GuiApprovalGate(new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved, "yes")), null, auditMemory);
Check(await redlineWrongPhrase.RequestAsync(Sn(), CancellationToken.None) is ApprovalDecision.Denied,
    "redline + wrong phrase -> Denied");

var redlineOk = new GuiApprovalGate(new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved, Sn().ConfirmPhrase)), null, auditMemory);
ApprovalDecision redlineApproved = await redlineOk.RequestAsync(Sn(), CancellationToken.None);
Check(redlineApproved.IsApproved, "redline + exact phrase -> Approved");
Check(auditMemory.Entries.Any(entry => entry.CapabilityId == "write-sn" && entry.TypedConfirm),
    "audit: the typed-confirm approval is recorded as typedConfirm=true");

// ---------------------------------------------------------------- 3. CLI non-interactive refuses redline
var cliAudit = new MemoryApprovalAuditSink();
ApprovalRequest SnAcknowledged() => Sn() with { Acknowledged = true, RedlineOverride = true, Origin = ApprovalOrigin.Cli, SessionId = "cli-session" };

var cliNonInteractive = new CliApprovalGate(new StringReader(Sn().ConfirmPhrase), new StringWriter(), interactive: false, null, cliAudit);
ApprovalDecision cliRefused = await cliNonInteractive.RequestAsync(SnAcknowledged(), CancellationToken.None);
Check(cliRefused is ApprovalDecision.Denied { ExitCode: 4 }, "CLI: redline write on a non-interactive stdin -> Denied (exit 4)");
Check(cliRefused is ApprovalDecision.Denied hint && hint.Reason.Contains("GUI"),
    "CLI: the refusal tells the caller to approve in the GUI instead");

var cliInteractive = new CliApprovalGate(new StringReader(Sn().ConfirmPhrase), new StringWriter(), interactive: true, null, cliAudit);
Check(await cliInteractive.RequestAsync(SnAcknowledged(), CancellationToken.None) is ApprovalDecision.Approved,
    "CLI: redline write with the phrase typed on an interactive stdin -> Approved");

var cliNoYes = new CliApprovalGate(new StringReader(""), new StringWriter(), interactive: false, null, cliAudit);
Check(await cliNoYes.RequestAsync(Reboot() with { Origin = ApprovalOrigin.Cli, SessionId = "cli-no-yes" }, CancellationToken.None) is ApprovalDecision.Denied,
    "CLI: without --yes the write is denied");

var cliYes = new CliApprovalGate(new StringReader(""), new StringWriter(), interactive: false, null, cliAudit);
Check(await cliYes.RequestAsync(Reboot() with { Acknowledged = true, Origin = ApprovalOrigin.Cli, SessionId = "cli-yes" }, CancellationToken.None) is ApprovalDecision.Approved,
    "CLI: ordinary write with --yes -> Approved");

var cliNoRedlineOverride = new CliApprovalGate(new StringReader(Sn().ConfirmPhrase), new StringWriter(), interactive: true, null, cliAudit);
Check(await cliNoRedlineOverride.RequestAsync(SnAcknowledged() with { RedlineOverride = false }, CancellationToken.None) is ApprovalDecision.Denied,
    "CLI: redline write without --allow-redline is denied even with the phrase typed");

// ---------------------------------------------------------------- 4. MCP without a GUI
var mcpAudit = new MemoryApprovalAuditSink();
var mcpNoGui = new McpApprovalGate(new NoGuiBridge(), null, mcpAudit);
ApprovalRequest McpReboot() => Reboot() with { Acknowledged = true, Origin = ApprovalOrigin.Mcp, SessionId = "mcp-session" };
ApprovalDecision mcpDecision = await mcpNoGui.RequestAsync(McpReboot(), CancellationToken.None);
Check(mcpDecision is ApprovalDecision.NotSupported { ErrorCode: "approval_required_human" },
    "MCP: confirm:true without a running GUI -> approval_required_human (confirm alone never passes)");
Check(await mcpNoGui.RequestAsync(McpReboot() with { Acknowledged = false }, CancellationToken.None) is ApprovalDecision.Denied,
    "MCP: without confirm:true the request is denied before the human step");

// ---------------------------------------------------------------- 5. the IPC channel really works
string socketPath = Path.Combine(Path.GetTempPath(), "kirin-approval-test-" + Guid.NewGuid().ToString("n") + ".sock");
using (var server = new GuiApprovalServer((request, token) => Task.FromResult(
           new GuiApprovalAnswer(ApprovalPromptOutcome.Approved, request.RequiresTypedConfirm, null, ApprovalScope.Once)), socketPath))
{
    Check(server.Start(), "IPC: the GUI side binds " + socketPath);
    var bridge = new SocketGuiApprovalBridge(socketPath);
    Check(bridge.IsGuiRunning(), "IPC: the bridge detects the running GUI");
    GuiApprovalAnswer answer = await bridge.RequestApprovalAsync(McpReboot(), CancellationToken.None);
    Check(answer.Outcome == ApprovalPromptOutcome.Approved, "IPC: a request round-trips to the GUI and back");

    var mcpWithGui = new McpApprovalGate(bridge, null, mcpAudit);
    Check(await mcpWithGui.RequestAsync(McpReboot() with { SessionId = "mcp-gui" }, CancellationToken.None) is ApprovalDecision.Approved,
        "MCP: with a GUI listening, the human answer approves the write");
}
Check(!new SocketGuiApprovalBridge(socketPath).IsGuiRunning(), "IPC: after the GUI stops, the bridge reports it as not running");

// ---------------------------------------------------------------- 6. audit + idempotency
string auditPath = Path.Combine(Path.GetTempPath(), "kirin-approval-audit-" + Guid.NewGuid().ToString("n") + ".jsonl");
var jsonlSink = new JsonlApprovalAuditSink(auditPath);
var idempotentCache = new SessionApprovalCache();
string sharedSession = idempotentCache.SessionId;
var countedPrompt = new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved));
var idempotentGate = new GuiApprovalGate(countedPrompt, idempotentCache, jsonlSink);
ApprovalGates.Install(idempotentGate); // AdmitAsync resolves the gate the entry point installed
ApprovalRequest repeated = ApprovalRequests.For("reboot", argv: new[] { "fastboot", "reboot" }, sessionId: sharedSession);

var admission1 = await ApprovalGuard.AdmitAsync(repeated, CancellationToken.None);
var admission2 = await ApprovalGuard.AdmitAsync(repeated, CancellationToken.None);
Check(countedPrompt.Asked == 1, "idempotency: the same request is prompted once (asked=" + countedPrompt.Asked + ")");
Check(admission2.Ticket is not null && admission1.Ticket!.Fingerprint == admission2.Ticket!.Fingerprint,
    "idempotency: the replayed request reuses the same approval fingerprint");

string[] lines = File.Exists(auditPath) ? File.ReadAllLines(auditPath).Where(line => line.Trim().Length > 0).ToArray() : Array.Empty<string>();
Check(lines.Length >= 1, "audit: the JSONL sink wrote " + lines.Length + " line(s)");
if (lines.Length >= 2)
{
    using JsonDocument first = JsonDocument.Parse(lines[0]);
    using JsonDocument second = JsonDocument.Parse(lines[1]);
    Check(first.RootElement.GetProperty("event").GetString() == "approval.decision", "audit: line carries event=approval.decision");
    Check(first.RootElement.GetProperty("capabilityId").GetString() == "reboot", "audit: line carries the capability id");
    Check(first.RootElement.GetProperty("origin").GetString() == "gui", "audit: line carries the originating face");
    Check(first.RootElement.TryGetProperty("fingerprint", out _) && first.RootElement.TryGetProperty("ts", out _),
        "audit: line carries fingerprint and timestamp");
    Check(second.RootElement.GetProperty("reused").GetBoolean(), "audit: the replayed approval is marked reused=true");
}

// ---------------------------------------------------------------- ticket discipline
Check(typeof(WriteTicket).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
    "WriteTicket: no public constructor (only the gate can mint one)");
foreach (MethodInfo method in typeof(DeviceWriteOps).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
{
    ParameterInfo[] parameters = method.GetParameters();
    Check(parameters.Length > 0 && parameters[0].ParameterType == typeof(WriteTicket),
        "DeviceWriteOps." + method.Name + ": first parameter is the approved WriteTicket");
}

var deniedAdmission = new ApprovalAdmission(new ApprovalDecision.Denied("test"), Reboot(), null);
Check(!deniedAdmission.IsApproved, "admission: a denied decision mints no ticket");
bool threw = false;
try { deniedAdmission.RequireTicket(); } catch (InvalidOperationException) { threw = true; }
Check(threw, "admission: RequireTicket() throws instead of handing out a fake ticket");

// ---------------------------------------------------------------- downgrade-erase (D-13)
// "Enable Downgrade" erases four oeminfo version fields. The registry used to call that PlanOnly while
// the GUI erased them with no gate at all, so both halves are pinned here: the request is a redline,
// irreversible one that needs the typed phrase, and the service method is unreachable without a ticket
// minted for that exact capability.
ApprovalRequest DowngradeErase() => ApprovalRequests.For("downgrade-erase",
    argv: new[] { "fastboot", "oem", "oeminfoerase-amssver" }, targets: new[] { "oeminfo" }, sessionId: sessionId);
Check(DowngradeErase().Kind == CapabilityApprovalKind.RedlineTypedConfirm, "downgrade-erase: registry declares RedlineTypedConfirm");
Check(DowngradeErase().RequiresTypedConfirm, "downgrade-erase: a request for it requires the typed confirm phrase");
Check(DowngradeErase().ConfirmPhrase == "I UNDERSTAND downgrade-erase", "downgrade-erase: confirm phrase is per capability");
Check(DowngradeErase().Reversibility == ApprovalReversibility.Irreversible, "downgrade-erase: declared irreversible");
Check(DowngradeErase().TouchesRedline, "downgrade-erase: touches a redline target (oeminfo)");
Check(CapabilityRegistry.ById("downgrade-erase")?.ServiceMethod == "DeviceWriteOps.EnableDowngradeAsync",
    "downgrade-erase: names DeviceWriteOps.EnableDowngradeAsync as the service method all faces share");
Check(CapabilityRegistry.ById("downgrade-enable")?.WriteKind == CapabilityWriteKind.PlanOnly,
    "downgrade-enable stays PlanOnly (it prints the 4 commands and writes nothing)");

var downgradeOps = new Kirin_Tool.Services.WriteOperations.DeviceWriteOps(new Kirin_Tool.Services.FastbootClient("fastboot"));
bool withoutTicket = false;
try { await downgradeOps.EnableDowngradeAsync(null!); }
catch (InvalidOperationException ex) when (ex.Message.Contains("downgrade-erase", StringComparison.Ordinal)) { withoutTicket = true; }
Check(withoutTicket, "DeviceWriteOps.EnableDowngradeAsync: without an approved WriteTicket it throws (no bypass)");

var downgradeApprover = new GuiApprovalGate(new ScriptedPrompt(new ApprovalPromptAnswer(ApprovalPromptOutcome.Approved)), null, new MemoryApprovalAuditSink());
ApprovalGates.Install(downgradeApprover);
ApprovalAdmission otherCapability = await ApprovalGuard.AdmitAsync(
    ApprovalRequests.For("reboot", argv: new[] { "fastboot", "reboot" }, targets: Array.Empty<string>(),
        sessionId: "downgrade-ticket-" + Guid.NewGuid().ToString("n")), CancellationToken.None);
bool wrongTicket = false;
try { await downgradeOps.EnableDowngradeAsync(otherCapability.RequireTicket()); }
catch (InvalidOperationException ex) when (ex.Message.Contains("cannot authorize", StringComparison.Ordinal)) { wrongTicket = true; }
Check(wrongTicket, "DeviceWriteOps.EnableDowngradeAsync: a 'reboot' ticket cannot authorize the erase");

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine(failures.Count + " approval-gate check(s) failed / " + checks + " total:");
    foreach (string failure in failures) Console.WriteLine("  - " + failure);
    return 1;
}
Console.WriteLine(checks + " approval-gate checks passed.");
return 0;

// ---------------------------------------------------------------- test doubles
sealed class ScriptedPrompt : IApprovalPrompt
{
    private readonly Queue<ApprovalPromptAnswer> _answers;
    public ScriptedPrompt(params ApprovalPromptAnswer[] answers) => _answers = new Queue<ApprovalPromptAnswer>(answers);
    public int Asked { get; private set; }
    public ApprovalPromptModel? LastModel { get; private set; }

    public Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken)
    {
        Asked++;
        LastModel = model;
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : new ApprovalPromptAnswer(ApprovalPromptOutcome.Denied, null, "scripted deny"));
    }
}

sealed class NeverPrompt : IApprovalPrompt
{
    public Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken) =>
        new TaskCompletionSource<ApprovalPromptAnswer>().Task; // never completes: the gate must time out
}

sealed class NoGuiBridge : IGuiApprovalBridge
{
    public string Name => "no-gui";
    public bool IsGuiRunning() => false;
    public Task<GuiApprovalAnswer> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        throw new GuiNotRunningException("test: no GUI");
}
