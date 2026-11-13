// Licensed under the repository's Business Source License 1.1. See LICENSE.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kirin_Tool.Services.Approval;

/// <summary>One audit line per approval decision (JSONL, one object per line).</summary>
public sealed record ApprovalAuditEntry
{
    public string Ts { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public string Event { get; init; } = "approval.decision";
    public string CapabilityId { get; init; } = "";
    public string TitleZh { get; init; } = "";
    public string Origin { get; init; } = "";
    public string Decision { get; init; } = "";
    public string? Scope { get; init; }
    public bool Reused { get; init; }
    public bool TouchesRedline { get; init; }
    public string Reversibility { get; init; } = "";
    public bool TypedConfirm { get; init; }
    public IReadOnlyList<string> Argv { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Targets { get; init; } = Array.Empty<string>();
    public string Fingerprint { get; init; } = "";
    public string DecisionId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string Tool { get; init; } = "";
    public string? Reason { get; init; }

    public static ApprovalAuditEntry From(ApprovalRequest request, ApprovalDecision decision, bool reused, bool typedConfirm) => new()
    {
        CapabilityId = request.CapabilityId,
        TitleZh = request.TitleZh,
        Origin = request.Origin switch
        {
            ApprovalOrigin.Gui => "gui",
            ApprovalOrigin.Cli => "cli",
            _ => "mcp",
        },
        Decision = decision.Code,
        Scope = decision is ApprovalDecision.Approved approved
            ? (approved.Scope == ApprovalScope.Session ? "session" : "once")
            : null,
        Reused = reused,
        TouchesRedline = request.TouchesRedline,
        Reversibility = request.Reversibility == ApprovalReversibility.Irreversible ? "irreversible" : "reversible",
        TypedConfirm = typedConfirm,
        Argv = request.Argv,
        Targets = request.Targets,
        Fingerprint = request.Fingerprint,
        DecisionId = request.Fingerprint,
        SessionId = request.SessionId,
        Tool = request.Tool,
        Reason = decision switch
        {
            ApprovalDecision.Denied denied => denied.Reason,
            ApprovalDecision.NotSupported notSupported => notSupported.ErrorCode + ": " + notSupported.Instruction,
            ApprovalDecision.TimedOut timedOut => "timed out after " + (int)timedOut.Waited.TotalSeconds + "s",
            _ => null,
        },
    };
}

/// <summary>
/// The only exit point for approval decisions. Adapters (lane D op-logging, tests, GUI log pane)
/// implement this instead of touching the gates.
/// </summary>
public interface IApprovalAuditSink
{
    void Record(ApprovalAuditEntry entry);
}

/// <summary>Append-only JSONL sink. IO failures never break a device operation (diagnostic only).</summary>
public sealed class JsonlApprovalAuditSink : IApprovalAuditSink
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public JsonlApprovalAuditSink(string path) => Path = path;

    public string Path { get; }

    public void Record(ApprovalAuditEntry entry)
    {
        try
        {
            lock (_gate)
            {
                string? directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(Path, JsonSerializer.Serialize(entry, Options) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("ApprovalAudit", ex.ToString());
        }
    }
}

/// <summary>In-memory sink for tests and for the GUI log pane.</summary>
public sealed class MemoryApprovalAuditSink : IApprovalAuditSink
{
    private readonly List<ApprovalAuditEntry> _entries = new();

    public IReadOnlyList<ApprovalAuditEntry> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public void Record(ApprovalAuditEntry entry)
    {
        lock (_entries) _entries.Add(entry);
    }
}

/// <summary>Discards everything (used by read-only/plan-only processes).</summary>
public sealed class NullApprovalAuditSink : IApprovalAuditSink
{
    public void Record(ApprovalAuditEntry entry) { }
}

/// <summary>Process-wide default sink (JSONL under the user's state dir unless overridden).</summary>
public static class ApprovalAudit
{
    private static IApprovalAuditSink? _sink;

    /// <summary>Test/seam override; `KIRIN_APPROVAL_AUDIT` redirects the default file.</summary>
    public static IApprovalAuditSink Sink
    {
        get => _sink ??= new JsonlApprovalAuditSink(DefaultPath);
        set => _sink = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static string DefaultPath
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("KIRIN_APPROVAL_AUDIT");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden!;

            string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                state = System.IO.Path.Combine(home, ".local", "state");
            }
            return System.IO.Path.Combine(state!, "kirin-tool", "approvals.jsonl");
        }
    }
}
