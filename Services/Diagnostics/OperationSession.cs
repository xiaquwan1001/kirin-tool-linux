// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Kirin_Tool.Services.Approval;

namespace Kirin_Tool.Services.Diagnostics
{
    /// <summary>
    /// Ordered key/value pairs for one structured event. Values are written as JSON scalars
    /// (string/bool/int/long/double/byte→0xNN); a value that is itself a field list becomes a
    /// nested object, which is how VCOM frames keep their own <c>frameSeq</c> out of the row-level
    /// <c>seq</c>.
    /// </summary>
    public static class LogFields
    {
        public static IReadOnlyList<KeyValuePair<string, object?>> Of(params (string Key, object? Value)[] fields)
        {
            var list = new List<KeyValuePair<string, object?>>(fields?.Length ?? 0);
            if (fields == null) return list;
            foreach ((string key, object? value) in fields)
            {
                if (string.IsNullOrEmpty(key) || value == null) continue;
                list.Add(new KeyValuePair<string, object?>(key, value));
            }
            return list;
        }
    }

    /// <summary>
    /// One operation session on disk: a directory plus a single main JSONL. Every line of the
    /// session (the VCOM frames, the approval decisions, the fastboot commands and their results)
    /// carries the same <c>sessionId</c> and a monotonically increasing <c>seq</c>, so the timeline
    /// can be replayed with one <c>jq -s 'sort_by(.seq)'</c>.
    ///
    /// Layout (<c>$KIRIN_LOG_DIR</c> honoured):
    /// <code>
    /// sessions/&lt;YYYYmmdd-HHMMSS&gt;-&lt;pid&gt;-&lt;op&gt;/session.jsonl      main timeline (0600)
    /// sessions/&lt;YYYYmmdd-HHMMSS&gt;-&lt;pid&gt;-&lt;op&gt;/vcom-trace.txt     optional human transcript (0600)
    /// sessions/&lt;YYYYmmdd-HHMMSS&gt;-&lt;pid&gt;-&lt;op&gt;/summary.json        verdict written on every path (0600)
    /// </code>
    /// See docs/log-merge.md for the field table and the VCOM→phase mapping.
    /// </summary>
    public sealed class OperationSession : IDisposable
    {
        public const string SessionsDirectoryName = "sessions";
        public const string MainLogFileName = "session.jsonl";
        public const string TraceFileName = "vcom-trace.txt";
        public const string SummaryFileName = "summary.json";

        private readonly object _gate = new object();
        private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private readonly string _op;
        private readonly string _tool;
        private readonly IReadOnlyList<string> _argv;
        private readonly string? _rootOverride;

        private FileStream? _stream;
        private StreamWriter? _trace;
        private long _seq;
        private bool _ended;
        private bool _disposed;

        private long _lines;
        private int _scopes;
        private int _steps;
        private int _prompts;
        private int _frames;
        private int _acks;
        private int _ackBad;
        private int _childResults;
        private int _childFailed;
        private int? _lastExitCode;
        private string? _lastChildVerdict;
        private bool? _rootOk;
        private string? _rootVerdict;
        private string? _rootNote;

        private readonly EventHandler? _processExit;

