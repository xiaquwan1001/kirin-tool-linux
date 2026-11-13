// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;

namespace Kirin_Tool.Services
{
    /// <summary>
    /// Append-only audit log of one VCOM session, shared by the GUI and the CLI/MCP path.
    /// Why it exists: a GUI unlock used to leave behind nothing but a dialog, so after a failed
    /// attempt nobody could tell which process held the tty or which frame was repeated.
    /// One line per event, UTC timestamp with milliseconds, flushed immediately.
    /// Format and field table: docs/cli-vcom-unlock-parity.md §"日志格式与字段表".
    /// </summary>
    public sealed class VcomSessionLogger : IDisposable
    {
        /// <summary>Overrides the log directory; otherwise <c>~/.local/state/kirin-tool</c>.</summary>
        public const string DirectoryEnvironmentVariable = "KIRIN_LOG_DIR";

        private readonly object _gate = new object();
        private readonly StreamWriter? _writer;
        private long _sequence;
        private bool _disposed;

        private VcomSessionLogger(string cpu, string? filePath, StreamWriter? writer)
        {
            Cpu = cpu;
            FilePath = filePath;
            _writer = writer;
        }

        public string Cpu { get; }

        /// <summary>Absolute log path, or null when logging is unavailable (never fatal).</summary>
        public string? FilePath { get; }

        public bool IsEnabled => _writer != null && !_disposed;

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

        /// <summary>File name pattern: <c>vcom-&lt;UTC yyyyMMddTHHmmssZ&gt;-&lt;cpu&gt;.log</c>.</summary>
        public static string BuildFileName(string cpu, DateTime utcNow)
        {
            string stamp = utcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string safeCpu = new string((cpu ?? "unknown").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
            return $"vcom-{stamp}-{safeCpu}.log";
        }

        /// <summary>
        /// Creates the session log. Never throws: a read-only home or a missing state directory
        /// degrades to "no log" and the caller keeps working.
        /// </summary>
        public static VcomSessionLogger Create(string cpu, string? directory = null)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
                Directory.CreateDirectory(target);
                TrySetOwnerOnly(target);
                string path = Path.Combine(target, BuildFileName(cpu, DateTime.UtcNow));
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                var logger = new VcomSessionLogger(cpu, path, writer);
                logger.Event("session.open", ("tool", "Kirin-Tool"), ("version", typeof(VcomSessionLogger).Assembly.GetName().Version?.ToString() ?? "unknown"));
                return logger;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"VCOM log unavailable: {ex.Message}");
                return new VcomSessionLogger(cpu, null, null);
            }
        }

        private static void TrySetOwnerOnly(string directory)
        {
            if (OperatingSystem.IsWindows()) return;
            try
            {
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception)
            {
                // Best effort: an existing directory owned by someone else stays as it is.
            }
        }

        /// <summary>One structured line: <c>&lt;utc&gt; pid= event=&lt;name&gt; k=v ...</c>.</summary>
        public void Event(string name, params (string Key, object? Value)[] fields)
        {
            if (!IsEnabled) return;
            var builder = new StringBuilder(160);
            builder.Append(DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            builder.Append(" pid=").Append(Environment.ProcessId);
            builder.Append(" seq=").Append(Interlocked.Increment(ref _sequence));
            builder.Append(" event=").Append(name);
            foreach ((string key, object? value) in fields)
            {
                if (value == null) continue;
                builder.Append(' ').Append(key).Append('=').Append(Format(value));
            }

            lock (_gate)
            {
                try { _writer!.WriteLine(builder.ToString()); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"VCOM log write failed: {ex.Message}"); }
            }
        }

        /// <summary>Progress text from the service layer (event=progress).</summary>
        public void Progress(string message) => Event("progress", ("text", message));

        /// <summary>Exception with its full stack (event=error).</summary>
        public void Error(string where, Exception exception) =>
            Event("error", ("where", where), ("type", exception.GetType().Name), ("message", exception.Message),
                  ("stack", exception.ToString().Replace("\r\n", " | ").Replace("\n", " | ")));

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
            if (_writer == null) return;
            lock (_gate)
            {
                try
                {
                    _writer.WriteLine($"{DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} pid={Environment.ProcessId} event=session.close");
                    _writer.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"VCOM log close failed: {ex.Message}");
                }
            }
        }
    }
}
