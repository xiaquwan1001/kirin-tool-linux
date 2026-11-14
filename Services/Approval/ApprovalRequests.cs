// Licensed under the repository's Business Source License 1.1. See LICENSE.

using Kirin_Tool.Services.Capabilities;

namespace Kirin_Tool.Services.Approval;

/// <summary>Which targets count as "redline" (unrecoverable / identity / boot chain).</summary>
public static class ApprovalPolicy
{
    private static readonly string[] RedlineTokens =
    {
        "oeminfo", "nvme", "persist", "modem_secure", "modemnvm", "certification",
        "hisee_encos", "veritykey", "frp", "config", "erecovery",
    };

    public static bool IsRedlineTarget(string target) =>
        !string.IsNullOrWhiteSpace(target) &&
        RedlineTokens.Any(token => target.Contains(token, StringComparison.OrdinalIgnoreCase));

    public static bool TouchesRedline(IEnumerable<string>? targets) =>
        targets != null && targets.Any(IsRedlineTarget);
}

/// <summary>
/// Builds an <see cref="ApprovalRequest"/> from the capability registry (static facts: title,
/// tool, declared approval kind, reversibility, side effects) plus the call site's dynamic facts
/// (argv, targets). Every face uses this, so the dialog/audit content cannot diverge between
/// GUI, CLI and MCP.
/// </summary>
public static class ApprovalRequests
{
    public static ApprovalRequest For(
        string capabilityId,
        IEnumerable<string?>? argv = null,
        IEnumerable<string>? targets = null,
        bool? touchesRedline = null,
        ApprovalReversibility? reversibility = null,
        bool acknowledged = false,
        bool redlineOverride = false,
        string? sessionId = null,
        int? timeoutSeconds = null,
        IEnumerable<string>? summary = null)
    {
        Capability capability = CapabilityRegistry.ById(capabilityId)
            ?? throw new ArgumentException("capability '" + capabilityId + "' is not registered in CapabilityRegistry", nameof(capabilityId));

        IReadOnlyList<string> targetList = (targets ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        IReadOnlyList<string> argvList = ApprovalRedaction.Redact(argv ?? Array.Empty<string?>());
        bool redline = touchesRedline ?? ApprovalPolicy.TouchesRedline(targetList);

        return new ApprovalRequest
        {
            CapabilityId = capability.Id,
            TitleZh = capability.NameZh,
            Summary = (summary ?? capability.SideEffects).ToList(),
            Tool = capability.Tool,
            Argv = argvList,
            Targets = targetList,
            TouchesRedline = redline,
            Reversibility = reversibility ?? capability.Reversibility,
            Origin = ApprovalGates.Origin,
            SessionId = sessionId ?? "",
            TimeoutSeconds = timeoutSeconds ?? 300,
            Kind = capability.Approval,
            Acknowledged = acknowledged,
            RedlineOverride = redlineOverride,
        };
    }
}
