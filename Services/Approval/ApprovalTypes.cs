// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Kirin_Tool.Services.Approval;

/// <summary>
/// How hard an entry point has to work before a capability may run. Declared per capability in
/// <see cref="Capabilities.CapabilityRegistry"/> and enforced by <see cref="IApprovalGate"/>.
/// </summary>
public enum CapabilityApprovalKind
{
    /// <summary>No device write: the gate is never consulted.</summary>
    None,
    /// <summary>One human acknowledgement (GUI button / CLI --yes / MCP confirm + out-of-band approval).</summary>
    Once,
    /// <summary>Redline or irreversible: the human must type a per-capability confirm phrase.</summary>
    RedlineTypedConfirm,
}

/// <summary>Which face asked for the approval.</summary>
public enum ApprovalOrigin
{
    Gui,
    Cli,
    Mcp,
}

/// <summary>Can the write be undone by this tool? Drives the typed-confirm requirement.</summary>
public enum ApprovalReversibility
{
    Reversible,
    Irreversible,
}

/// <summary>How long an approval is good for.</summary>
public enum ApprovalScope
{
    Once,
    Session,
}

/// <summary>
/// Everything a human needs to judge one request, and nothing that leaks a secret. Built by
/// <see cref="ApprovalRequests.For"/> from the capability registry plus the call site's facts.
/// </summary>
public sealed record ApprovalRequest
{
    public string CapabilityId { get; init; } = "";
    public string TitleZh { get; init; } = "";
    public IReadOnlyList<string> Summary { get; init; } = Array.Empty<string>();
    /// <summary>fastboot | vcom | internal</summary>
    public string Tool { get; init; } = "fastboot";
    /// <summary>Redacted argv, one token per list item, shown verbatim to the human.</summary>
    public IReadOnlyList<string> Argv { get; init; } = Array.Empty<string>();
    /// <summary>Partitions / objects that will be written.</summary>
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
    public bool TouchesRedline { get; init; }
    public ApprovalReversibility Reversibility { get; init; } = ApprovalReversibility.Reversible;
    public ApprovalOrigin Origin { get; init; } = ApprovalOrigin.Gui;
    public string SessionId { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public CapabilityApprovalKind Kind { get; init; } = CapabilityApprovalKind.Once;

    /// <summary>--yes (CLI) / confirm:true (MCP). Never set by the GUI face: the dialog is the acknowledgement.</summary>
    public bool Acknowledged { get; init; }

    /// <summary>--allow-redline (CLI only). MCP refuses this argument at the protocol layer.</summary>
    public bool RedlineOverride { get; init; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds <= 0 ? 300 : TimeoutSeconds);

    /// <summary>
    /// Redline / irreversible requests must be confirmed by typing, not by clicking: a bool flag
    /// that a script can set is not evidence that a human read the consequences.
    /// </summary>
    public bool RequiresTypedConfirm =>
        Kind == CapabilityApprovalKind.RedlineTypedConfirm ||
        TouchesRedline ||
        Reversibility == ApprovalReversibility.Irreversible;

    /// <summary>The exact string the human has to type. Per-capability so muscle memory cannot carry over.</summary>
    public string ConfirmPhrase => "I UNDERSTAND " + CapabilityId;

    /// <summary>
    /// Stable identity of "this same write". Session reuse and duplicate-request detection key off
    /// it, so a replayed request can never be silently re-authorized by an unrelated approval.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append(CapabilityId).Append('\u001f').Append(SessionId).Append('\u001f').Append(Kind);
            foreach (string token in Argv) builder.Append('\u001f').Append(token);
            builder.Append('\u001e');
            foreach (string target in Targets) builder.Append('\u001f').Append(target);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
            return Convert.ToHexString(hash)[..32].ToLowerInvariant();
        }
    }
}

/// <summary>
/// The gate's verdict. Only <see cref="Approved"/> can mint a <see cref="WriteTicket"/>; every
/// other case is a refusal with a machine-readable reason.
/// </summary>
public abstract record ApprovalDecision
{
    public abstract bool IsApproved { get; }

    /// <summary>Machine-readable token used in audits, JSON output and tests.</summary>
    public abstract string Code { get; }

    public sealed record Approved(ApprovalScope Scope, bool Reused = false) : ApprovalDecision
    {
        public override bool IsApproved => true;
        public override string Code => "approved";
    }

    /// <summary>Exit code 4 ("gate refused") is the CLI default for a denial.</summary>
    public sealed record Denied(string Reason, int ExitCode = 4) : ApprovalDecision
    {
        public override bool IsApproved => false;
        public override string Code => "denied";
    }

    /// <summary>A silent dialog is not consent.</summary>
    public sealed record TimedOut(TimeSpan Waited) : ApprovalDecision
    {
        public override bool IsApproved => false;
        public override string Code => "timed_out";
    }

    /// <summary>
    /// This entry point cannot obtain a human decision on its own; the caller has to switch
    /// entry point (or start the GUI). Carries the instruction the AI/client should show.
    /// </summary>
    public sealed record NotSupported(string Entry, string Instruction, string ErrorCode = "approval_required_human") : ApprovalDecision
    {
        public override bool IsApproved => false;
        public override string Code => "not_supported";
    }
}

/// <summary>
/// Proof that an approval gate said yes to one specific <see cref="ApprovalRequest"/>.
/// The constructor is internal to this assembly *and* only reachable from
/// <see cref="ApprovalGuard"/>, so a write service method that demands a ticket cannot be called
/// without going through the gate — enforced by the compiler, not by review.
/// </summary>
public sealed class WriteTicket
{
    private WriteTicket(string capabilityId, ApprovalScope scope, string fingerprint)
    {
        CapabilityId = capabilityId;
        Scope = scope;
        Fingerprint = fingerprint;
    }