        internal OperationSession(string op, string tool, IReadOnlyList<string> argv, string? notes, string? rootOverride = null)
        {
            _op = op;
            _tool = tool;
            _argv = argv ?? Array.Empty<string>();
            _rootOverride = string.IsNullOrWhiteSpace(rootOverride) ? null : rootOverride;
            SessionId = BuildSessionId(op, _startedUtc);

            if (!OperationLogger.Enabled)
            {
                Directory = string.Empty;
                return;
            }

            try
            {
                string logRoot = _rootOverride ?? OperationLogger.RootDirectory;
                string root = Path.Combine(logRoot, SessionsDirectoryName);
                System.IO.Directory.CreateDirectory(root);
                // The log root stays owner-only as well: it used to hold the VCOM transcript
                // directly, and the whole tree may contain device identifiers.
                OperationLogger.MakeOwnerOnlyDirectory(logRoot);
                OperationLogger.MakeOwnerOnlyDirectory(root);

                for (int attempt = 1; attempt <= 50; attempt++)
                {
                    string id = attempt == 1
                        ? SessionId
                        : SessionId + "-" + attempt.ToString(CultureInfo.InvariantCulture);
                    string directory = Path.Combine(root, id);
                    try
                    {
                        System.IO.Directory.CreateDirectory(directory);
                    }
                    catch (IOException)
                    {
                        continue; // name collision in the same second: try the next suffix
                    }

                    FileStream? stream = OperationLogger.TryOpenOwnerOnly(Path.Combine(directory, MainLogFileName));
                    if (stream == null) continue;

                    SessionId = id;
                    Directory = directory;
                    MainLogPath = Path.Combine(directory, MainLogFileName);
                    _stream = stream;
                    break;
                }

                OperationLogger.MakeOwnerOnlyDirectory(Directory);

                // One start line per session: this is where the session's own op/argv live.
                Event(
                    "start",
                    step: "session.open",
                    note: notes,
                    argv: _argv,
                    fields: LogFields.Of(("sessionOp", _op)));
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
                CloseQuietly();
            }

            if (_stream != null)
            {
                // A session that outlives its caller (crash, Ctrl-C, GUI close) must still leave a
                // summary behind, so the audit trail never ends in a half-written directory.
                _processExit = (_, _) => { try { End(null, "unknown:process-exit", "process exited with the session still open"); } catch { } };
                try { AppDomain.CurrentDomain.ProcessExit += _processExit; } catch { }
            }
        }

        internal static string BuildSessionId(string op, DateTime localNow)
        {
            string stamp = localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeOp = new string((op ?? "unknown")
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-')
                .ToArray()).Trim('-');
            if (safeOp.Length == 0) safeOp = "operation";
            if (safeOp.Length > 40) safeOp = safeOp.Substring(0, 40);
            return stamp + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + safeOp;
        }

        public string SessionId { get; private set; }

        /// <summary>Absolute session directory (empty when logging is off or failed).</summary>
        public string Directory { get; private set; } = string.Empty;

        /// <summary>Absolute path of <c>session.jsonl</c> (null when logging is unavailable).</summary>
        public string? MainLogPath { get; private set; }

        /// <summary>Absolute path of <c>vcom-trace.txt</c>, created on first transcript line.</summary>
        public string? TracePath { get; private set; }

        public string Op => _op;

        public string Tool => _tool;

        public DateTime StartedUtc => _startedUtc;

        public bool IsEnabled => _stream != null && !_disposed;

        public bool HasEnded => _ended;

        /// <summary>Number of lines written so far (used by the idle-rotation tests).</summary>
        public long LineCount { get { lock (_gate) return _lines; } }

        public void Touch() => OperationLogger.TouchSession(this);

        /// <summary>
        /// One structured line. <paramref name="phase"/> is start/step/prompt/result;
        /// <paramref name="step"/> names the structured event (<c>frame.write</c>, <c>approval.decision</c>…).
        /// </summary>
        public void Event(
            string phase,
            string? step = null,
            string? note = null,
            IEnumerable<KeyValuePair<string, object?>>? fields = null,
            string? parentId = null,
            string? scopeId = null,
            bool? ok = null,
            string? verdictSource = null,
            IReadOnlyList<string>? argv = null,
            string? answer = null,
            int? exitCode = null,
            long? durationMs = null,
            string? stdoutTail = null,
            string? stderrTail = null,
            string? capabilityId = null,
            string? approvalId = null,
            string? op = null,
            string? tool = null)
        {
            if (_stream == null || _disposed) return;

            if (phase == "step") Interlocked.Increment(ref _steps);
            if (phase == "prompt") Interlocked.Increment(ref _prompts);

            try
            {
                WriteLine(writer =>
                {
                    WritePhaseFields(writer, phase, step, note, fields, parentId, scopeId, ok, verdictSource,
                        argv, answer, exitCode, durationMs, stdoutTail, stderrTail, capabilityId, approvalId,
                        op ?? _op, tool ?? _tool);
                });
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
                CloseQuietly();
            }
        }

