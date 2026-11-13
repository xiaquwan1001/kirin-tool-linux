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

using Kirin_Tool.Models;
using Kirin_Tool.Services.Diagnostics;
using System.Diagnostics;
using System.Threading.Tasks;
using System;
using System.Threading;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace Kirin_Tool.Utils
{
    /// <summary>
    /// The single place in the process tree where a child process is started.
    /// Every run writes two things: the legacy human diagnostics file
    /// (`kirintool_log_*.log`, unchanged, fastboot only) and one structured JSONL
    /// operation file (<see cref="OperationLogger"/>), for every tool.
    /// </summary>
    public static class ProcessRunner
    {
        private static readonly object LogLock = new object();

        public static Task<ProcessResult> RunAsync(string executablePath, string arguments, int timeoutMinutes = 300, int timeoutSeconds = 0, string op = null)
        {
            var info = StartInfo(executablePath);
            info.Arguments = arguments;
            return RunAsync(info, arguments, OperationLogger.ParseArgv(arguments), timeoutMinutes, timeoutSeconds, op, null);
        }

        public static Task<ProcessResult> RunAsync(string executablePath, IReadOnlyList<string> arguments, int timeoutMinutes = 300, int timeoutSeconds = 0, string op = null)
        {
            var info = StartInfo(executablePath);
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            return RunAsync(info, Quote(arguments), arguments, timeoutMinutes, timeoutSeconds, op, null);
        }

        /// <summary>
        /// Same contract as the list overload, but output is read chunk-by-chunk and echoed to
        /// <paramref name="progress"/> as it arrives, so a long bootloader unlock can be rendered
        /// live. Logging, timeout and kill-tree behaviour are shared with every other command:
        /// this overload exists so no caller has to build its own <see cref="ProcessStartInfo"/>
        /// (docs/op-logging.md §4).
        /// </summary>
        public static Task<ProcessResult> RunStreamingAsync(string executablePath, IReadOnlyList<string> arguments, IProgress<string> progress = null, int timeoutMinutes = 300, int timeoutSeconds = 0, string op = null)
        {
            var info = StartInfo(executablePath);
            foreach (string arg in arguments) info.ArgumentList.Add(arg);
            return RunAsync(info, Quote(arguments), arguments, timeoutMinutes, timeoutSeconds, op, progress);
        }

        private static string Quote(IReadOnlyList<string> arguments) =>
            string.Join(" ", arguments.Select(a => System.Text.Json.JsonSerializer.Serialize(a)));

        private static ProcessStartInfo StartInfo(string executablePath) => new ProcessStartInfo
        {
            FileName = executablePath, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };

        private static async Task<ProcessResult> RunAsync(ProcessStartInfo processStartInfo, string arguments, IReadOnlyList<string> argv, int timeoutMinutes, int timeoutSeconds, string op, IProgress<string> progress)
        {
            string executablePath = processStartInfo.FileName;
            string tool = ToolName(executablePath);
            string operation = string.IsNullOrWhiteSpace(op) ? DeriveOp(tool, argv) : op;

            using var scope = OperationLogger.Begin(operation, tool, argv);
            scope.Step("exec " + executablePath);

            using var process = new Process { StartInfo = processStartInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                scope.Result(false, null, null, null, ex.Message, "exception:" + ex.GetType().Name, "child process could not be started");
                throw;
            }

            var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.FromMinutes(timeoutMinutes);
            using var cts = new CancellationTokenSource(timeout);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var combined = progress == null ? null : new StringBuilder();
                var combinedLock = new object();

                Task<string> outputTask = progress == null
                    ? process.StandardOutput.ReadToEndAsync()
                    : PumpAsync(process.StandardOutput, combined, combinedLock, progress);
                Task<string> errorTask = progress == null
                    ? process.StandardError.ReadToEndAsync()
                    : PumpAsync(process.StandardError, combined, combinedLock, progress);

                await process.WaitForExitAsync(cts.Token);

                string output = await outputTask;
                string error = await errorTask;

                var result = new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    Output = output + error,
                    Arguments = arguments
                };

                WriteLog(executablePath, arguments, result.Output);

                // stdout and stderr stay split in the structured log, so "OKAY came back on
                // stderr" can no longer masquerade as a device answer (audit S24).
                scope.Result(
                    result.IsSuccess,
                    process.ExitCode,
                    stopwatch.ElapsedMilliseconds,
                    output,
                    error,
                    result.IsSuccess
                        ? "exit-code"
                        : "exit-code:" + process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));

                return result;
            }
            catch (OperationCanceledException)
            {
                // Kill the whole tree so child processes do not outlive the timeout.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                WriteLog(executablePath, arguments, $"[Process Timed Out after {timeout.TotalSeconds} seconds]");
                scope.Result(false, null, stopwatch.ElapsedMilliseconds, null, null, "timeout", $"process timed out after {timeout.TotalSeconds:F0} seconds");
                throw new TimeoutException($"Process timed out after {timeout.TotalSeconds:F0} seconds");
            }
            catch (Exception ex)
            {
                scope.Result(false, null, stopwatch.ElapsedMilliseconds, null, ex.Message, "exception:" + ex.GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Chunked reader used by <see cref="RunStreamingAsync"/>. Returns the full text of the
        /// stream; <paramref name="combined"/> accumulates stdout+stderr in arrival order for the
        /// live progress callback (same text the GUI already showed).
        /// </summary>
        private static async Task<string> PumpAsync(StreamReader reader, StringBuilder combined, object combinedLock, IProgress<string> progress)
        {
            var builder = new StringBuilder();
            var chunk = new char[256];
            try
            {
                while (true)
                {
                    int read = await reader.ReadAsync(chunk, 0, chunk.Length);
                    if (read == 0) break;

                    string snapshot;
                    lock (builder)
                    {
                        builder.Append(chunk, 0, read);
                        snapshot = builder.ToString();
                    }

                    lock (combinedLock)
                    {
                        combined.Append(chunk, 0, read);
                    }

                    progress.Report(snapshot);
                }
            }
            catch (Exception)
            {
                // The stream closed because the process died or was killed. The exit code is
                // the authoritative verdict (logged by the caller) but the text already read
                // must survive, which is what the audit's S26 finding was about.
            }

            lock (builder)
            {
                return builder.ToString();
            }
        }

        private static string ToolName(string executablePath)
        {
            string name = Path.GetFileName(executablePath) ?? string.Empty;
            if (name.Length == 0) name = executablePath ?? string.Empty;
            if (name.Contains("fastboot", StringComparison.OrdinalIgnoreCase)) return "fastboot";
            if (name.Contains("adb", StringComparison.OrdinalIgnoreCase)) return "adb";
            return "internal";
        }

        /// <summary>`fastboot flash boot x.img` → `fastboot.flash`; otherwise `tool.run`.</summary>
        private static string DeriveOp(string tool, IReadOnlyList<string> argv)
        {
            string verb = argv?.FirstOrDefault(a => !string.IsNullOrEmpty(a) && !a.StartsWith("-", StringComparison.Ordinal)) ?? "run";
            return tool + "." + verb;
        }

        private static void WriteLog(string executablePath, string arguments, string output)
        {
            if (!executablePath.Contains("fastboot", StringComparison.OrdinalIgnoreCase)) return;
            WriteDiagnostic($"{Path.GetFileName(executablePath)} {arguments}", output);
        }

        public static void WriteDiagnostic(string source, string output)
        {
            try
            {
                string fileName = $"kirintool_log_{DateTime.Now:yyyy_MM_dd}.log";
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                string cmdText = source;
                string logMessage = $"[{timestamp}] {cmdText}\n{output}\n\n";

                // Prefer a log dir next to the executable; when that is not
                // writable (AppImage squashfs, root-owned pacman install),
                // fall back to the user's XDG state directory.
                string exeLogDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                string xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
                if (string.IsNullOrWhiteSpace(xdgState))
                {
                    xdgState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
                }
                string userLogDir = Path.Combine(xdgState, "Kirin-Tool", "log");

                foreach (var logDir in new[] { exeLogDir, userLogDir })
                {
                    try
                    {
                        Directory.CreateDirectory(logDir);
                        lock (LogLock)
                        {
                            File.AppendAllText(Path.Combine(logDir, fileName), logMessage);
                        }
                        return;
                    }
                    catch
                    {
                        // try the next candidate
                    }
                }
            }
            catch {}
        }
    }
}
