namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// Verdict for an NVE variable write (currently the serial number). fastboot's exit code only
/// says the command was not rejected locally; the audit (F04/S18) found the GUI printing
/// "Successfully rewrote the device's serial number!" off that exit code alone. A write is
/// <c>Verified</c> only when the value reads back identical.
/// </summary>
public sealed record NveWriteVerdict(bool Verified, string Reason, string Expected, string? ReadBack, string? ReadBackError = null)
{
    public string ReadBackDisplay => string.IsNullOrEmpty(ReadBack) ? "(no value read back)" : ReadBack;

    public string Explain => Verified
        ? $"The device reads back the new value ({ReadBackDisplay}), so the write took effect."
        : Reason switch
        {
            "command_failed" => "fastboot reported a failure for the write command itself; nothing was confirmed.",
            "readback_unavailable" => $"The value could not be read back after the write, so it is NOT confirmed ({ReadBackError ?? ReadBackDisplay}).",
            _ => $"The write did NOT take effect: the device still reads back '{ReadBackDisplay}'.",
        };
}

public static class NveWriteCheck
{
    public const string ReadbackOk = "readback_ok";
    public const string CommandFailed = "command_failed";
    public const string ReadbackUnavailable = "readback_unavailable";

    /// <summary>
    /// Compares what was written with what the device reports afterwards. <paramref name="readBackError"/>
    /// carries the read failure (if any) so "could not read" is never folded into "matches".
    /// </summary>
    public static NveWriteVerdict Compare(string expected, string? readBack, bool commandSucceeded, string? readBackError = null)
    {
        string wanted = (expected ?? string.Empty).Trim();
        string? got = readBack?.Trim();

        if (!commandSucceeded)
            return new NveWriteVerdict(false, CommandFailed, wanted, got);

        if (readBackError != null || string.IsNullOrEmpty(got) || got.Contains("(read failed", StringComparison.OrdinalIgnoreCase))
            return new NveWriteVerdict(false, ReadbackUnavailable, wanted, string.IsNullOrEmpty(got) ? null : got, readBackError);

        if (string.Equals(wanted, got, StringComparison.Ordinal))
            return new NveWriteVerdict(true, ReadbackOk, wanted, got);

        return new NveWriteVerdict(false, $"readback_mismatch:{got}", wanted, got);
    }
}