    public string CapabilityId { get; }
    public ApprovalScope Scope { get; }
    public string Fingerprint { get; }

    internal static WriteTicket Mint(ApprovalRequest request, ApprovalScope scope) =>
        new(request.CapabilityId, scope, request.Fingerprint);
}

/// <summary>Decision + request + (only on approval) the ticket the service layer demands.</summary>
public sealed record ApprovalAdmission(ApprovalDecision Decision, ApprovalRequest Request, WriteTicket? Ticket)
{
    public bool IsApproved => Decision.IsApproved && Ticket is not null;

    public WriteTicket RequireTicket() => Ticket
        ?? throw new InvalidOperationException(
            "approval was not granted for '" + Request.CapabilityId + "' (" + Decision.Code + "); no WriteTicket was minted");
}

/// <summary>One approval gate per entry point.</summary>
public interface IApprovalGate
{
    ApprovalOrigin Origin { get; }

    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>What a prompt can answer.</summary>
public enum ApprovalPromptOutcome
{
    Approved,
    ApprovedForSession,
    Denied,
    TimedOut,
}

/// <summary>One answer from the human-facing prompt.</summary>
public sealed record ApprovalPromptAnswer(ApprovalPromptOutcome Outcome, string? TypedConfirm = null, string? Reason = null);

/// <summary>What the prompt is asked to display.</summary>
public sealed record ApprovalPromptModel(ApprovalRequest Request, bool RequiresTypedConfirm, string ConfirmPhrase);

/// <summary>
/// The presentation seam: the GUI dialog implements this over Avalonia, tests implement it in
/// memory. Keeping it separate is what makes the gate testable without a display.
/// </summary>
public interface IApprovalPrompt
{
    Task<ApprovalPromptAnswer> AskAsync(ApprovalPromptModel model, CancellationToken cancellationToken);
}

/// <summary>
/// Session-scoped, fingerprint-keyed reuse of an approval: "本会话批准" for the GUI, and the
/// idempotency guarantee for repeated identical requests (test ⑥).
/// </summary>
public sealed class SessionApprovalCache
{
    private readonly ConcurrentDictionary<string, ApprovalScope> _approved = new(StringComparer.Ordinal);

    public bool TryReuse(string fingerprint) => _approved.ContainsKey(fingerprint);

    public void Remember(string fingerprint, ApprovalScope scope) => _approved[fingerprint] = scope;

    public int Count => _approved.Count;

    public void Clear() => _approved.Clear();

    public string SessionId { get; } = Guid.NewGuid().ToString("n");
}

/// <summary>argv redaction: the human must see *what* will be written, not the secret value itself.</summary>
public static class ApprovalRedaction
{
    private static readonly string[] SensitiveFlags = { "value", "sn", "serial", "serialno", "nve" };

    public static IReadOnlyList<string> Redact(IEnumerable<string> argv)
    {
        var tokens = argv.Where(token => token != null).ToList();
        var redacted = new List<string>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            string current = tokens[i];
            string bare = current.TrimStart('-');
            bool sensitiveFlag = SensitiveFlags.Contains(bare, StringComparer.OrdinalIgnoreCase);
            if (sensitiveFlag)
            {
                redacted.Add(current);
                if (i + 1 < tokens.Count) redacted.Add("<redacted>");
                i++;
                continue;
            }

            int equals = bare.IndexOf('=');
            if (equals > 0 && SensitiveFlags.Contains(bare[..equals], StringComparer.OrdinalIgnoreCase))
            {
                redacted.Add(current[..(current.Length - bare.Length + equals + 1)] + "<redacted>");
                continue;
            }

            redacted.Add(LooksLikeSerial(current) ? "<redacted>" : current);
        }
        return redacted;
    }

    /// <summary>Long mixed alphanumeric tokens are serial-number shaped; keep them out of the audit log.</summary>
    private static bool LooksLikeSerial(string token)
    {
        if (token.Length < 10) return false;
        bool digit = token.Any(char.IsDigit);
        bool letter = token.Any(char.IsLetter);
        return digit && letter && !token.Contains('/') && !token.Contains(' ') && !token.EndsWith(".img", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Installs the gate for the current entry point and mints tickets (the only ticket source).</summary>
public static class ApprovalGates
{
    private static IApprovalGate _current = new UnsupportedApprovalGate();

    /// <summary>Gate for the current entry point. Fail-closed by default: nothing is approved.</summary>
    public static IApprovalGate Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static ApprovalOrigin Origin => _current.Origin;

    public static void Install(IApprovalGate gate) => Current = gate;

    public static void ResetToFailClosed() => _current = new UnsupportedApprovalGate();
}

/// <summary>
/// The only way to obtain a <see cref="WriteTicket"/>. Every write face calls this immediately
/// before calling the shared service method, so "did this path go through the gate?" has exactly
/// one structural answer.
/// </summary>
public static class ApprovalGuard
{
    public static async Task<ApprovalAdmission> AdmitAsync(ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        IApprovalGate gate = ApprovalGates.Current;
        ApprovalDecision decision = await gate.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        WriteTicket? ticket = decision is ApprovalDecision.Approved approved
            ? WriteTicket.Mint(request, approved.Scope)
            : null;
        return new ApprovalAdmission(decision, request, ticket);
    }
}

/// <summary>
/// Fallback gate: if a process forgot to install one, writes are refused instead of allowed.
/// </summary>
public sealed class UnsupportedApprovalGate : IApprovalGate
{
    public ApprovalOrigin Origin => ApprovalOrigin.Cli;

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ApprovalDecision>(new ApprovalDecision.NotSupported(
            "none",
            "no approval gate is installed in this process; write capabilities are disabled (fail-closed)"));
}
