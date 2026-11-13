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
using System.Text.Json.Nodes;

namespace Kirin_Tool.Services.Diagnostics
{
    /// <summary>One log session as seen by the read-only <c>logs</c> face.</summary>
    public sealed record LogSessionDescriptor
    {
        public required string SessionId { get; init; }
        /// <summary>session | legacy-jsonl | legacy-vcom-text</summary>
        public required string Kind { get; init; }
        /// <summary>Session directory (sessions) or the log file itself (legacy).</summary>
        public required string Directory { get; init; }
        public string? MainLog { get; init; }
        public string? Trace { get; init; }
        public string? Summary { get; init; }
        public string? Op { get; init; }
        public string? Tool { get; init; }
        public DateTime? StartedUtc { get; init; }
        public int? Pid { get; init; }
        public bool Legacy { get; init; }
        public long Bytes { get; init; }
        public int? EventCount { get; init; }
        public bool? Ok { get; init; }
        public string? VerdictSource { get; init; }
    }

    /// <summary>
    /// Read side of the merged logs: <c>sessions/</c> first, the pre-2.4.15 layout afterwards.
    /// Nothing here writes or deletes, which is why <c>logs</c> needs no approval and no confirm.
    /// Legacy files are reported with <c>legacy: true</c> and synthesised <c>sessionId</c>s so a
    /// single consumer can read both generations.
    /// </summary>
    public static class LogSessionStore
    {
        public const string Latest = "latest";
        private const string LegacyIdPrefix = "legacy:";

        public static string Root => OperationLogger.RootDirectory;

