// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Kirin_Tool.Models;

namespace Kirin_Tool.Services.Diagnostics
{
    /// <summary>
    /// Canonical op names (docs/op-scope-map.md). Every device-touching action opens exactly
    /// one of these; the name is what a ticket or audit greps for, so it must not drift.
    /// </summary>
    public static class OpNames
    {
        /// <summary>Flashing the images of a Huawei flashing XML (plan-level, CLI and GUI).</summary>
        public const string FlashXml = "flash.xml";

        /// <summary>Flashing an UPDATE.APP / PTABLE.APP plan.</summary>
        public const string FlashOta = "flash.ota";

        /// <summary>Rescue-partition write of the reboot-to-USB-Update segment (ERECOVERY*).</summary>
        public const string FlashRescue = "flash.rescue";

        /// <summary>
        /// One image written with no enclosing plan scope (the GUI per-image loops call the
        /// flasher one partition at a time). Verdict logic is the flasher's own.
        /// </summary>
        public const string FlashImage = "flash.image";

        /// <summary>The pre-op backup run: per-partition dump plus manifest.</summary>
        public const string BackupPreOp = "backup.preop";

        /// <summary>One partition dump that is not part of a backup run (standalone read).</summary>
        public const string BackupDump = "backup.dump";

        /// <summary>USB-update transfer of an unpacked package.</summary>
        public const string UsbUpdateFlash = "usbupdate.flash";

        /// <summary>Unpacking one package into the dload directory.</summary>
        public const string UsbUpdateUnpack = "usbupdate.unpack";

        /// <summary>Deleting the dload directory (the listing is recorded before it is removed).</summary>
        public const string UsbUpdateCleanup = "usbupdate.dload-cleanup";

        /// <summary>Dumping (or reading) oeminfo from the device.</summary>
        public const string OemInfoRead = "oeminfo.read";

        /// <summary>Model/region conversion of an oeminfo image.</summary>
        public const string OemInfoConvert = "oeminfo.convert";

        /// <summary>Writing a converted oeminfo image back to the device.</summary>
        public const string OemInfoWrite = "oeminfo.write";

        /// <summary>Erasing the oeminfo version fields (4 × oem oeminfoerase-*ver) to allow a downgrade.</summary>
        public const string DowngradeErase = "downgrade.erase";
    }

    /// <summary>Tool/category values used by the op scopes below (they become directory names).</summary>
    public static class OpTools
    {
        public const string Flash = "flash";
        public const string Internal = "internal";
    }

    /// <summary>
    /// Shared helpers for the write-path op scopes: verdict strings, byte counts and the
    /// dload-directory listing. Kept in one place so every path names its evidence the same
    /// way (docs/op-scope-map.md, section "verdictSource values").
    /// </summary>
    public static class OpLog
    {
        /// <summary>The flasher category (directory name) for plan-level flash ops.</summary>
        public const string FlashTool = OpTools.Flash;

        /// <summary>Prefix of a step line that reports a successful unit of work.</summary>
        public const string OkPrefix = "ok: ";

        /// <summary>Prefix of a step line that reports a failure (the run may continue).</summary>
        public const string FailedPrefix = "failed: ";

        /// <summary>Prefix of a step line that reports a declared degradation (skip / unsupported).</summary>
        public const string DegradedPrefix = "degraded: ";

        /// <summary>Prefix of a step line that reports something deliberately not done.</summary>
        public const string SkippedPrefix = "skipped: ";

        /// <summary>How many paths of a dload listing are written into one step line.</summary>
        private const int ListingLimit = 200;

        /// <summary>Begin an op scope with an explicit tool category.</summary>
        public static OperationScope Begin(string op, string tool, IReadOnlyList<string>? argv = null, string? notes = null)
            => OperationLogger.Begin(op, tool, argv, notes);

        /// <summary>Begin an op scope in the internal (non-child-process) category.</summary>
        public static OperationScope Begin(string op, IReadOnlyList<string>? argv = null, string? notes = null)
            => OperationLogger.Begin(op, OpTools.Internal, argv, notes);

