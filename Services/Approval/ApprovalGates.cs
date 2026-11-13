// Licensed under the repository's Business Source License 1.1. See LICENSE.

namespace Kirin_Tool.Services.Approval;

/// <summary>One gate attempt: the verdict plus whether a typed confirm phrase was actually entered.</summary>
public sealed record ApprovalAttempt(ApprovalDecision Decision, bool TypedConfirm = false);

/// <summary>
/// Shared gate machinery: session reuse (idempotent identical requests), a real timeout that
/// denies instead of assuming consent, and the single audit write per decision.
/// </summary>
public abstract class ApprovalGateBase : IApprovalGate
{
    private readonly SessionApprovalCache? _cache;
    private readonly IApprovalAuditSink _audit;

    protected ApprovalGateBase(ApprovalOrigin origin, SessionApprovalCache? cache = null, IApprovalAuditSink? audit = null)
    {
        Origin = origin;
        _cache = cache;
        _audit = audit ?? ApprovalAudit.Sink;
    }

    public ApprovalOrigin Origin { get; }

    public async Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Kind == CapabilityApprovalKind.None)
            return Record(request, new ApprovalDecision.Denied("capability declares no approval requirement", 4), reused: false, typedConfirm: false);

        // Same capability + same session + same argv + same targets => the human already answered
        // this exact question. Reuse it instead of prompting again (idempotency, test ⑥).
        if (_cache != null && _cache.TryReuse(request.Fingerprint))
            return Record(request, new ApprovalDecision.Approved(ApprovalScope.Once, Reused: true), reused: true, typedConfirm: false);

        ApprovalAttempt attempt = await DecideAsync(request, cancellationToken).ConfigureAwait(false);

        if (attempt.Decision is ApprovalDecision.Approved approved)
        {
            ApprovalScope scope = approved.Scope;
            if (_cache != null) _cache.Remember(request.Fingerprint, scope);
        }

        return Record(request, attempt.Decision, reused: false, typedConfirm: attempt.TypedConfirm);
    }

    /// <summary>Gate-specific decision logic; the timeout and the audit line are handled here.</summary>
    protected abstract Task<ApprovalAttempt> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Bounded wait: whatever the prompt does, the gate answers within <see cref="ApprovalRequest.Timeout"/>.
    /// A hung dialog is a refusal, never a silent yes.
    /// </summary>
    protected static async Task<ApprovalAttempt> BoundedAsync(ApprovalRequest request, Func<CancellationToken, Task<ApprovalAttempt>> body, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            Task<ApprovalAttempt> work = body(linked.Token);
            Task finished = await Task.WhenAny(work, Task.Delay(request.Timeout, cancellationToken)).ConfigureAwait(false);
            if (finished != work)
                return new ApprovalAttempt(new ApprovalDecision.TimedOut(request.Timeout));
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new ApprovalAttempt(new ApprovalDecision.TimedOut(request.Timeout));
        }
        catch (OperationCanceledException)
        {
            return new ApprovalAttempt(new ApprovalDecision.Denied("approval request cancelled by the caller", 4));
        }
    }

    private ApprovalDecision Record(ApprovalRequest request, ApprovalDecision decision, bool reused, bool typedConfirm)
    {
        try
        {
            _audit.Record(ApprovalAuditEntry.From(request, decision, reused, typedConfirm));
        }
        catch (Exception ex)
        {
            global::Kirin_Tool.Utils.ProcessRunner.WriteDiagnostic("ApprovalGate", ex.ToString());
        }
        return decision;
    }
}

/// <summary>
/// GUI gate: a modal dialog that lists the real argv and the side effects. Redline or
/// irreversible requests additionally demand the per-capability confirm phrase typed by hand.
/// </summary>
public sealed class GuiApprovalGate : ApprovalGateBase
{
    private readonly IApprovalPrompt _prompt;

    public GuiApprovalGate(IApprovalPrompt prompt, SessionApprovalCache? cache = null, IApprovalAuditSink? audit = null)
        : base(ApprovalOrigin.Gui, cache, audit)
    {
        _prompt = prompt;
    }

    protected override Task<ApprovalAttempt> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        BoundedAsync(request, async token =>
        {
            var model = new ApprovalPromptModel(request, request.RequiresTypedConfirm, request.ConfirmPhrase);
            ApprovalPromptAnswer answer = await _prompt.AskAsync(model, token).ConfigureAwait(false);

            switch (answer.Outcome)
            {
                case ApprovalPromptOutcome.Approved:
                case ApprovalPromptOutcome.ApprovedForSession:
                    if (request.RequiresTypedConfirm)
                    {
                        string typed = (answer.TypedConfirm ?? "").Trim();
                        if (!string.Equals(typed, request.ConfirmPhrase, StringComparison.Ordinal))
                        {
                            return new ApprovalAttempt(new ApprovalDecision.Denied(
                                "confirm phrase mismatch: expected '" + request.ConfirmPhrase + "'"));
                        }
                    }
                    return new ApprovalAttempt(
                        new ApprovalDecision.Approved(answer.Outcome == ApprovalPromptOutcome.ApprovedForSession ? ApprovalScope.Session : ApprovalScope.Once),
                        TypedConfirm: request.RequiresTypedConfirm);

                case ApprovalPromptOutcome.TimedOut:
                    return new ApprovalAttempt(new ApprovalDecision.TimedOut(request.Timeout));

                default:
                    return new ApprovalAttempt(new ApprovalDecision.Denied(answer.Reason ?? "denied in the approval dialog"));
            }
        }, cancellationToken);
}