        private void WritePhaseFields(
            Utf8JsonWriter writer, string phase, string? step, string? note,
            IEnumerable<KeyValuePair<string, object?>>? fields, string? parentId, string? scopeId,
            bool? ok, string? verdictSource, IReadOnlyList<string>? argv, string? answer,
            int? exitCode, long? durationMs, string? stdoutTail, string? stderrTail,
            string? capabilityId, string? approvalId, string op, string tool)
        {
            writer.WriteString("phase", phase);
            writer.WriteString("op", op);
            writer.WriteString("tool", tool);
            if (!string.IsNullOrEmpty(step)) writer.WriteString("step", step);
            if (!string.IsNullOrEmpty(scopeId)) writer.WriteString("scopeId", scopeId);
            if (!string.IsNullOrEmpty(parentId)) writer.WriteString("parentId", parentId);

            string? capability = capabilityId ?? OperationLogger.CurrentCapabilityId;
            string? approval = approvalId ?? OperationLogger.CurrentApprovalId;
            if (!string.IsNullOrEmpty(capability)) writer.WriteString("capabilityId", capability);
            if (!string.IsNullOrEmpty(approval)) writer.WriteString("approvalId", approval);

            if (argv != null)
            {
                writer.WriteStartArray("argv");
                foreach (string arg in argv) writer.WriteStringValue(arg);
                writer.WriteEndArray();
            }

            if (!string.IsNullOrEmpty(note)) writer.WriteString("notes", note);
            if (!string.IsNullOrEmpty(answer)) writer.WriteString("answer", answer);

            if (fields != null)
            {
                foreach (KeyValuePair<string, object?> field in fields) WriteValue(writer, field.Key, field.Value);
            }

            if (exitCode.HasValue) writer.WriteNumber("exitCode", exitCode.Value);
            if (durationMs.HasValue) writer.WriteNumber("durationMs", durationMs.Value);
            string? outTail = OperationLogger.Tail(stdoutTail);
            string? errTail = OperationLogger.Tail(stderrTail);
            if (outTail != null) writer.WriteString("stdoutTail", outTail);
            if (errTail != null) writer.WriteString("stderrTail", errTail);
            if (ok.HasValue) writer.WriteBoolean("ok", ok.Value);
            if (!string.IsNullOrEmpty(verdictSource)) writer.WriteString("verdictSource", verdictSource);
        }

        private static void WriteValue(Utf8JsonWriter writer, string key, object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case bool flag:
                    writer.WriteBoolean(key, flag);
                    return;
                case byte b:
                    writer.WriteString(key, "0x" + b.ToString("X2", CultureInfo.InvariantCulture));
                    return;
                case int number:
                    writer.WriteNumber(key, number);
                    return;
                case long number:
                    writer.WriteNumber(key, number);
                    return;
                case double number:
                    writer.WriteNumber(key, number);
                    return;
                case IReadOnlyList<KeyValuePair<string, object?>> nested:
                    writer.WriteStartObject(key);
                    foreach (KeyValuePair<string, object?> field in nested) WriteValue(writer, field.Key, field.Value);
                    writer.WriteEndObject();
                    return;
                case IReadOnlyList<string> items:
                    writer.WriteStartArray(key);
                    foreach (string item in items) writer.WriteStringValue(item);
                    writer.WriteEndArray();
                    return;
                default:
                    writer.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                    return;
            }
        }