        /// <summary>The flasher's own verdict rule: exit-code, with the number on failure.</summary>
        public static string ExitVerdict(ProcessResult? result)
        {
            if (result == null) return "exit-code";
            return result.IsSuccess
                ? "exit-code"
                : "exit-code:" + result.ExitCode.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The plan-level rule used by flash-ota and flash-xml:
        /// ok = exit code 0 AND the loader printed OKAY.
        /// </summary>
        public static string OkayVerdict(ProcessResult? result, bool ok)
        {
            if (ok) return "output-contains:OKAY";
            if (result == null) return "output-missing:OKAY";
            return result.IsSuccess ? "output-missing:OKAY" : ExitVerdict(result);
        }

        /// <summary>all-ok, or how many of the plan entries failed.</summary>
        public static string OutcomeVerdict(int okCount, int total)
        {
            if (total <= 0) return "partition-outcomes:none";
            return okCount == total
                ? "partition-outcomes:all-ok"
                : "partition-outcomes:" + (total - okCount).ToString(CultureInfo.InvariantCulture) + "/"
                  + total.ToString(CultureInfo.InvariantCulture) + "-failed";
        }

        /// <summary>An unexpected throw decided the outcome.</summary>
        public static string ExceptionVerdict(Exception ex) => "exception:" + ex.GetType().Name;

        /// <summary>One-line outcome summary of a ProcessResult for a step note.</summary>
        public static string ResultStep(string what, ProcessResult? result, bool ok)
        {
            string exit = result == null ? "exit=?" : "exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture);
            string okay = result != null && (result.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase)
                ? "okay=yes"
                : "okay=no";
            return (ok ? OkPrefix : FailedPrefix) + what + " -> " + exit + " " + okay;
        }

        /// <summary>Collapse a multi-line tool output into one bounded line for a step note.</summary>
        public static string OneLine(string? text, int maxCharacters = 300)
        {
            if (string.IsNullOrWhiteSpace(text)) return "(no output)";
            string single = text!.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return single.Length <= maxCharacters ? single : single.Substring(0, maxCharacters) + "...";
        }

        /// <summary>Size of a file in bytes, or -1 when it does not exist (never throws).</summary>
        public static long SizeOrUnknown(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            try
            {
                var info = new FileInfo(path!);
                return info.Exists ? info.Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>Path plus its size, or an explicit "missing" marker.</summary>
        public static string FileStep(string? path)
        {
            long size = SizeOrUnknown(path);
            return size < 0 ? path + " (missing)" : path + " (" + Bytes(size) + ")";
        }

        /// <summary>Human byte count that keeps the exact number too.</summary>
        public static string Bytes(long bytes)
        {
            if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
            string[] units = { "KiB", "MiB", "GiB", "TiB" };
            double value = bytes;
            int unit = -1;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit]
                + " (" + bytes.ToString(CultureInfo.InvariantCulture) + " B)";
        }

        /// <summary>
        /// Files (recursively) under a directory with their sizes. Never throws: a broken or
        /// unreadable tree is reported as a partial listing plus an error note.
        /// </summary>
        public static (int Count, long Bytes, IReadOnlyList<string> Paths, string? Error) Snapshot(string? directory)
        {
            var paths = new List<string>();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return (0, 0, paths, null);
            long total = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory!, "*", SearchOption.AllDirectories))
                {
                    paths.Add(file);
                    try { total += new FileInfo(file).Length; } catch { /* the listing still names it */ }
                }
                return (paths.Count, total, paths, null);
            }
            catch (Exception ex)
            {
                return (paths.Count, total, paths, ex.Message);
            }
        }

        /// <summary>The bounded path listing that goes into one step line before a delete.</summary>
        public static string Listing(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return "(empty)";
            IEnumerable<string> shown = paths.Take(ListingLimit);
            string text = string.Join(", ", shown);
            return paths.Count > ListingLimit
                ? text + " ...(+" + (paths.Count - ListingLimit).ToString(CultureInfo.InvariantCulture) + " more)"
                : text;
        }

        /// <summary>
        /// Delete the dload directory exactly like the previous inline
        /// "if (Directory.Exists(d)) { try { Directory.Delete(d, true); } catch { } }", but record
        /// the listing and total size first and record a failed delete instead of swallowing it.
        /// Control flow (and the swallowed exception) is unchanged: the caller continues.
        /// </summary>
        public static void CleanDirectory(string directory, string? opName = null)
        {
            if (!Directory.Exists(directory)) return;
            using var op = Begin(opName ?? OpNames.UsbUpdateCleanup, new[] { directory }, "dload directory cleanup");
            var snapshot = Snapshot(directory);
            op.Step("before delete: " + snapshot.Count.ToString(CultureInfo.InvariantCulture) + " files, "
                + Bytes(snapshot.Bytes) + (snapshot.Error == null ? "" : " (listing error: " + snapshot.Error + ")"));
            op.Step("deleting: " + Listing(snapshot.Paths));
            try
            {
                Directory.Delete(directory, true);
                op.Result(true, null, null, null, null, "directory-deleted",
                    "removed " + snapshot.Count.ToString(CultureInfo.InvariantCulture) + " files, " + Bytes(snapshot.Bytes));
            }
            catch (Exception ex)
            {
                // Same behaviour as before (the caller continues and recreates the directory),
                // but the failure is now an explicit event instead of an empty catch.
                op.Result(false, null, null, null, ex.Message, ExceptionVerdict(ex),
                    "dload directory could not be deleted; the caller continues as before");
            }
        }
    }
}
