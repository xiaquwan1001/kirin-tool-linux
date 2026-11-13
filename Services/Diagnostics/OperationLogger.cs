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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Kirin_Tool.Services.Diagnostics
{
    /// <summary>
    /// The single writer for everything a device operation leaves on disk.
    ///
    /// Since v2.4.15 there is exactly one log per operation: a *session* directory
    /// (KIRIN_LOG_DIR/sessions/&lt;stamp&gt;-&lt;pid&gt;-&lt;op&gt;/) holding session.jsonl (machine timeline),
    /// an optional vcom-trace.txt (human VCOM transcript) and summary.json (the verdict).
    /// VCOM frames, approval decisions and fastboot commands all interleave in the one JSONL,
    /// sharing sessionId and a monotonically increasing seq. See docs/log-merge.md.
    ///
    /// Compatibility: the pre-2.4.15 layout (&lt;root&gt;/{vcom,fastboot,flash}/&lt;stamp&gt;-&lt;pid&gt;.jsonl
    /// and the flat vcom-*.log transcripts) is still *readable* by `logs show`, but nothing
    /// writes there any more.
    ///
    /// The logger never throws: a broken log directory must not break flashing. The first
    /// failure is reported once on stderr (see ReportFailure), then silence.
    /// </summary>
    public static class OperationLogger
    {
        /// <summary>Environment override for the log root (CI/containers).</summary>
        public const string DirectoryVariable = "KIRIN_LOG_DIR";

        /// <summary>Set to "1" to turn the structured log off entirely (tests/CI).</summary>
        public const string DisableVariable = "KIRIN_LOG_DISABLE";

        /// <summary>Seconds of inactivity after which the next operation starts a new session (0 = never).</summary>
        public const string SessionIdleVariable = "KIRIN_LOG_SESSION_IDLE_SECONDS";

        /// <summary>Default idle window: a GUI left open does not write one endless session.</summary>
        public const int DefaultSessionIdleSeconds = 1800;

        /// <summary>Default root, honouring XDG_STATE_HOME.</summary>
        public static string DefaultRootDirectory
        {
            get
            {
                string? state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrWhiteSpace(state))
                {
                    state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                }
                return Path.Combine(state, "kirin-tool");
            }
        }

        public static string RootDirectory
        {
            get
            {
                string? overridden = Environment.GetEnvironmentVariable(DirectoryVariable);
                return string.IsNullOrWhiteSpace(overridden) ? DefaultRootDirectory : overridden;
            }
        }

        /// <summary>Where new sessions live: &lt;root&gt;/sessions.</summary>
        public static string SessionsRoot => Path.Combine(RootDirectory, OperationSession.SessionsDirectoryName);

        public static bool Enabled =>
            !string.Equals(Environment.GetEnvironmentVariable(DisableVariable), "1", StringComparison.Ordinal);

        /// <summary>Idle window in seconds (0 or a negative value disables rotation).</summary>
        public static int SessionIdleSeconds
        {
            get
            {
                string? raw = Environment.GetEnvironmentVariable(SessionIdleVariable);
                if (string.IsNullOrWhiteSpace(raw)) return DefaultSessionIdleSeconds;
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
                    ? Math.Max(0, seconds)
                    : DefaultSessionIdleSeconds;
            }
        }

        private static readonly object SecretGate = new object();
        private static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
        private static int _failureReported;

        private static readonly object SessionGate = new object();
        private static OperationSession? _current;
        private static DateTime _lastActivityUtc = DateTime.UtcNow;
        private static string? _capabilityId;
        private static string? _approvalId;

        /// <summary>Options whose following argument is a credential and must be masked.</summary>
        private static readonly HashSet<string> SecretOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwd", "pass", "pwd", "token", "secret", "apikey", "api-key",
            "key", "auth", "credential", "credentials", "pin", "unlock-code", "unlockcode",
        };

        /// <summary>
        /// Register a literal that must never be written verbatim (e.g. the vendor unlock
        /// magic). Masked as ***(len) so the field still shows it was present.
        /// </summary>
        public static void RegisterSecret(string? secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 4) return;
            lock (SecretGate)
            {
                Secrets.Add(secret);
            }
        }

        /// <summary>Mask an argv array for the log: option credentials and registered secrets.</summary>
        public static IReadOnlyList<string> Redact(IReadOnlyList<string>? argv)
        {
            if (argv == null || argv.Count == 0) return Array.Empty<string>();

            var result = new string[argv.Count];
            bool maskNext = false;
            for (int i = 0; i < argv.Count; i++)
            {
                string arg = argv[i] ?? string.Empty;
                if (maskNext)
                {
                    result[i] = "***";
                    maskNext = false;
                    continue;
                }

                if (IsSecretOption(arg))
                {
                    int equals = arg.IndexOf('=');
                    if (equals >= 0)
                    {
                        // Inline form (--token=abc): mask the value, keep the option name.
                        result[i] = arg.Substring(0, equals + 1) + "***";
                        continue;
                    }

                    result[i] = arg;
                    maskNext = true;
                    continue;
                }

                if (IsRegisteredSecret(arg))
                {
                    result[i] = "***(" + arg.Length.ToString(CultureInfo.InvariantCulture) + ")";
                    continue;
                }

                result[i] = arg;
            }
            return result;
        }

        private static bool IsSecretOption(string arg)
        {
            string trimmed = arg.TrimStart('-');
            int equals = trimmed.IndexOf('=');
            string name = equals >= 0 ? trimmed.Substring(0, equals) : trimmed;
            return arg.StartsWith("-", StringComparison.Ordinal) && SecretOptions.Contains(name);
        }

        private static bool IsRegisteredSecret(string arg)
        {
            lock (SecretGate)
            {
                return Secrets.Contains(arg);
            }
        }

        /// <summary>
        /// Split a verbatim command string into argv tokens for logging only. Quotes are
        /// honoured so `oem dump-emmc oeminfo "/tmp/a b.bin"` stays three tokens; the string
        /// actually handed to the child process is never re-serialised from this.
        /// </summary>
        public static IReadOnlyList<string> ParseArgv(string? commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();

            var tokens = new List<string>();
            var current = new StringBuilder();
            char quote = '\0';
            bool started = false;

            foreach (char c in commandLine)
            {
                if (quote != '\0')
                {
                    if (c == quote) { quote = '\0'; }
                    else { current.Append(c); }
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    quote = c;
                    started = true;
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    if (started || current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        started = false;
                    }
                    continue;
                }

                current.Append(c);
                started = true;
            }

            if (started || current.Length > 0) tokens.Add(current.ToString());
            return tokens;
        }

        // ------------------------------------------------------------------ session lifecycle

        /// <summary>The operation session the rest of this process writes into (null = none yet).</summary>
        public static OperationSession? CurrentSession
        {
            get
            {
                lock (SessionGate)
                {
                    return _current is { IsEnabled: true } session ? session : null;
                }
            }
        }

        /// <summary>The capability id every line currently carries (set by CapabilityScope).</summary>
        public static string? CurrentCapabilityId
        {
            get { lock (SessionGate) return _capabilityId; }
        }

        /// <summary>The approval fingerprint every line currently carries (if any).</summary>
        public static string? CurrentApprovalId
        {
            get { lock (SessionGate) return _approvalId; }
        }

        /// <summary>
        /// Begins (or joins) the operation session: the directory and session.jsonl are created on
        /// first use, and every later scope in this process writes into the same file with the same
        /// sessionId. An idle window (SessionIdleVariable) rotates the session so a long-lived GUI
        /// does not accumulate one endless directory.
        /// </summary>
        public static OperationSession BeginSession(string op, string tool, IReadOnlyList<string>? argv = null, string? notes = null, string? rootOverride = null) =>
            EnsureSession(op, tool, argv, notes, rootOverride);

        /// <summary>Same as BeginSession; kept for call sites that read better as "ensure".</summary>
        public static OperationSession EnsureSession(string op, string tool, IReadOnlyList<string>? argv = null, string? notes = null, string? rootOverride = null)
        {
            string safeOp = string.IsNullOrWhiteSpace(op) ? "unknown" : op!;
            string safeTool = string.IsNullOrWhiteSpace(tool) ? "internal" : tool!.ToLowerInvariant();

            if (!Enabled) return new OperationSession(safeOp, safeTool, Array.Empty<string>(), notes, rootOverride);
            if (!string.IsNullOrWhiteSpace(rootOverride)) return new OperationSession(safeOp, safeTool, Redact(argv), notes, rootOverride);

            OperationSession? expired;
            lock (SessionGate)
            {
                int idle = SessionIdleSeconds;
                expired = _current != null && idle > 0 && (DateTime.UtcNow - _lastActivityUtc).TotalSeconds > idle
                    ? _current
                    : null;
                if (expired != null) _current = null;
                else if (_current is { IsEnabled: true }) return _current;
                else if (_current != null) { expired = _current; _current = null; }
            }

            expired?.End(null, "session:idle-rotate", "rotated after " + SessionIdleSeconds + "s without activity");

            var created = new OperationSession(safeOp, safeTool, Redact(argv), notes, null);
            lock (SessionGate)
            {
                _current = created.IsEnabled ? created : null;
                _lastActivityUtc = DateTime.UtcNow;
            }
            return created;
        }

        /// <summary>Ends the current session (writes its result line and summary.json).</summary>
        public static void EndSession(bool? ok = null, string? verdictSource = null, string? note = null)
        {
            OperationSession? session;
            lock (SessionGate)
            {
                session = _current;
                _current = null;
            }
            session?.End(ok, verdictSource, note);
        }

        /// <summary>Starts a new scope inside the current (or a freshly created) session.</summary>
        public static OperationScope Begin(string op, string tool, IReadOnlyList<string>? argv = null, string? notes = null, string? parentId = null)
        {
            OperationSession session = EnsureSession(op, tool, argv, notes);
            return new OperationScope(session, op, tool, Redact(argv), notes, parentId);
        }

        /// <summary>
        /// Attaches capabilityId/approvalId to everything logged until disposed, so frames and
        /// child commands can be joined back to the registry row and the approval decision that
        /// allowed them.
        /// </summary>
        public static IDisposable CapabilityScope(string? capabilityId, string? approvalId = null)
        {
            lock (SessionGate)
            {
                string? previousCapability = _capabilityId;
                string? previousApproval = _approvalId;
                _capabilityId = string.IsNullOrWhiteSpace(capabilityId) ? previousCapability : capabilityId;
                _approvalId = string.IsNullOrWhiteSpace(approvalId) ? previousApproval : approvalId;
                return new CapabilityRestore(previousCapability, previousApproval);
            }
        }

        private sealed class CapabilityRestore : IDisposable
        {
            private readonly string? _capability;
            private readonly string? _approval;
            private bool _disposed;

            public CapabilityRestore(string? capability, string? approval)
            {
                _capability = capability;
                _approval = approval;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (SessionGate)
                {
                    _capabilityId = _capability;
                    _approvalId = _approval;
                }
            }
        }

        internal static void TouchSession(OperationSession session)
        {
            lock (SessionGate)
            {
                if (ReferenceEquals(_current, session)) _lastActivityUtc = DateTime.UtcNow;
            }
        }

        internal static void ReleaseSession(OperationSession session)
        {
            lock (SessionGate)
            {
                if (ReferenceEquals(_current, session)) _current = null;
            }
        }

        /// <summary>For tests: forget the ambient session without ending it.</summary>
        public static void ResetSessionForTests()
        {
            lock (SessionGate)
            {
                _current = null;
                _capabilityId = null;
                _approvalId = null;
            }
        }

        // ------------------------------------------------------------------ io helpers

        /// <summary>Legacy category directory name (kept so old readers keep working).</summary>
        internal static string CategoryDirectory(string tool) => tool.ToLowerInvariant();

        /// <summary>UTC file-name stamp: sortable, no colons (Windows/FAT safe).</summary>
        internal static string Timestamp(DateTime utc) =>
            utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        internal static string IsoTimestamp(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        internal static DateTime UtcNow() => DateTime.UtcNow;

        internal static void MakeOwnerOnlyDirectory(string directory)
        {
            if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory)) return;
            try
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch
            {
                // Best effort: an existing directory owned by someone else stays as it is.
            }
        }

        /// <summary>Creates a brand-new 0600 file (or appends), or null when it cannot.</summary>
        internal static FileStream? TryOpenOwnerOnly(string path, bool append = false)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = append ? FileMode.Append : FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.Read,
                    Options = FileOptions.None,
                };
                var stream = new FileStream(path, options);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                return stream;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Truncates/creates a 0600 file for the summary write.</summary>
        internal static FileStream OpenOwnerOnlyForWrite(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return stream;
        }

        /// <summary>UTF-8-safe tail truncation (byte budget, never splits a code point).</summary>
        internal static string? Tail(string? text, int maxBytes = 2048)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (Encoding.UTF8.GetByteCount(text!) <= maxBytes) return text;

            int bytes = 0;
            int index = text!.Length;
            while (index > 0)
            {
                int width = char.IsLowSurrogate(text[index - 1]) && index >= 2 ? 2 : 1;
                int cost = Encoding.UTF8.GetByteCount(text.Substring(index - width, width));
                if (bytes + cost > maxBytes) break;
                bytes += cost;
                index -= width;
            }
            return text.Substring(index);
        }

        internal static void ReportFailure(Exception ex)
        {
            if (Interlocked.Exchange(ref _failureReported, 1) == 1) return;
            try
            {
                Console.Error.WriteLine("[operation-log] disabled after error: " + ex.GetType().Name + ": " + ex.Message);
            }
            catch
            {
                // Nothing sensible left to do; keep the main flow alive.
            }
        }
    }

    /// <summary>
    /// One operation scope inside a session. Several scopes share one session.jsonl (that is the
    /// point of the merge): each gets its own scopeId, and lines are ordered by the session-wide
    /// seq. Thread-safe; Dispose writes a terminal result line if the caller did not (an
    /// operation must never end without a verdict).
    /// </summary>
    public sealed class OperationScope : IDisposable
    {
        private const int TailBytes = 2048;

        private readonly OperationSession _session;
        private readonly string _op;
        private readonly string _tool;
        private readonly IReadOnlyList<string> _argv;
        private readonly string _scopeId;
        private readonly string? _parentId;
        private readonly System.Diagnostics.Stopwatch _stopwatch;
        private readonly string? _capabilityId;
        private readonly string? _approvalId;

        private bool _resultWritten;
        private bool _disposed;

        internal OperationScope(OperationSession session, string op, string tool, IReadOnlyList<string> argv, string? notes, string? parentId)
        {
            _session = session;
            _op = string.IsNullOrWhiteSpace(op) ? "unknown" : op;
            _tool = string.IsNullOrWhiteSpace(tool) ? "internal" : tool.ToLowerInvariant();
            _argv = argv ?? Array.Empty<string>();
            _scopeId = "scope-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            _parentId = string.IsNullOrEmpty(parentId) ? session.SessionId : parentId;
            _capabilityId = OperationLogger.CurrentCapabilityId;
            _approvalId = OperationLogger.CurrentApprovalId;
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();

            session.CountScopeOpen();
            session.Event(
                "start",
                step: null,
                note: notes,
                argv: _argv,
                scopeId: _scopeId,
                parentId: _parentId,
                capabilityId: _capabilityId,
                approvalId: _approvalId,
                op: _op,
                tool: _tool);
        }

        public string Op => _op;

        public string Tool => _tool;

        /// <summary>This scope's id (referenced by parentId of its children).</summary>
        public string Id => _scopeId;

        public string SessionId => _session.SessionId;

        /// <summary>Absolute path of the session's main JSONL (null when logging is off/failed).</summary>
        public string? FilePath => _session.MainLogPath;

        /// <summary>Absolute session directory (sessions/&lt;sessionId&gt;/), null when logging is off.</summary>
        public string? SessionDirectory => string.IsNullOrEmpty(_session.Directory) ? null : _session.Directory;

        /// <summary>A step inside the operation (human-readable note, no verdict).</summary>
        public void Step(string note) => Step(note, (string?)null);

        /// <summary>A structured step: step names the event (frame.write, exec, ...).</summary>
        public void Step(string? note, string? step, IEnumerable<KeyValuePair<string, object?>>? fields = null, bool? ok = null, string? verdictSource = null, string? parentId = null)
        {
            _session.Event(
                "step",
                step: step,
                note: note,
                fields: fields,
                parentId: parentId ?? _parentId,
                scopeId: _scopeId,
                ok: ok,
                verdictSource: verdictSource,
                capabilityId: _capabilityId,
                approvalId: _approvalId,
                op: _op,
                tool: _tool);
        }

        /// <summary>A question put to the user and the answer that was accepted.</summary>
        public void Prompt(string question, string? answer = null) => Prompt(question, answer, null, null);

        public void Prompt(string question, string? answer, IEnumerable<KeyValuePair<string, object?>>? fields, string? parentId = null)
        {
            _session.Event(
                "prompt",
                step: "prompt",
                note: question,
                fields: fields,
                parentId: parentId ?? _parentId,
                scopeId: _scopeId,
                answer: answer == null ? null : "answer=" + answer,
                capabilityId: _capabilityId,
                approvalId: _approvalId,
                op: _op,
                tool: _tool);
        }

        /// <summary>Free-form event (used by adapters that already own a phase/step name).</summary>
        public void Event(string phase, string? step, string? note = null, IEnumerable<KeyValuePair<string, object?>>? fields = null, bool? ok = null, string? verdictSource = null, string? parentId = null)
        {
            _session.Event(
                phase,
                step: step,
                note: note,
                fields: fields,
                parentId: parentId ?? _parentId,
                scopeId: _scopeId,
                ok: ok,
                verdictSource: verdictSource,
                capabilityId: _capabilityId,
                approvalId: _approvalId,
                op: _op,
                tool: _tool);
        }

        /// <summary>
        /// Terminal line. verdictSource is mandatory in spirit: it names the evidence that decided
        /// ok (exit-code, arg-validation, timeout, output-contains:OKAY, ...).
        /// </summary>
        public void Result(
            bool ok,
            int? exitCode = null,
            long? durationMs = null,
            string? stdout = null,
            string? stderr = null,
            string? verdictSource = null,
            string? notes = null)
        {
            if (_resultWritten)
                return;

            _resultWritten = true;
            _session.NoteChildResult(ok, verdictSource, exitCode);
            _session.Event(
                "result",
                step: null,
                note: notes,
                parentId: _parentId,
                scopeId: _scopeId,
                ok: ok,
                verdictSource: verdictSource,
                exitCode: exitCode,
                durationMs: durationMs ?? _stopwatch.ElapsedMilliseconds,
                stdoutTail: OperationLogger.Tail(stdout, TailBytes),
                stderrTail: OperationLogger.Tail(stderr, TailBytes),
                capabilityId: _capabilityId,
                approvalId: _approvalId,
                op: _op,
                tool: _tool);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // A scope that ends without a verdict is itself a finding: record it as
            // not-ok so no operation quietly disappears from the audit trail.
            if (!_resultWritten)
            {
                Result(false, null, null, null, null, "unknown:no-result", "scope disposed without an explicit result");
            }
        }
    }
}