        /// <summary>Writes one line (ts/pid/sessionId/seq + payload) under the session lock.</summary>
        internal void WriteLine(Action<Utf8JsonWriter> writeFields)
        {
            lock (_gate)
            {
                if (_stream == null) return;

                using var buffer = new MemoryStream(512);
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("ts", OperationLogger.IsoTimestamp(OperationLogger.UtcNow()));
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteString("sessionId", SessionId);
                    writer.WriteNumber("seq", ++_seq);
                    writeFields(writer);
                    writer.WriteEndObject();
                }

                buffer.WriteByte((byte)'\n');
                _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                _stream.Flush();
                _lines++;
            }
            OperationLogger.TouchSession(this);
        }

        internal void CountScopeOpen()
        {
            Interlocked.Increment(ref _scopes);
        }

        /// <summary>A child scope's verdict, folded into the session verdict.</summary>
        internal void NoteChildResult(bool ok, string? verdictSource, int? exitCode)
        {
            Interlocked.Increment(ref _childResults);
            if (!ok) Interlocked.Increment(ref _childFailed);
            lock (_gate)
            {
                if (exitCode.HasValue) _lastExitCode = exitCode.Value;
                if (!string.IsNullOrEmpty(verdictSource)) _lastChildVerdict = verdictSource;
            }
        }

        internal void CountFrameWrite() => Interlocked.Increment(ref _frames);

        internal void CountAck(bool bad)
        {
            Interlocked.Increment(ref _acks);
            if (bad) Interlocked.Increment(ref _ackBad);
        }

        /// <summary>Appends one human-readable VCOM transcript line, creating <c>vcom-trace.txt</c> lazily.</summary>
        public void AppendTrace(string line)
        {
            if (_stream == null || _disposed) return;
            lock (_gate)
            {
                try
                {
                    if (_trace == null)
                    {
                        TracePath = Path.Combine(Directory, TraceFileName);
                        var stream = OperationLogger.TryOpenOwnerOnly(TracePath, append: true);
                        if (stream == null) return;
                        _trace = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    }
                    _trace.WriteLine(line);
                }
                catch (Exception ex)
                {
                    OperationLogger.ReportFailure(ex);
                }
            }
        }

        /// <summary>
        /// Ends the session: writes the terminal <c>result</c> line, then <c>summary.json</c>.
        /// Safe to call twice; the first call wins. <paramref name="ok"/> null means "derive from
        /// the child scopes" (any failed child ⇒ false).
        /// </summary>
        public void End(bool? ok = null, string? verdictSource = null, string? note = null)
        {
            bool first;
            lock (_gate)
            {
                first = !_ended;
                if (first)
                {
                    if (ok.HasValue) _rootOk = ok;
                    if (!string.IsNullOrEmpty(verdictSource)) _rootVerdict = verdictSource;
                    if (!string.IsNullOrEmpty(note)) _rootNote = note;
                    _ended = true;
                }
            }
            if (!first) return;

            bool? derived = DeriveOk();
            string verdict = _rootVerdict
                ?? (verdictSource ?? _lastChildVerdict ?? (derived.HasValue ? "children" : "unknown:no-result"));

            try
            {
                Event("result", note: _rootNote, ok: derived ?? false, verdictSource: verdict,
                    exitCode: _lastExitCode, durationMs: _stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
            }

            WriteSummary(derived ?? false, verdict);
            CloseQuietly();
            OperationLogger.ReleaseSession(this);
        }

        private bool? DeriveOk()
        {
            bool? ok = _rootOk;
            if (_childResults > 0)
            {
                if (_childFailed > 0) ok = false;
                else ok ??= true;
            }
            return ok;
        }

        private void WriteSummary(bool ok, string verdict)
        {
            if (string.IsNullOrEmpty(Directory) || MainLogPath == null) return;
            try
            {
                string path = Path.Combine(Directory, SummaryFileName);
                using var buffer = new MemoryStream(512);
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", SessionId);
                    writer.WriteString("op", _op);
                    writer.WriteString("tool", _tool);
                    writer.WriteString("startedUtc", OperationLogger.IsoTimestamp(_startedUtc));
                    writer.WriteString("endedUtc", OperationLogger.IsoTimestamp(OperationLogger.UtcNow()));
                    writer.WriteNumber("durationMs", _stopwatch.ElapsedMilliseconds);
                    writer.WriteBoolean("ok", ok);
                    writer.WriteString("verdictSource", verdict);
                    if (_lastExitCode.HasValue) writer.WriteNumber("exitCode", _lastExitCode.Value);

                    writer.WriteStartObject("paths");
                    writer.WriteString("directory", Directory);
                    writer.WriteString("mainLog", MainLogPath);
                    if (TracePath != null) writer.WriteString("trace", TracePath);
                    writer.WriteString("summary", path);
                    writer.WriteEndObject();

                    writer.WriteStartObject("counts");
                    writer.WriteNumber("lines", LineCount);
                    writer.WriteNumber("scopes", _scopes);
                    writer.WriteNumber("steps", _steps);
                    writer.WriteNumber("prompts", _prompts);
                    writer.WriteNumber("frames", _frames);
                    writer.WriteNumber("acks", _acks);
                    writer.WriteNumber("ackBad", _ackBad);
                    writer.WriteNumber("children", _childResults);
                    writer.WriteNumber("childrenFailed", _childFailed);
                    writer.WriteEndObject();

                    if (!string.IsNullOrEmpty(_rootNote)) writer.WriteString("note", _rootNote);
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writer.WriteEndObject();
                }
                buffer.WriteByte((byte)'\n');

                using (FileStream stream = OperationLogger.OpenOwnerOnlyForWrite(path))
                {
                    stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                    stream.Flush();
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The session directory was cleaned up under us (tests, user tidy-up): not an error.
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
            }
        }

        private void CloseQuietly()
        {
            lock (_gate)
            {
                try
                {
                    _trace?.Flush();
                    _trace?.Dispose();
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    _trace = null;
                }

                try
                {
                    _stream?.Flush();
                    _stream?.Dispose();
                }
                catch
                {
                    // best effort
                }
                finally
                {
                    _stream = null;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { if (_processExit != null) AppDomain.CurrentDomain.ProcessExit -= _processExit; } catch { }

            if (!_ended) End(null, "unknown:no-result", "session disposed without an explicit end");
            else CloseQuietly();
        }
    }

    /// <summary>
    /// Mirrors every approval decision into the current operation session, so the timeline shows
    /// who was asked, what the human answered and which capability/approval it belongs to.
    /// Installed as part of <see cref="ApprovalAudit.Sink"/> by default (approvals.jsonl is kept).
    /// </summary>
    public sealed class OperationSessionApprovalSink : IApprovalAuditSink
    {
        public void Record(ApprovalAuditEntry entry)
        {
            if (entry == null || !OperationLogger.Enabled) return;
            try
            {
                OperationSession session = OperationLogger.EnsureSession("approval.decision", "internal");
                if (!session.IsEnabled) return;

                session.Event(
                    "step",
                    step: "approval.decision",
                    note: entry.Decision + (string.IsNullOrEmpty(entry.Reason) ? string.Empty : ": " + entry.Reason),
                    fields: LogFields.Of(
                        ("approval", LogFields.Of(
                            ("capability", entry.CapabilityId),
                            ("title", entry.TitleZh),
                            ("origin", entry.Origin),
                            ("decision", entry.Decision),
                            ("scope", entry.Scope),
                            ("reused", entry.Reused),
                            ("redline", entry.TouchesRedline),
                            ("reversibility", entry.Reversibility),
                            ("typedConfirm", entry.TypedConfirm),
                            ("targets", entry.Targets),
                            ("fingerprint", entry.Fingerprint))),
                        ("ok", entry.Decision == "approved")),
                    capabilityId: entry.CapabilityId,
                    approvalId: entry.DecisionId);
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
            }
        }
    }

    /// <summary>Fans one approval decision out to several sinks without changing the gate.</summary>
    public sealed class TeeApprovalAuditSink : IApprovalAuditSink
    {
        private readonly IReadOnlyList<IApprovalAuditSink> _sinks;

        public TeeApprovalAuditSink(params IApprovalAuditSink[] sinks) => _sinks = sinks ?? Array.Empty<IApprovalAuditSink>();

        public void Record(ApprovalAuditEntry entry)
        {
            foreach (IApprovalAuditSink sink in _sinks)
            {
                try { sink.Record(entry); } catch { /* one broken sink must not break the others */ }
            }
        }
    }
}
