// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Services
{
    /// <summary>
    /// Append-only audit of one VCOM session, shared by the GUI and the CLI/MCP path.
    ///
    /// Since v2.4.15 this class is an *adapter*: it no longer owns a second log file. Every line
    /// is written twice into ONE session (docs/log-merge.md):
    ///   * structured — session.jsonl, phase/step mapped from the event name (see MapEvent),
    ///     same sessionId and the same monotonic seq as the fastboot commands that follow it;
    ///   * human — vcom-trace.txt next to it, in the exact pre-2.4.15 text format, so an operator
    ///     can still read a frame-by-frame transcript with grep/less.
    /// The <c>Event/Progress/Error</c> signatures are unchanged, so VcomFlasher and
    /// FirmwareUnlocker needed no edit at all.
    /// </summary>
    public sealed class VcomSessionLogger : IDisposable
    {
        /// <summary>Overrides the log root; otherwise <c>~/.local/state/kirin-tool</c>.</summary>
        public const string DirectoryEnvironmentVariable = "KIRIN_LOG_DIR";

        /// <summary>Session op used for the VCOM leg of an operation.</summary>
        public const string SessionOp = "vcom.session";

        private readonly object _gate = new object();
        private readonly OperationSession? _session;
        private long _sequence;
        private bool _disposed;

        private VcomSessionLogger(string cpu, OperationSession? session)
        {
            Cpu = cpu;
            _session = session;
        }

        public string Cpu { get; }

        /// <summary>
        /// The human-readable transcript path (vcom-trace.txt). Kept under the old property name
        /// so existing callers (GUI dialog, CLI stderr line) keep working; null when logging is off.
        /// </summary>
        public string? FilePath => _session?.TracePath;

        /// <summary>Absolute path of the session's main JSONL (the machine timeline).</summary>
        public string? MainLogPath => _session?.MainLogPath;

        /// <summary>Absolute session directory shown by the GUI at the end of the run.</summary>
        public string? SessionDirectory => string.IsNullOrEmpty(_session?.Directory) ? null : _session!.Directory;

        public string? SessionId => _session?.SessionId;

        public bool IsEnabled => _session is { IsEnabled: true } && !_disposed;

        public static string DefaultDirectory
        {
            get
            {
                string? configured = Environment.GetEnvironmentVariable(DirectoryEnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(configured)) return configured!;

                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home)) home = Path.GetTempPath();
                return Path.Combine(home, ".local", "state", "kirin-tool");
            }
        }

        /// <summary>Legacy file name pattern, kept for callers/tests that still quote it.</summary>
        public static string BuildFileName(string cpu, DateTime utcNow)
        {
            string stamp = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string safeCpu = new string((cpu ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            return $"vcom-{stamp}-{safeCpu}.log";
        }

        /// <summary>
        /// Opens (or joins) the operation session and returns the VCOM adapter bound to it.
        /// Never throws: a read-only home degrades to "no log" and the caller keeps working.
        /// </summary>
        public static VcomSessionLogger Create(string cpu, string? directory = null)
        {
            try
            {
                OperationSession session = OperationLogger.BeginSession(SessionOp, "vcom",
                    new[] { "--cpu", cpu ?? "unknown" }, rootOverride: directory);
                var logger = new VcomSessionLogger(cpu ?? "unknown", session.IsEnabled ? session : null);
                logger.Event("session.open", ("tool", "Kirin-Tool"),
                    ("version", typeof(VcomSessionLogger).Assembly.GetName().Version?.ToString() ?? "unknown"));
                return logger;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VCOM log unavailable: {ex.Message}");
                return new VcomSessionLogger(cpu ?? "unknown", null);
            }
        }

        /// <summary>One event: one human transcript line + one structured step in session.jsonl.</summary>
        public void Event(string name, params (string Key, object? Value)[] fields)
        {
            if (!IsEnabled)
            {
                return;
            }

            long sequence = Interlocked.Increment(ref _sequence);
            string line = BuildTraceLine(name, sequence, fields);

            lock (_gate)
            {
                _session!.AppendTrace(line);
                WriteStructured(name, fields);
            }
        }

        /// <summary>Progress text from the service layer (event=progress).</summary>
        public void Progress(string message) => Event("progress", ("text", message));

        /// <summary>Exception with its full stack (event=error).</summary>
        public void Error(string where, Exception exception) =>
            Event("error", ("where", where), ("type", exception.GetType().Name), ("message", exception.Message),
                  ("stack", exception.ToString().Replace("\r\n", " | ").Replace("\n", " | ")));

        private string BuildTraceLine(string name, long sequence, (string Key, object? Value)[] fields)
        {
            var builder = new StringBuilder(160);
            builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            builder.Append(" pid=").Append(Environment.ProcessId);
            builder.Append(" seq=").Append(sequence);
            builder.Append(" event=").Append(name);
            foreach ((string key, object? value) in fields ?? Array.Empty<(string, object?)>())
            {
                if (value == null) continue;
                builder.Append(' ').Append(key).Append('=').Append(Format(value));
            }
            return builder.ToString();
        }

        /// <summary>
        /// The VCOM→phase mapping from docs/log-merge.md: frames and progress become phase=step
        /// with a structured payload, prompts stay phase=prompt, errors carry ok=false, and the
        /// session-level start line is the only phase=start the adapter writes.
        /// </summary>
        private void WriteStructured(string name, (string Key, object? Value)[] fields)
        {
            OperationSession session = _session!;
            var flat = new List<KeyValuePair<string, object?>>(fields?.Length ?? 0);
            List<KeyValuePair<string, object?>>? nested = null;
            string? nestedName = name switch
            {
                "frame.write" => "frame",
                "frame.ack" => "ack",
                _ when name.StartsWith("port.", StringComparison.Ordinal) => "port",
                _ when name.StartsWith("stage.", StringComparison.Ordinal) => "stage",
                _ => null,
            };
            string? note = null;
            bool? ok = null;
            string? verdictSource = null;

            foreach ((string key, object? value) in fields ?? Array.Empty<(string, object?)>())
            {
                if (value == null) continue;

                // Wire facts stay grouped: frame/ack/port/stage are one object each, which is
                // also what keeps the DATA frame sequence out of the row-level seq field.
                if (nestedName != null)
                {
                    string nestedKey = key switch
                    {
                        "seq" when nestedName == "frame" => "frameSeq",
                        "stage" => "name",
                        _ => key,
                    };
                    (nested ??= new()).Add(new(nestedKey, value));
                    continue;
                }

                switch (key)
                {
                    case "text" when name is "progress" or "prompt":
                        note = Convert.ToString(value, CultureInfo.InvariantCulture);
                        break;
                    case "where":
                        note = Convert.ToString(value, CultureInfo.InvariantCulture);
                        break;
                    // session.open carries the app identity; "tool"/"version" would collide with
                    // the row-level tool field and the JSON would carry a duplicate key.
                    case "tool" when name == "session.open":
                        flat.Add(new("app", value));
                        break;
                    case "version":
                        flat.Add(new("appVersion", value));
                        break;
                    default:
                        flat.Add(new(key, value));
                        break;
                }
            }

            if (nestedName != null && nested != null) flat.Add(new(nestedName, nested));

            if (name == "error")
            {
                ok = false;
                verdictSource = "exception:" + (flat.FirstOrDefault(f => f.Key == "type").Value as string ?? "unknown");
            }

            // Frame/ACK accounting feeds summary.json counts.
            if (name == "frame.write") session.CountFrameWrite();
            if (name == "frame.ack")
            {
                string? result = (fields ?? Array.Empty<(string, object?)>())
                    .FirstOrDefault(field => field.Key == "result").Value as string;
                session.CountAck(!string.Equals(result, "ok", StringComparison.Ordinal));
            }

            session.Event(
                PhaseOf(name),
                step: name,
                note: note ?? (name == "prompt.answer" ? null : null),
                fields: flat,
                ok: ok,
                verdictSource: verdictSource);
        }

        private static string PhaseOf(string name) => name switch
        {
            "session.open" => "start",
            "prompt" or "prompt.answer" => "prompt",
            _ => "step",
        };

        private static string Format(object value)
        {
            string text = value switch
            {
                bool flag => flag ? "true" : "false",
                byte b => "0x" + b.ToString("X2", CultureInfo.InvariantCulture),
                int number when number is >= 0 => number.ToString(CultureInfo.InvariantCulture),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
            text = text.Replace("\r", " ").Replace("\n", " ");
            bool needsQuotes = text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || c == '"');
            return needsQuotes ? "\"" + text.Replace("\"", "'") + "\"" : text;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_session == null) return;

            lock (_gate)
            {
                try
                {
                    string line = $"{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} pid={Environment.ProcessId} event=session.close";
                    _session.AppendTrace(line);
                    // The session itself stays open: the fastboot step that follows the cable
                    // replug belongs to the SAME timeline (that is the whole point of the merge).
                    _session.Event("step", step: "session.close", note: "vcom transcript closed");
                    _session.Touch();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VCOM log close failed: {ex.Message}");
                }
            }
        }
    }
}
