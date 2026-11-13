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
    /// Structured, machine-readable companion to the human diagnostics log.
    /// One JSONL file per device operation: `$KIRIN_LOG_DIR/&lt;category&gt;/&lt;utc-ts&gt;-&lt;pid&gt;.jsonl`.
    /// Every line is a phase of that operation (start / step / prompt / result) and the
    /// `result` line carries `ok` plus `verdictSource` so a failure can always be traced
    /// back to the exit code, the argument check or the exact output keyword that decided it.
    ///
    /// The logger never throws: a broken log directory must not break flashing. The first
    /// failure is reported once on stderr (see <see cref="ReportFailure"/>), then silence.
    /// See docs/op-logging.md for the field table and the migration order.
    /// </summary>
    public static class OperationLogger
    {
        /// <summary>Environment override for the log root (CI/containers).</summary>
        public const string DirectoryVariable = "KIRIN_LOG_DIR";

        /// <summary>Set to "1" to turn the structured log off entirely (tests/CI).</summary>
        public const string DisableVariable = "KIRIN_LOG_DISABLE";

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

        public static bool Enabled =>
            !string.Equals(Environment.GetEnvironmentVariable(DisableVariable), "1", StringComparison.Ordinal);

        private static readonly object SecretGate = new object();
        private static readonly HashSet<string> Secrets = new HashSet<string>(StringComparer.Ordinal);
        private static int _failureReported;

        /// <summary>Options whose following argument is a credential and must be masked.</summary>
        private static readonly HashSet<string> SecretOptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "passwd", "pass", "pwd", "token", "secret", "apikey", "api-key",
            "key", "auth", "credential", "credentials", "pin", "unlock-code", "unlockcode",
        };

        /// <summary>
        /// Register a literal that must never be written verbatim (e.g. the vendor unlock
        /// magic). Masked as `***(&lt;len&gt;)` so the field still shows it was present.
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
                        // Inline form (`--token=abc`): mask the value, keep the option name.
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

        /// <summary>
        /// Start a new operation scope. The returned scope owns one JSONL file and is safe to
        /// use from multiple threads; dispose it (or call <see cref="OperationScope.Result"/>)
        /// once per operation.
        /// </summary>
        public static OperationScope Begin(string op, string tool, IReadOnlyList<string>? argv = null, string? notes = null)
        {
            string safeOp = string.IsNullOrWhiteSpace(op) ? "unknown" : op!;
            string safeTool = string.IsNullOrWhiteSpace(tool) ? "internal" : tool!.ToLowerInvariant();
            return new OperationScope(safeOp, safeTool, Redact(argv), notes);
        }

        /// <summary>vcom/fastboot/flash keep their own directory; other tools get their own named directory.</summary>
        internal static string CategoryDirectory(string tool) => tool.ToLowerInvariant();

        /// <summary>UTC file-name stamp: sortable, no colons (Windows/FAT safe).</summary>
        internal static string Timestamp(DateTime utc) =>
            utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        internal static string IsoTimestamp(DateTime utc) =>
            utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        internal static DateTime UtcNow() => DateTime.UtcNow;

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
}

namespace Kirin_Tool.Services.Diagnostics
{
    /// <summary>
    /// One operation = one JSONL file. Thread-safe; <see cref="Dispose"/> writes a terminal
    /// `result` line if the caller did not (an operation must never end without a verdict).
    /// </summary>
    public sealed class OperationScope : IDisposable
    {
        private const int TailBytes = 2048;

        private readonly object _gate = new object();
        private readonly string _op;
        private readonly string _tool;
        private readonly IReadOnlyList<string> _argv;
        private readonly DateTime _startedUtc;
        private readonly System.Diagnostics.Stopwatch _stopwatch;

        private FileStream? _stream;
        private string? _filePath;
        private bool _resultWritten;
        private bool _disposed;

        internal OperationScope(string op, string tool, IReadOnlyList<string> argv, string? notes)
        {
            _op = op;
            _tool = tool;
            _argv = argv;
            _startedUtc = OperationLogger.UtcNow();
            _stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (!OperationLogger.Enabled) return;

            try
            {
                string directory = Path.Combine(OperationLogger.RootDirectory, OperationLogger.CategoryDirectory(tool));
                Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

                string stem = OperationLogger.Timestamp(_startedUtc) + "-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (int attempt = 1; attempt <= 50; attempt++)
                {
                    string candidate = Path.Combine(directory, attempt == 1 ? stem + ".jsonl" : stem + "-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jsonl");
                    FileStream? stream = TryOpen(candidate);
                    if (stream == null) continue;
                    _stream = stream;
                    _filePath = candidate;
                    break;
                }

                if (_stream == null) return;
                WriteLine(builder =>
                {
                    builder.WriteString("phase", "start");
                    builder.WriteString("op", _op);
                    builder.WriteString("tool", _tool);
                    WriteArgv(builder);
                    if (!string.IsNullOrEmpty(notes)) builder.WriteString("notes", notes);
                });
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
                CloseQuietly();
            }
        }

