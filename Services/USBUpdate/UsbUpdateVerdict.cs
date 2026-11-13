// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

/*
 * Copyright (c) 2026 Kethily Daniel & NDXCode. All rights reserved.
 *
 * Use of this software is governed by the Business Source License included
 * in the LICENSE file and at www.mariadb.com/bsl11.
 *
 * Change Date: Four years from the date each version of the Licensed Work
 * is first publicly distributed.
 *
 * On the Change Date, in accordance with the Business Source License,
 * use of this software will be governed by the GNU General Public License v3.0
 * or later (GPL-3.0-or-later).
 *
 * Contact Information: https://kirintool.cfd
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kirin_Tool.Services.USBUpdate
{
    /// <summary>
    /// What the USB-update channel actually established, and nothing else (audit D-04,
    /// docs/usb-update-verdict.md). There is deliberately no fourth "assume ok" value: a
    /// transfer that wrote nothing is <see cref="Failed"/>, a transfer that wrote some of the
    /// required partitions is <see cref="Partial"/>, and only a transfer whose every required
    /// partition was answered by the device on a handshaken link is <see cref="Verified"/>.
    /// </summary>
    public enum UsbUpdateOutcome
    {
        Failed = 0,
        Partial = 1,
        Verified = 2,
    }

    /// <summary>One explicit step of a USB-update transfer. Nothing in the channel degrades silently.</summary>
    public enum UsbUpdateEventKind
    {
        /// <summary>No expected handshake answer was observed (link is unconfirmed).</summary>
        HandshakeMissing = 0,
        /// <summary>The package authorization command got no expected answer.</summary>
        AuthorizationMissing = 1,
        /// <summary>The partition's head/data/tail were answered by the device.</summary>
        Written = 2,
        /// <summary>A precondition file was absent, so the partition was not attempted.</summary>
        Skipped = 3,
        /// <summary>No complete payload reached the device (0-byte image, absent image).</summary>
        Missing = 4,
        /// <summary>The image stream ended before its declared size (was: silent success).</summary>
        StreamEndedEarly = 5,
        /// <summary>The transfer stopped at a partition (the device rejected a command).</summary>
        TransferStopped = 6,
        /// <summary>Any other declared degradation (no verdict impact of its own).</summary>
        Degraded = 7,
    }

    /// <summary>
    /// A single degraded/skipped/missing/written fact, with the fields the operation log and the
    /// GUI need. <see cref="Token"/> is the log prefix (`ok`/`skipped`/`missing`/`degraded`).
    /// </summary>
    public sealed record UsbUpdateEvent(UsbUpdateEventKind Kind, string? Partition, string Reason, bool Required)
    {
        /// <summary>The op-log prefix this event must be written under.</summary>
        public string Token => Kind switch
        {
            UsbUpdateEventKind.Written => "ok",
            UsbUpdateEventKind.Skipped => "skipped",
            UsbUpdateEventKind.Missing => "missing",
            _ => "degraded",
        };

        /// <summary>A human/op-log line: `token: partition: reason`.</summary>
        public string Line => Token + ": "
            + (string.IsNullOrEmpty(Partition) ? string.Empty : Partition + ": ")
            + Reason;

        /// <summary>The structured side of the same fact (operation-log fields).</summary>
        public IReadOnlyList<KeyValuePair<string, object?>> Fields => new[]
        {
            new KeyValuePair<string, object?>("event", Kind.ToString().ToLowerInvariant()),
            new KeyValuePair<string, object?>("partition", Partition ?? string.Empty),
            new KeyValuePair<string, object?>("required", Required),
        };
    }

    /// <summary>A partition that was declared in the plan but never attempted.</summary>
    public sealed record UsbUpdateSkip(string Partition, string Reason, bool Required);

    /// <summary>
    /// The verdict of one USB-update transfer. Built only by <see cref="UsbUpdateVerdictBuilder"/>,
    /// so a caller cannot invent "verified" without the write/skip/missing facts behind it.
    /// </summary>
    public sealed record UsbUpdateVerdict(
        UsbUpdateOutcome Outcome,
        IReadOnlyList<string> RequiredPartitions,
        IReadOnlyList<string> Written,
        IReadOnlyList<UsbUpdateSkip> Skipped,
        IReadOnlyList<string> Missing,
        bool HandshakeEstablished,
        string Reason,
        IReadOnlyList<UsbUpdateEvent> Events)
    {
        /// <summary>Only a fully answered transfer on a handshaken link may claim success.</summary>
        public bool Verified => Outcome == UsbUpdateOutcome.Verified;

        /// <summary>Machine token: `verified` / `partial` / `failed`.</summary>
        public string Token => TokenOf(Outcome);

        /// <summary>The machine token of an outcome (one place decides the spelling).</summary>
        public static string TokenOf(UsbUpdateOutcome outcome) => outcome switch
        {
            UsbUpdateOutcome.Verified => "verified",
            UsbUpdateOutcome.Partial => "partial",
            _ => "failed",
        };

        /// <summary>How many of the required partitions reached the device.</summary>
        public int RequiredWrittenCount => RequiredPartitions.Count(name =>
            Written.Any(written => string.Equals(written, name, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// One line for the GUI and the CLI: the outcome, the counts of every category and the
        /// reason. Every class of degradation is visible here, not only in the log.
        /// </summary>
        public string Describe()
        {
            return "usb-update " + Token
                + ": required=" + RequiredWrittenCount.ToString(CultureInfo.InvariantCulture)
                + "/" + RequiredPartitions.Count.ToString(CultureInfo.InvariantCulture)
                + ", written=" + Written.Count.ToString(CultureInfo.InvariantCulture)
                + ", skipped=" + Skipped.Count.ToString(CultureInfo.InvariantCulture)
                + ", missing=" + Missing.Count.ToString(CultureInfo.InvariantCulture)
                + ", events=" + Events.Count.ToString(CultureInfo.InvariantCulture)
                + ", handshake=" + (HandshakeEstablished ? "established" : "not-established")
                + " — " + Reason;
        }
    }

    /// <summary>
    /// Accumulates the facts of one transfer and decides the outcome. This is the only place
    /// D-04's rules live, and it is hardware-free on purpose: the transfer code feeds it, the
    /// tests exercise it, and both read the same rule.
    /// </summary>
    public sealed class UsbUpdateVerdictBuilder
    {
        private readonly List<string> _required = new List<string>();
        private readonly List<string> _written = new List<string>();
        private readonly List<UsbUpdateSkip> _skipped = new List<UsbUpdateSkip>();
        private readonly List<string> _missing = new List<string>();
        private readonly List<UsbUpdateEvent> _events = new List<UsbUpdateEvent>();
        private readonly Action<string>? _log;
        private readonly Action<UsbUpdateEvent>? _onEvent;
        private readonly bool _requiredGiven;
        private bool _handshake;
        private string? _handshakeReason;
        private string? _fatal;
        private string? _stopReason;

        /// <param name="requiredPartitions">
        /// The set that must all be written for a `verified` verdict. `null`/`empty` means
        /// "the plan decides": every entry the plan declares (list.txt flag==1) is required.
        /// </param>
        /// <param name="log">Human log sink (the service's `_log`).</param>
        /// <param name="onEvent">Optional structured sink (the flasher turns these into op steps).</param>
        public UsbUpdateVerdictBuilder(IEnumerable<string>? requiredPartitions = null, Action<string>? log = null,
            Action<UsbUpdateEvent>? onEvent = null)
        {
            _log = log;
            _onEvent = onEvent;
            if (requiredPartitions != null)
            {
                foreach (string name in requiredPartitions)
                {
                    if (!string.IsNullOrWhiteSpace(name) && !Contains(_required, name))
                    {
                        _required.Add(name);
                    }
                }
            }
            _requiredGiven = _required.Count > 0;
        }

        /// <summary>True when the caller supplied an explicit required set.</summary>
        public bool HasExplicitRequiredSet => _requiredGiven;

        /// <summary>Declare a partition as required (no-op when the caller supplied the set).</summary>
        public void DeclareRequired(string partition)
        {
            if (_requiredGiven || string.IsNullOrWhiteSpace(partition)) return;
            if (!Contains(_required, partition)) _required.Add(partition);
        }

        /// <summary>The link answered the handshake: everything after this can be trusted.</summary>
        public void HandshakeEstablished()
        {
            _handshake = true;
            _handshakeReason = null;
        }

        /// <summary>The link never answered the handshake. Caps the verdict at `partial`.</summary>
        public void HandshakeMissing(string reason)
        {
            _handshake = false;
            _handshakeReason = reason;
            Add(new UsbUpdateEvent(UsbUpdateEventKind.HandshakeMissing, null, reason, true));
        }

        /// <summary>The package authorization command was sent but not acknowledged.</summary>
        public void AuthorizationMissing(string reason)
            => Add(new UsbUpdateEvent(UsbUpdateEventKind.AuthorizationMissing, null, reason, false));

        /// <summary>A partition's head/data/tail were all answered by the device.</summary>
        public void NoteWritten(string partition)
        {
            if (!Contains(_written, partition)) _written.Add(partition);
            Add(new UsbUpdateEvent(UsbUpdateEventKind.Written, partition, "transferred (head/data/tail answered)", true));
        }

        /// <summary>A precondition file was missing, so this partition was never attempted.</summary>
        public void NoteSkipped(string partition, string reason)
        {
            bool required = IsRequired(partition);
            _skipped.Add(new UsbUpdateSkip(partition, reason, required));
            Add(new UsbUpdateEvent(UsbUpdateEventKind.Skipped, partition, reason, required));
        }

        /// <summary>No complete payload reached the device for this partition.</summary>
        public void NoteMissing(string partition, string reason)
        {
            if (!Contains(_missing, partition)) _missing.Add(partition);
            Add(new UsbUpdateEvent(UsbUpdateEventKind.Missing, partition, reason, IsRequired(partition)));
        }

        /// <summary>
        /// Was: `if (bytesRead == 0) break;` followed by success. Now: the cut-off stream is a
        /// missing partition plus an explicit `degraded` event.
        /// </summary>
        public void NoteStreamEndedEarly(string partition, long sentBytes, long declaredBytes)
        {
            if (!Contains(_missing, partition)) _missing.Add(partition);
            Add(new UsbUpdateEvent(UsbUpdateEventKind.StreamEndedEarly, partition,
                "stream ended early: " + sentBytes.ToString(CultureInfo.InvariantCulture) + " of "
                + declaredBytes.ToString(CultureInfo.InvariantCulture) + " bytes read; the rest was never sent",
                IsRequired(partition)));
        }

        /// <summary>The transfer stopped here (device rejected a command); verdict can be at most `partial`.</summary>
        public void NoteTransferStopped(string partition, string reason)
        {
            if (!Contains(_missing, partition)) _missing.Add(partition);
            _stopReason = reason;
            Add(new UsbUpdateEvent(UsbUpdateEventKind.TransferStopped, partition, reason, IsRequired(partition)));
        }

        /// <summary>Any other declared degradation (unacknowledged reboot, malformed plan line, ...).</summary>
        public void NoteDegraded(string reason, string? partition = null, bool required = false)
            => Add(new UsbUpdateEvent(UsbUpdateEventKind.Degraded, partition, reason, required));

        /// <summary>The transfer cannot have produced anything (no list.txt, no plan).</summary>
        public void NoteFatal(string reason) => _fatal = reason;

        /// <summary>Fold every fact into one verdict (the rule table lives here, nowhere else).</summary>
        public UsbUpdateVerdict Build(string? reason = null)
        {
            int requiredWritten = _required.Count(IsWritten);
            bool anyWritten = _required.Count > 0 ? requiredWritten > 0 : _written.Count > 0;
            bool allRequiredWritten = _required.Count > 0 && requiredWritten == _required.Count;

            UsbUpdateOutcome outcome;
            string detail;

            if (_fatal != null)
            {
                outcome = UsbUpdateOutcome.Failed;
                detail = _fatal;
            }
            else if (!anyWritten)
            {
                outcome = UsbUpdateOutcome.Failed;
                detail = _stopReason != null
                    ? "no required partition was written (" + _stopReason + ")"
                    : "no required partition was written";
            }
            else if (allRequiredWritten && _handshake && _stopReason == null)
            {
                outcome = UsbUpdateOutcome.Verified;
                detail = "all " + _required.Count.ToString(CultureInfo.InvariantCulture) + " required partition(s) were answered by the device";
            }
            else
            {
                outcome = UsbUpdateOutcome.Partial;
                var reasons = new List<string>();
                if (_stopReason != null) reasons.Add("transfer stopped: " + _stopReason);
                if (!_handshake) reasons.Add("handshake not established" + (_handshakeReason == null ? "" : ": " + _handshakeReason));
                if (!allRequiredWritten)
                    reasons.Add(requiredWritten.ToString(CultureInfo.InvariantCulture) + "/"
                        + _required.Count.ToString(CultureInfo.InvariantCulture) + " required partition(s) written");
                if (_skipped.Count > 0) reasons.Add(_skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped");
                if (_missing.Count > 0) reasons.Add(_missing.Count.ToString(CultureInfo.InvariantCulture) + " missing");
                detail = string.Join("; ", reasons);
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                detail = reason + (string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")");
            }

            return new UsbUpdateVerdict(outcome, _required.ToArray(), _written.ToArray(), _skipped.ToArray(),
                _missing.ToArray(), _handshake, detail, _events.ToArray());
        }

        private bool IsRequired(string partition) => Contains(_required, partition);
        private bool IsWritten(string partition) => Contains(_written, partition);

        private static bool Contains(IEnumerable<string> names, string name)
            => names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

        private void Add(UsbUpdateEvent item)
        {
            _events.Add(item);
            _log?.Invoke(item.Line);
            _onEvent?.Invoke(item);
        }
    }
}