/// <summary>
/// CLI gate: --yes for ordinary writes; redline writes additionally need --allow-redline *and*
/// the confirm phrase typed on stdin. A non-interactive stdin can never satisfy that, so it is
/// refused with exit 4 and a pointer to the GUI instead of being auto-approved.
/// </summary>
public sealed class CliApprovalGate : ApprovalGateBase
{
    private readonly TextReader _stdin;
    private readonly TextWriter? _prompt;
    private readonly bool _interactive;

    public CliApprovalGate(TextReader? stdin = null, TextWriter? prompt = null, bool? interactive = null,
        SessionApprovalCache? cache = null, IApprovalAuditSink? audit = null)
        : base(ApprovalOrigin.Cli, cache, audit)
    {
        _stdin = stdin ?? Console.In;
        _prompt = prompt ?? Console.Error;
        _interactive = interactive ?? !Console.IsInputRedirected;
    }

    /// <summary>Name of the readline-based test double hook (kept public for the harness).</summary>
    public bool IsInteractive => _interactive;

    protected override Task<ApprovalAttempt> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!request.Acknowledged)
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Denied(
                "write refused: pass --yes to acknowledge what will be written (nothing was changed)", 4)));
        }

        if (!request.RequiresTypedConfirm)
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Approved(ApprovalScope.Once)));

        if (!request.RedlineOverride)
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Denied(
                "write refused: this is a redline / irreversible operation; pass --allow-redline as well as --yes", 4)));
        }

        if (!_interactive)
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Denied(
                "write refused: a redline operation needs the confirm phrase typed on an interactive stdin. " +
                "Approve it in the GUI, or run this command in a terminal and type '" + request.ConfirmPhrase + "' (exit 4)", 4)));
        }

        _prompt?.WriteLine("approval required: " + request.TitleZh + " (" + request.CapabilityId + ")");
        foreach (string line in request.Summary) _prompt?.WriteLine("  " + line);
        if (request.Argv.Count > 0) _prompt?.WriteLine("  argv: " + string.Join(' ', request.Argv));
        if (request.Targets.Count > 0) _prompt?.WriteLine("  targets: " + string.Join(", ", request.Targets));
        if (request.Reversibility == ApprovalReversibility.Irreversible) _prompt?.WriteLine("  IRREVERSIBLE: this cannot be undone by this tool.");
        _prompt?.Write("type '" + request.ConfirmPhrase + "' to proceed: ");
        _prompt?.Flush();

        string typed = (_stdin.ReadLine() ?? "").Trim();
        if (!string.Equals(typed, request.ConfirmPhrase, StringComparison.Ordinal))
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Denied(
                "write refused: confirm phrase mismatch ('" + request.ConfirmPhrase + "' expected)", 4)));
        }

        return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Approved(ApprovalScope.Once), TypedConfirm: true));
    }
}

/// <summary>
/// MCP gate. confirm:true is the caller claiming it understands this is not a dry-run — it is
/// never the approval itself. The approval has to come from a human out of band: the request is
/// handed to the running GUI and answered there. No GUI in sight means a structured refusal
/// (approval_required_human), not a pass.
/// </summary>
public sealed class McpApprovalGate : ApprovalGateBase
{
    private readonly IGuiApprovalBridge _bridge;

    public McpApprovalGate(IGuiApprovalBridge bridge, SessionApprovalCache? cache = null, IApprovalAuditSink? audit = null)
        : base(ApprovalOrigin.Mcp, cache, audit)
    {
        _bridge = bridge;
    }

    protected override Task<ApprovalAttempt> DecideAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        if (!request.Acknowledged)
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.Denied(
                "confirm:true is required before the human approval step (nothing was changed)", 4)));
        }

        if (!_bridge.IsGuiRunning())
        {
            return Task.FromResult(new ApprovalAttempt(new ApprovalDecision.NotSupported(
                "mcp",
                "a human approval is required for '" + request.CapabilityId + "'. Start the GUI and approve the prompt there, " +
                "or run the CLI with --yes" + (request.RequiresTypedConfirm ? " --allow-redline" : "") + ".")));
        }

        return BoundedAsync(request, async token =>
        {
            try
            {
                GuiApprovalAnswer answer = await _bridge.RequestApprovalAsync(request, token).ConfigureAwait(false);
                return answer.Outcome switch
                {
                    ApprovalPromptOutcome.Approved => new ApprovalAttempt(
                        new ApprovalDecision.Approved(answer.Scope == ApprovalScope.Session ? ApprovalScope.Session : ApprovalScope.Once), answer.TypedConfirm),
                    ApprovalPromptOutcome.ApprovedForSession => new ApprovalAttempt(new ApprovalDecision.Approved(ApprovalScope.Session), answer.TypedConfirm),
                    ApprovalPromptOutcome.TimedOut => new ApprovalAttempt(new ApprovalDecision.TimedOut(request.Timeout)),
                    _ => new ApprovalAttempt(new ApprovalDecision.Denied(answer.Reason ?? "denied by the operator in the GUI")),
                };
            }
            catch (GuiNotRunningException)
            {
                return new ApprovalAttempt(new ApprovalDecision.NotSupported(
                    "mcp",
                    "a human approval is required for '" + request.CapabilityId + "' but the GUI is not running. " +
                    "Start the GUI and approve the prompt there, or run the CLI with --yes" +
                    (request.RequiresTypedConfirm ? " --allow-redline" : "") + "."));
            }
        }, cancellationToken);
    }
}