        private static FileStream? TryOpen(string path)
        {
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
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
                return null; // name collision in the same millisecond: try the next suffix
            }
        }

        public string Op => _op;

        public string Tool => _tool;

        /// <summary>Absolute path of this operation's JSONL file (null when logging is off/failed).</summary>
        public string? FilePath => _filePath;

        /// <summary>A step inside the operation (human-readable note, no verdict).</summary>
        public void Step(string note) => WritePhase("step", note, null);

        /// <summary>A question put to the user and the answer that was accepted.</summary>
        public void Prompt(string question, string? answer = null) =>
            WritePhase("prompt", question, answer == null ? null : "answer=" + answer);

        private void WritePhase(string phase, string note, string? extraNotes)
        {
            if (_stream == null) return;
            try
            {
                WriteLine(builder =>
                {
                    builder.WriteString("phase", phase);
                    builder.WriteString("op", _op);
                    builder.WriteString("tool", _tool);
                    if (!string.IsNullOrEmpty(note)) builder.WriteString("notes", note);
                    if (!string.IsNullOrEmpty(extraNotes)) builder.WriteString("answer", extraNotes);
                });
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
                CloseQuietly();
            }
        }

        /// <summary>
        /// Terminal line. `verdictSource` is mandatory in spirit: it names the evidence that
        /// decided `ok` (exit-code, arg-validation, timeout, output-contains:OKAY, …).
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
            if (_resultWritten || _stream == null)
            {
                _resultWritten = true;
                return;
            }

            try
            {
                long elapsed = durationMs ?? _stopwatch.ElapsedMilliseconds;
                WriteLine(builder =>
                {
                    builder.WriteString("phase", "result");
                    builder.WriteString("op", _op);
                    builder.WriteString("tool", _tool);
                    if (exitCode.HasValue) builder.WriteNumber("exitCode", exitCode.Value);
                    builder.WriteNumber("durationMs", elapsed);
                    string? outTail = OperationLogger.Tail(stdout, TailBytes);
                    string? errTail = OperationLogger.Tail(stderr, TailBytes);
                    if (outTail != null) builder.WriteString("stdoutTail", outTail);
                    if (errTail != null) builder.WriteString("stderrTail", errTail);
                    builder.WriteBoolean("ok", ok);
                    if (!string.IsNullOrEmpty(verdictSource)) builder.WriteString("verdictSource", verdictSource);
                    if (!string.IsNullOrEmpty(notes)) builder.WriteString("notes", notes);
                });
                _resultWritten = true;
            }
            catch (Exception ex)
            {
                OperationLogger.ReportFailure(ex);
            }
            finally
            {
                CloseQuietly();
            }
        }

        private void WriteLine(Action<Utf8JsonWriter> writeFields)
        {
            if (_stream == null) return;
            lock (_gate)
            {
                if (_stream == null) return;

                using var buffer = new MemoryStream(512);
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("ts", OperationLogger.IsoTimestamp(OperationLogger.UtcNow()));
                    writer.WriteNumber("pid", Environment.ProcessId);
                    writeFields(writer);
                    writer.WriteEndObject();
                }
                buffer.WriteByte((byte)'\n');
                _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                _stream.Flush();
            }
        }

        private void WriteArgv(Utf8JsonWriter writer)
        {
            writer.WriteStartArray("argv");
            foreach (string arg in _argv) writer.WriteStringValue(arg);
            writer.WriteEndArray();
        }

        private void CloseQuietly()
        {
            lock (_gate)
            {
                try
                {
                    _stream?.Flush();
                    _stream?.Dispose();
                }
                catch
                {
                    // Nothing to do: the log is best-effort by design.
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

            // A scope that ends without a verdict is itself a finding: record it as
            // not-ok so no operation quietly disappears from the audit trail.
            if (!_resultWritten && _stream != null)
            {
                Result(false, null, null, null, null, "unknown:no-result", "scope disposed without an explicit result");
                return;
            }
            CloseQuietly();
        }
    }
}
