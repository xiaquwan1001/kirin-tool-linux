namespace Kirin_Tool.Services.Verdict;

/// <summary>Shared human-readable strings for the verdict layer (GUI dialog + CLI stderr).</summary>
public static class UnlockVerdictText
{
    /// <summary>Stable reason string for a run that stopped before it could produce its own verdict.</summary>
    public static string FailureReason(UnlockFailureKind kind) => kind switch
    {
        UnlockFailureKind.MissingLoader => "missing_loader",
        UnlockFailureKind.NoDevice => "no_device",
        UnlockFailureKind.DeviceRejected => "device_rejected",
        UnlockFailureKind.Cancelled => "cancelled",
        UnlockFailureKind.LocalFile => "local_file",
        _ => "error",
    };

    /// <summary>"frames 42, acked 40, missing 2/40" — the numbers the verdict was derived from.</summary>
    public static string Evidence(VcomAckCounts counts)
    {
        if (counts.FramesExpectingAck == 0)
            return $"frames sent {counts.FramesSent}, no frame expected an ACK";
        return $"frames sent {counts.FramesSent}, ACKed {counts.Acked}, missing {counts.Missing}/{counts.FramesExpectingAck} " +
               $"({counts.MissingRatio:P0} missing)";
    }

    /// <summary>One line that never says "success" unless the device answered.</summary>
    public static string Explain(VcomVerdict verdict) => verdict switch
    {
        { Applied: false } => $"Nothing was uploaded ({verdict.Reason}).",
        { Verified: true } => $"Loaders uploaded and the device ACKed every frame ({verdict.Reason}).",
        { Reason: "ack_not_requested" } => "Loaders uploaded, but no ACK was ever read (--ack-mode off), so the device never confirmed anything.",
        _ => $"Loaders uploaded but NOT verified ({verdict.Reason}); the bootloader state was not read back.",
    };
}
