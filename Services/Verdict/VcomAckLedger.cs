// Verdict honesty layer: per-frame ACK accounting for the VCOM chain.
// Deliberately outside Services/VcomFlasher.cs (a frozen stable-boundary file): the wire
// protocol is untouched, this only *counts* what the flasher already reports through
// VcomFlasherOptions.FrameObserver.

namespace Kirin_Tool.Services.Verdict;

/// <summary>
/// Frame/ACK totals. <see cref="FramesExpectingAck"/> excludes the frames that are never
/// acknowledged by design (START, TAIL, the second exploit HEAD), so <see cref="MissingRatio"/>
/// cannot be diluted by them.
/// </summary>
public sealed record VcomAckCounts(
    int FramesSent,
    int FramesExpectingAck,
    int Acked,
    int Bad,
    int Timeouts,
    int NotExpectingAck)
{
    public static readonly VcomAckCounts Empty = new(0, 0, 0, 0, 0, 0);

    /// <summary>Frames that expected an ACK and did not get a usable one (bad byte or silence).</summary>
    public int Missing => Bad + Timeouts;

    public double MissingRatio => FramesExpectingAck == 0 ? 0d : (double)Missing / FramesExpectingAck;
}

/// <summary>
/// The conclusion of one VCOM run, split into the two orthogonal facts:
/// <c>Applied</c> (this host really wrote the frames) and <c>Verified</c> (the device said so).
/// A run can be applied but unverified — and then it must not be reported as a success.
/// </summary>
public sealed record VcomVerdict(
    bool Applied,
    bool Verified,
    string Reason,
    VcomAckCounts Counts)
{
    public int FramesSent => Counts.FramesSent;
    public int Acked => Counts.Acked;
    public int Missing => Counts.Missing;
    public double MissingRatio => Counts.MissingRatio;

    /// <summary><c>verified</c> / <c>applied-unverified</c> / <c>failed</c>.</summary>
    public string Conclusion => !Applied ? "failed" : Verified ? "verified" : "applied-unverified";

    /// <summary>Machine-readable gate string: <c>applied</c> / <c>applied-unverified</c> / <c>failed</c>.</summary>
    public string Gate => !Applied ? "failed" : Verified ? "applied" : "applied-unverified";

    public static VcomVerdict NotAttempted(string reason = "not_attempted") =>
        new(false, false, reason, VcomAckCounts.Empty);

    /// <summary>
    /// Derives the verdict from the ledger + the ACK policy that was in force. The ACK policy
    /// never changes the *counts*; it only decides whether the counts are evidence at all
    /// (<c>Off</c> means nothing was ever read back, so nothing can be verified).
    /// </summary>
    public static VcomVerdict From(VcomAckCounts counts, VcomAckMode mode, bool applied = true)
    {
        if (!applied) return new VcomVerdict(false, false, "not_attempted", counts);
        if (mode == VcomAckMode.Off) return new VcomVerdict(true, false, "ack_not_requested", counts);
        if (counts.FramesExpectingAck == 0) return new VcomVerdict(true, false, "ack_no_frames", counts);
        if (counts.Missing > 0) return new VcomVerdict(true, false, $"ack_missing:{counts.Missing}", counts);
        return new VcomVerdict(true, true, "ack_ok", counts);
    }

    /// <summary>A run that did not complete: nothing to verify, the reason names the failure.</summary>
    public static VcomVerdict Failed(VcomAckCounts counts, string reason) => new(false, false, reason, counts);
}

/// <summary>
/// Counts the frames the flasher reports. One instance is attached to
/// <see cref="VcomFlasherOptions.FrameObserver"/>; <see cref="EnterStage"/> delimits the
/// per-stage numbers (addresses alone are ambiguous: on hisi970..990 the <c>null</c> stage and
/// the <c>XLOADER</c> stage both load at 0x22000). Thread-safe: the observer fires from the
/// transfer loop, <see cref="Stages"/> is read afterwards.
/// </summary>
public sealed class VcomAckLedger
{
    private readonly object _gate = new();
    private readonly List<(string Name, Mutable Counts)> _stages = new();
    private readonly Mutable _total = new();
    private string _current = "(pre-stage)";

    /// <summary>Name of the stage the next frames belong to (the loader name; START keeps the default).</summary>
    public void EnterStage(string stageName)
    {
        lock (_gate)
        {
            _current = string.IsNullOrWhiteSpace(stageName) ? "(pre-stage)" : stageName;
        }
    }

    /// <summary>The <see cref="VcomFlasherOptions.FrameObserver"/> sink.</summary>
    public void OnFrame(VcomFrameInfo frame)
    {
        if (frame == null) return;
        lock (_gate)
        {
            Apply(_total, frame);
            Apply(For(_current), frame);
        }
    }

    public VcomAckCounts Total
    {
        get { lock (_gate) return _total.Snapshot(); }
    }

    /// <summary>Per-stage counts in the order the stages ran (including <c>(pre-stage)</c> for START).</summary>
    public IReadOnlyList<(string Stage, VcomAckCounts Counts)> Stages
    {
        get
        {
            lock (_gate)
                return _stages.Select(stage => (stage.Name, stage.Counts.Snapshot())).ToList();
        }
    }

    public VcomAckCounts ForStage(string stageName)
    {
        lock (_gate)
        {
            int index = _stages.FindIndex(stage => string.Equals(stage.Name, stageName, StringComparison.Ordinal));
            return index < 0 ? VcomAckCounts.Empty : _stages[index].Counts.Snapshot();
        }
    }

    private Mutable For(string name)
    {
        int index = _stages.FindIndex(stage => string.Equals(stage.Name, name, StringComparison.Ordinal));
        if (index >= 0) return _stages[index].Counts;
        var created = new Mutable();
        _stages.Add((name, created));
        return created;
    }

    private static void Apply(Mutable counts, VcomFrameInfo frame)
    {
        counts.FramesSent++;
        switch (frame.AckStatus)
        {
            case "ok":
                counts.FramesExpectingAck++;
                counts.Acked++;
                break;
            case "bad":
                counts.FramesExpectingAck++;
                counts.Bad++;
                break;
            case "timeout":
                counts.FramesExpectingAck++;
                counts.Timeouts++;
                break;
            default:
                // "not-expected" (START / TAIL / exploit HEAD) and "skipped" (AckMode=Off).
                counts.NotExpectingAck++;
                break;
        }
    }

    private sealed class Mutable
    {
        public int FramesSent;
        public int FramesExpectingAck;
        public int Acked;
        public int Bad;
        public int Timeouts;
        public int NotExpectingAck;

        public VcomAckCounts Snapshot() => new(FramesSent, FramesExpectingAck, Acked, Bad, Timeouts, NotExpectingAck);
    }
}
