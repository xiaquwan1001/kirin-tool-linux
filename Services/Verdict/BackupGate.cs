namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// The pre-op backup gate, extracted from the CLI so it can be checked without a device.
/// Audit S10/S11: <c>--require-backup</c> used to pass silently when the backup directory
/// could not be created at all — exactly the case the gate exists for ("the dumps did not
/// happen" was treated as "no dumps were needed").
/// </summary>
public sealed record BackupGateVerdict(
    bool Refuse,
    int ExitCode,
    string Reason,
    bool Attempted,
    bool Complete,
    bool SkippedByRequest);

public static class BackupGate
{
    /// <summary>Mirrors Cli.ExitCodes.Gated (gate refused, nothing was written).</summary>
    public const int RefusalExitCode = 4;

    public static BackupGateVerdict Evaluate(bool requireBackup, PreOpBackupResult result)
    {
        bool skipped = !result.Attempted && string.Equals(result.Error, PreOpBackupResult.Skipped.Error, StringComparison.Ordinal);

        if (result.Complete)
            return new BackupGateVerdict(false, 0, "backup complete", result.Attempted, true, skipped);

        if (!requireBackup)
            return new BackupGateVerdict(false, 0,
                result.Attempted
                    ? $"backup incomplete ({result.OkCount}/{result.TotalCount})"
                    : $"backup not taken ({result.Error ?? "not attempted"})",
                result.Attempted, false, skipped);

        // --require-backup: "not attempted" is a refusal too. The only non-refusal is a complete
        // backup; there is no third state in which a required backup may be ignored.
        string reason = !result.Attempted
            ? $"the pre-op backup was never taken ({result.Error ?? "not attempted"})"
            : $"the pre-op backup is incomplete ({result.OkCount}/{result.TotalCount})";
        return new BackupGateVerdict(true, RefusalExitCode, reason, result.Attempted, false, skipped);
    }
}