        /// <summary>Every discoverable session, newest first.</summary>
        public static IReadOnlyList<LogSessionDescriptor> List(string? root = null)
        {
            string target = string.IsNullOrWhiteSpace(root) ? Root : root!;
            var sessions = new List<LogSessionDescriptor>();

            string sessionsRoot = Path.Combine(target, OperationSession.SessionsDirectoryName);
            if (Directory.Exists(sessionsRoot))
            {
                foreach (string directory in SafeEnumerate(sessionsRoot, SearchOption.TopDirectoryOnly))
                {
                    if (!Directory.Exists(directory)) continue;
                    sessions.Add(DescribeSessionDirectory(directory));
                }
            }

            // Legacy one-op-per-file JSONL (<root>/<category>/<stamp>-<pid>.jsonl).
            foreach (string directory in SafeEnumerate(target, SearchOption.TopDirectoryOnly))
            {
                if (!Directory.Exists(directory)) continue;
                if (string.Equals(Path.GetFileName(directory), OperationSession.SessionsDirectoryName, StringComparison.Ordinal)) continue;
                foreach (string file in SafeEnumerate(directory, SearchOption.TopDirectoryOnly))
                {
                    if (!file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
                    sessions.Add(DescribeLegacyJsonl(file, Path.GetFileName(directory)));
                }
            }

            // Legacy flat VCOM text transcript (<root>/vcom-<UTC>-<cpu>.log).
            foreach (string file in SafeEnumerate(target, SearchOption.TopDirectoryOnly))
            {
                if (!file.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetFileName(file).StartsWith("vcom-", StringComparison.OrdinalIgnoreCase)) continue;
                sessions.Add(DescribeLegacyVcomText(file));
            }

            return sessions
                .OrderByDescending(descriptor => descriptor.StartedUtc ?? DateTime.MinValue)
                .ThenByDescending(descriptor => descriptor.SessionId, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Resolves <c>latest</c>, an exact/prefix session id, or a path (session dir, main log,
        /// trace, legacy file). Returns null when nothing matches unambiguously.
        /// </summary>
        public static LogSessionDescriptor? Resolve(string? selector, string? root = null)
        {
            IReadOnlyList<LogSessionDescriptor> all = List(root);
            string token = (selector ?? Latest).Trim();

            if (token.Length == 0 || string.Equals(token, Latest, StringComparison.OrdinalIgnoreCase))
                return all.FirstOrDefault();

            LogSessionDescriptor? exact = all.FirstOrDefault(descriptor =>
                string.Equals(descriptor.SessionId, token, StringComparison.Ordinal) ||
                string.Equals(NormalizeLegacyId(descriptor.SessionId), token, StringComparison.Ordinal));
            if (exact != null) return exact;

            // A path on disk: session directory, session.jsonl, trace, summary or a legacy file.
            string full;
            try { full = Path.GetFullPath(token); }
            catch (Exception) { full = token; }

            LogSessionDescriptor? byPath = all.FirstOrDefault(descriptor =>
                PathsOf(descriptor).Any(path => string.Equals(Path.GetFullPath(path), full, StringComparison.Ordinal)) ||
                string.Equals(Path.GetFullPath(descriptor.Directory), full, StringComparison.Ordinal));
            if (byPath != null) return byPath;

            // Unique prefix of a session id (git-style shorthand).
            var prefixed = all.Where(descriptor => descriptor.SessionId.StartsWith(token, StringComparison.Ordinal)).ToList();
            return prefixed.Count == 1 ? prefixed[0] : null;
        }

        /// <summary>Human/agent-readable dump of one session (the <c>logs show</c> payload).</summary>
        public static string Show(LogSessionDescriptor descriptor, bool includeTrace, bool indented = true)
        {
            using var buffer = new MemoryStream(4096);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, SkipValidation = true }))
            {
                writer.WriteStartObject();

                writer.WriteStartObject("session");
                writer.WriteString("sessionId", descriptor.SessionId);
                writer.WriteString("kind", descriptor.Kind);
                writer.WriteString("directory", descriptor.Directory);
                if (descriptor.MainLog != null) writer.WriteString("mainLog", descriptor.MainLog);
                if (descriptor.Trace != null) writer.WriteString("trace", descriptor.Trace);
                if (descriptor.Summary != null) writer.WriteString("summary", descriptor.Summary);
                if (descriptor.Op != null) writer.WriteString("op", descriptor.Op);
                if (descriptor.Tool != null) writer.WriteString("tool", descriptor.Tool);
                if (descriptor.StartedUtc.HasValue) writer.WriteString("startedUtc", OperationLogger.IsoTimestamp(descriptor.StartedUtc.Value));
                if (descriptor.Pid.HasValue) writer.WriteNumber("pid", descriptor.Pid.Value);
                writer.WriteBoolean("legacy", descriptor.Legacy);
                writer.WriteNumber("bytes", descriptor.Bytes);
                if (descriptor.EventCount.HasValue) writer.WriteNumber("eventCount", descriptor.EventCount.Value);
                if (descriptor.Ok.HasValue) writer.WriteBoolean("ok", descriptor.Ok.Value);
                if (descriptor.VerdictSource != null) writer.WriteString("verdictSource", descriptor.VerdictSource);
                writer.WriteEndObject();

                writer.WritePropertyName("summary");
                if (descriptor.Summary != null && File.Exists(descriptor.Summary))
                {
                    JsonNode? summary = TryParse(File.ReadAllText(descriptor.Summary));
                    if (summary != null) summary.WriteTo(writer, new JsonSerializerOptions { WriteIndented = indented });
                    else writer.WriteNullValue();
                }
                else
                {
                    writer.WriteNullValue();
                }

                writer.WriteStartArray("events");
                int events = 0;
                foreach (JsonNode? node in ReadEvents(descriptor))
                {
                    node?.WriteTo(writer, new JsonSerializerOptions { WriteIndented = indented });
                    events++;
                }
                writer.WriteEndArray();

                if (includeTrace && descriptor.Trace != null && File.Exists(descriptor.Trace))
                {
                    writer.WriteStartArray("trace");
                    foreach (string line in File.ReadLines(descriptor.Trace)) writer.WriteStringValue(line);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
                _ = events;
            }
            buffer.WriteByte((byte)'\n');
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>The <c>logs path</c> payload: exactly the paths a script needs.</summary>
        public static string Paths(LogSessionDescriptor descriptor, bool indented = true)
        {
            using var buffer = new MemoryStream(512);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, SkipValidation = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("sessionId", descriptor.SessionId);
                writer.WriteString("kind", descriptor.Kind);
                writer.WriteBoolean("legacy", descriptor.Legacy);
                writer.WriteString("dir", descriptor.Kind == "session" ? descriptor.Directory : Path.GetDirectoryName(descriptor.Directory) ?? descriptor.Directory);
                writer.WriteString("mainLog", descriptor.MainLog);
                if (descriptor.Trace != null) writer.WriteString("trace", descriptor.Trace);
                else writer.WriteNull("trace");
                if (descriptor.Summary != null) writer.WriteString("summary", descriptor.Summary);
                else writer.WriteNull("summary");
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>The <c>logs list</c> payload.</summary>
        public static string ListJson(IReadOnlyList<LogSessionDescriptor> descriptors, string? root = null, bool indented = true)
        {
            using var buffer = new MemoryStream(1024);
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, SkipValidation = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("root", string.IsNullOrWhiteSpace(root) ? Root : root!);
                writer.WriteNumber("count", descriptors.Count);
                writer.WriteNumber("sessions", descriptors.Count(descriptor => descriptor.Kind == "session"));
                writer.WriteNumber("legacy", descriptors.Count(descriptor => descriptor.Legacy));
                writer.WriteStartArray("items");
                foreach (LogSessionDescriptor descriptor in descriptors)
                {
                    writer.WriteStartObject();
                    writer.WriteString("sessionId", descriptor.SessionId);
                    writer.WriteString("kind", descriptor.Kind);
                    writer.WriteBoolean("legacy", descriptor.Legacy);
                    writer.WriteString("directory", descriptor.Directory);
                    if (descriptor.MainLog != null) writer.WriteString("mainLog", descriptor.MainLog);
                    if (descriptor.Op != null) writer.WriteString("op", descriptor.Op);
                    if (descriptor.Tool != null) writer.WriteString("tool", descriptor.Tool);
                    if (descriptor.StartedUtc.HasValue) writer.WriteString("startedUtc", OperationLogger.IsoTimestamp(descriptor.StartedUtc.Value));
                    if (descriptor.Pid.HasValue) writer.WriteNumber("pid", descriptor.Pid.Value);
                    writer.WriteNumber("bytes", descriptor.Bytes);
                    if (descriptor.EventCount.HasValue) writer.WriteNumber("eventCount", descriptor.EventCount.Value);
                    if (descriptor.Ok.HasValue) writer.WriteBoolean("ok", descriptor.Ok.Value);
                    else writer.WriteNull("ok");
                    if (descriptor.VerdictSource != null) writer.WriteString("verdictSource", descriptor.VerdictSource);
                    else writer.WriteNull("verdictSource");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        // ------------------------------------------------------------------ discovery helpers

        private static IEnumerable<string> SafeEnumerate(string directory, SearchOption option)
        {
            try { return Directory.EnumerateFileSystemEntries(directory, "*", option); }
            catch (Exception) { return Array.Empty<string>(); }
        }

        private static IEnumerable<string> PathsOf(LogSessionDescriptor descriptor) => new[]
        {
            descriptor.MainLog, descriptor.Trace, descriptor.Summary, descriptor.Directory,
        }.Where(path => !string.IsNullOrEmpty(path))!.Select(path => path!);

        private static string NormalizeLegacyId(string sessionId) =>
            sessionId.StartsWith(LegacyIdPrefix, StringComparison.Ordinal) ? sessionId.Substring(LegacyIdPrefix.Length) : sessionId;

        private static string LegacyId(string category, string fileName) => LegacyIdPrefix + category + "/" + fileName;

        private static LogSessionDescriptor DescribeSessionDirectory(string directory)
        {
            string id = Path.GetFileName(directory);
            string mainLog = Path.Combine(directory, OperationSession.MainLogFileName);
            string trace = Path.Combine(directory, OperationSession.TraceFileName);
            string summary = Path.Combine(directory, OperationSession.SummaryFileName);

            var lines = File.Exists(mainLog) ? ReadJsonLines(mainLog) : new List<JsonNode?>();
            JsonNode? summaryNode = File.Exists(summary) ? TryParse(File.ReadAllText(summary)) : null;

            string? op = lines.FirstOrDefault(node => node?["op"] != null)?["op"]?.GetValue<string>()
                ?? summaryNode?["op"]?.GetValue<string>();
            string? tool = lines.FirstOrDefault(node => node?["tool"] != null)?["tool"]?.GetValue<string>()
                ?? summaryNode?["tool"]?.GetValue<string>();
            DateTime? started = ParseTimestamp(summaryNode?["startedUtc"]?.GetValue<string>())
                ?? ParseTimestamp(lines.FirstOrDefault()?["ts"]?.GetValue<string>())
                ?? Directory.GetCreationTimeUtc(directory);
            int? pid = summaryNode?["pid"]?.GetValue<int>()
                ?? lines.FirstOrDefault()?["pid"]?.GetValue<int>()
                ?? ParsePidFromDirectoryName(id);
            bool? ok = summaryNode?["ok"]?.GetValue<bool>();
            string? verdict = summaryNode?["verdictSource"]?.GetValue<string>()
                ?? lines.LastOrDefault(node => node?["phase"]?.GetValue<string>() == "result")?["verdictSource"]?.GetValue<string>();

            return new LogSessionDescriptor
            {
                SessionId = id,
                Kind = "session",
                Directory = directory,
                MainLog = File.Exists(mainLog) ? mainLog : null,
                Trace = File.Exists(trace) ? trace : null,
                Summary = File.Exists(summary) ? summary : null,
                Op = op,
                Tool = tool,
                StartedUtc = started,
                Pid = pid,
                Legacy = false,
                Bytes = SizeOf(mainLog) + SizeOf(trace) + SizeOf(summary),
                EventCount = lines.Count,
                Ok = ok,
                VerdictSource = verdict,
            };
        }

        private static LogSessionDescriptor DescribeLegacyJsonl(string file, string category)
        {
            var lines = ReadJsonLines(file);
            JsonNode? first = lines.FirstOrDefault();
            JsonNode? last = lines.LastOrDefault();
            return new LogSessionDescriptor
            {
                SessionId = LegacyId(category, Path.GetFileName(file)),
                Kind = "legacy-jsonl",
                Directory = file,
                MainLog = file,
                Op = first?["op"]?.GetValue<string>(),
                Tool = first?["tool"]?.GetValue<string>() ?? category,
                StartedUtc = ParseTimestamp(first?["ts"]?.GetValue<string>()) ?? File.GetLastWriteTimeUtc(file),
                Pid = first?["pid"]?.GetValue<int>(),
                Legacy = true,
                Bytes = SizeOf(file),
                EventCount = lines.Count,
                Ok = last?["ok"]?.GetValue<bool>(),
                VerdictSource = last?["verdictSource"]?.GetValue<string>(),
            };
        }

        private static LogSessionDescriptor DescribeLegacyVcomText(string file)
        {
            string fileName = Path.GetFileName(file);
            DateTime? started = File.GetLastWriteTimeUtc(file);
            int? pid = null;
            try
            {
                string? first = File.ReadLines(file).FirstOrDefault();
                if (first != null)
                {
                    IReadOnlyList<string> tokens = OperationLogger.ParseArgv(first);
                    if (tokens.Count > 0) started = ParseTimestamp(tokens[0]) ?? started;
                    foreach (string token in tokens)
                    {
                        if (token.StartsWith("pid=", StringComparison.Ordinal) &&
                            int.TryParse(token.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                        {
                            pid = parsed;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // unreadable transcript: still list the file itself
            }

            return new LogSessionDescriptor
            {
                SessionId = LegacyId("vcom-text", fileName),
                Kind = "legacy-vcom-text",
                Directory = file,
                MainLog = file,
                Trace = file,
                Op = "vcom.session",
                Tool = "vcom",
                StartedUtc = started,
                Pid = pid,
                Legacy = true,
                Bytes = SizeOf(file),
                EventCount = CountLines(file),
                Ok = null,
                VerdictSource = "legacy:unparsed",
            };
        }

        private static IEnumerable<JsonNode?> ReadEvents(LogSessionDescriptor descriptor)
        {
            if (descriptor.MainLog == null || !File.Exists(descriptor.MainLog))
                return Array.Empty<JsonNode?>();

            switch (descriptor.Kind)
            {
                case "legacy-vcom-text":
                    return ParseVcomText(descriptor.MainLog, descriptor.SessionId);
                case "legacy-jsonl":
                    return ReadJsonLines(descriptor.MainLog)
                        .Select(node =>
                        {
                            if (node is not JsonObject obj) return node;
                            obj["legacy"] = true;
                            obj["sessionId"] = descriptor.SessionId;
                            return (JsonNode?)obj;
                        })
                        .ToList();
                default:
                    return ReadJsonLines(descriptor.MainLog);
            }
        }

        /// <summary>
        /// Turns the pre-2.4.15 VCOM text transcript back into the merged event shape, so one
        /// consumer can read both generations: <c>&lt;ts&gt; pid= seq= event=name k=v …</c>.
        /// </summary>
        private static List<JsonNode?> ParseVcomText(string path, string sessionId)
        {
            var events = new List<JsonNode?>();
            int ordinal = 0;
            foreach (string line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ordinal++;
                IReadOnlyList<string> tokens = OperationLogger.ParseArgv(line);
                if (tokens.Count == 0) continue;

                var node = new JsonObject
                {
                    ["ts"] = tokens[0],
                    ["sessionId"] = sessionId,
                    ["seq"] = ordinal,
                    ["legacy"] = true,
                    ["phase"] = "step",
                };

                for (int i = 1; i < tokens.Count; i++)
                {
                    string token = tokens[i];
                    int equals = token.IndexOf('=');
                    if (equals <= 0) continue;
                    string key = token.Substring(0, equals);
                    string value = token.Substring(equals + 1);

                    switch (key)
                    {
                        case "pid":
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)) node["pid"] = pid;
                            break;
                        case "seq":
                            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long traceSeq)) node["traceSeq"] = traceSeq;
                            break;
                        case "event":
                            node["step"] = value;
                            if (value == "session.open") node["phase"] = "start";
                            else if (value is "prompt" or "prompt.answer") node["phase"] = "prompt";
                            break;
                        case "text":
                        case "where":
                            node["notes"] = value;
                            break;
                        default:
                            node[key] = value;
                            break;
                    }
                }

                events.Add(node);
            }
            return events;
        }

        private static List<JsonNode?> ReadJsonLines(string path)
        {
            var nodes = new List<JsonNode?>();
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    nodes.Add(TryParse(line));
                }
            }
            catch (Exception)
            {
                // A half-written line must not hide the rest of the file.
            }
            return nodes;
        }

        private static JsonNode? TryParse(string text)
        {
            try { return JsonNode.Parse(text); }
            catch (Exception) { return null; }
        }

        private static DateTime? ParseTimestamp(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value)
                ? value
                : null;
        }

        private static int? ParsePidFromDirectoryName(string id)
        {
            // <YYYYmmdd>-<HHMMSS>-<pid>-<op>
            string[] parts = id.Split('-');
            return parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid)
                ? pid
                : null;
        }

        private static long SizeOf(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception) { return 0; }
        }

        private static int CountLines(string path)
        {
            try { return File.ReadLines(path).Count(); }
            catch (Exception) { return 0; }
        }
    }
}
