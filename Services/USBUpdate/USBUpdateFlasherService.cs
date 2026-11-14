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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kirin_Tool.Services.Diagnostics;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services.USBUpdate
{
    public class USBUpdateFlasherService
    {
        private readonly Action<string> _log;
        private readonly string _dloadDirectory;
        private readonly Func<string, string, Task<bool>> _onRetryRequired;

        public Action<int>? OnProgressUpdate;
        // SourceLabel is nullable (a package without a label); subscribers pass it through to
        // the progress dialog, which already renders a null label as its own fallback text.
        public Action<List<(string PartitionName, string? SourceLabel)>>? OnPartitionsDiscovered;
        public Action<int, string, int>? OnPartitionProgress;
        // Param payloads are nullable: source labels and failure reasons may legitimately be
        // null and all subscribers report them verbatim, so the delegate says so.
        public Action<int, string, bool, string?>? OnPartitionCompleted;
        public Action<string?>? OnExtractionStarted;
        public Action<int>? OnExtractionProgress;

        public USBUpdateFlasherService(Action<string> log, string dloadDirectory, Func<string, string, Task<bool>> onRetryRequired)
        {
            _log = log;
            _dloadDirectory = dloadDirectory;
            _onRetryRequired = onRetryRequired;
        }

        /// <summary>
        /// Redline gate: refuse a plan that writes device-keyed partitions unless
        /// <paramref name="allowRedline"/> is set. Kept as a separate static method so the
        /// decision ("refused after discovery, before any write") is reachable from a
        /// hardware-free test without a device on the bus. Behaviour is unchanged from the
        /// inlined version: log the hit, throw <see cref="InvalidOperationException"/> on
        /// refusal, otherwise log that the caller explicitly allowed it.
        /// </summary>
        internal static void EnforceRedlineWrite(IReadOnlyList<string> partitionNames, bool allowRedline, Action<string>? log)
        {
            IReadOnlyList<string> redlineHits = FlashPolicy.RedlineHits(partitionNames);
            if (redlineHits.Count == 0)
            {
                return;
            }

            log?.Invoke($"redline: plan writes {redlineHits.Count} device-keyed partition(s): {string.Join(", ", redlineHits)}");
            if (!allowRedline)
            {
                log?.Invoke("redline: refused, nothing has been written (allowRedline=false)");
                throw new InvalidOperationException(
                    $"Refusing to flash {redlineHits.Count} redline partition(s): {string.Join(", ", redlineHits)}. "
                    + "These hold the device identity, baseband calibration and key/DRM material; a wrong image is "
                    + "unrecoverable without a verified backup. No bytes have been written to the device. "
                    + "Re-run with allowRedline (CLI: --allow-redline) only if you have that backup.");
            }
            log?.Invoke("redline: proceeding because allowRedline=true");
        }

        /// <summary>
        /// Unpack the given package files and burn the discovered partitions over the
        /// USB-update channel.
        /// </summary>
        /// <param name="allowRedline">
        /// Device-keyed (redline) partitions are refused by default. The names are only
        /// known after unpacking, so the gate lives here rather than in the caller: pass
        /// <c>true</c> only after the user explicitly confirmed that THIS operation may
        /// overwrite identity/calibration/key material.
        /// </param>
        /// <returns>
        /// What the device actually established (D-04): `verified` / `partial` / `failed`. A
        /// non-verified transfer no longer hides behind a bool or behind an exception, so the GUI
        /// and the CLI can report all three states; the caller must read this value.
        /// </returns>
        public async Task<UsbUpdateVerdict> FlashPartitions(IEnumerable<(string? FilePath, string Label, List<string> SelectedPartitions)> filesData, CancellationToken cancellationToken = default, bool allowRedline = false)
        {
            // One op for the whole USB-update transfer (docs/op-scope-map.md row 7): every unpack,
            // the redline decision and every per-partition transfer is a step of this file.
            using var op = OpLog.Begin(OpNames.UsbUpdateFlash, OpLog.FlashTool,
                filesData.Select(data => data.FilePath).ToArray(), "USB update transfer");
            op.Step("dload directory: " + _dloadDirectory);
            op.Step("redline acknowledgement: " + (allowRedline ? "allow-redline" : "not acknowledged (refuse redline plans)"));

            // Was an empty catch: the directory is deleted exactly as before (a failure still lets
            // the caller continue and recreate it), but the listing and the failure are recorded.
            OpLog.CleanDirectory(_dloadDirectory, OpNames.UsbUpdateCleanup);
            Directory.CreateDirectory(_dloadDirectory);

            var sw = Stopwatch.StartNew();
            List<(string partitionName, string? sourceLabel)> allPartitions = new();
            List<string> mappingLines = new List<string>();
            bool unlockCodeExtracted = false;

            int fileIndex = 0;
            foreach (var data in filesData)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(data.FilePath) || !File.Exists(data.FilePath))
                {
                    // Previously a silent skip: an empty label or a vanished package would only
                    // show up later as "No partitions found to flash."
                    op.Step(OpLog.SkippedPrefix + (data.Label ?? "(no label)") + ": " + (data.FilePath ?? "(no path)")
                        + " is missing, package skipped");
                    fileIndex++;
                    continue;
                }

                string subDir = Path.Combine(_dloadDirectory, $"part{fileIndex}");
                Directory.CreateDirectory(subDir);

                var extractor = new USBUpdateApp(_log, subDir);
                OnExtractionStarted?.Invoke(data.Label);
                op.Step("unpack " + (data.Label ?? subDir) + " <- " + OpLog.FileStep(data.FilePath));

                // One usbupdate.unpack op per package: produced image count and total bytes.
                List<string> extracted;
                using (var unpack = OpLog.Begin(OpNames.UsbUpdateUnpack, new[] { data.FilePath }, data.Label))
                {
                    unpack.Step("output directory: " + subDir);
                    unpack.Step("partition filter: " + (data.SelectedPartitions == null || data.SelectedPartitions.Count == 0
                        ? "(all entries)"
                        : string.Join(", ", data.SelectedPartitions)));

                    extracted = extractor.ExtractAllPartitions(data.FilePath,
                        extractUnlockCode: !unlockCodeExtracted,
                        onProgress: (p) => OnExtractionProgress?.Invoke(p),
                        includePartitions: data.SelectedPartitions);

                    long unpackedBytes = 0;
                    foreach (string line in extracted)
                    {
                        string name = line.Split(' ')[0];
                        string imagePath = Path.Combine(subDir, name + ".img");
                        unpackedBytes += Math.Max(0, OpLog.SizeOrUnknown(imagePath));
                        unpack.Step(OpLog.OkPrefix + "unpacked " + name + ": " + OpLog.FileStep(imagePath));
                    }

                    if (!unlockCodeExtracted)
                    {
                        string unlockPath = Path.Combine(subDir, "unlockcode");
                        if (File.Exists(unlockPath))
                        {
                            File.Copy(unlockPath, Path.Combine(_dloadDirectory, "unlockcode"), true);
                            unlockCodeExtracted = true;
                            unpack.Step(OpLog.OkPrefix + "unlockcode extracted -> " + Path.Combine(_dloadDirectory, "unlockcode"));
                        }
                        else
                        {
                            unpack.Step(OpLog.DegradedPrefix + "no unlockcode entry in this package (the VCOM leg needs one)");
                        }
                    }

                    if (extracted.Count == 0)
                        unpack.Result(false, null, null, null, null, "files-written:0",
                            "the package produced no image; nothing can be transferred from it");
                    else
                        unpack.Result(true, null, null, null, null, "files-written:" + extracted.Count,
                            extracted.Count + " image(s), " + OpLog.Bytes(unpackedBytes) + " in " + subDir);
                }

                op.Step(extracted.Count == 0
                    ? OpLog.DegradedPrefix + (data.Label ?? subDir) + ": no image unpacked"
                    : OpLog.OkPrefix + (data.Label ?? subDir) + ": " + extracted.Count + " image(s) unpacked");

                foreach (var line in extracted)
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 1)
                    {
                        allPartitions.Add((parts[0], data.Label));
                        mappingLines.Add($"{parts[0]}|{subDir}");
                    }
                }
                fileIndex++;
            }

            // Super partition merging - detect and merge multiple super partitions
            var superIndices = new List<int>();
            for (int i = 0; i < allPartitions.Count; i++)
            {
                if (allPartitions[i].partitionName.Equals("super", StringComparison.OrdinalIgnoreCase))
                {
                    superIndices.Add(i);
                }
            }

            if (superIndices.Count > 1)
            {
                OnExtractionStarted?.Invoke("Merging super partitions");
                OnExtractionProgress?.Invoke(0);

                // Collect the super .img paths from each source directory
                var superImgPaths = new List<string>();
                string? firstSuperHeaderPath = null;

                foreach (var idx in superIndices)
                {
                    var mappingParts = mappingLines[idx].Split('|');
                    if (mappingParts.Length >= 2)
                    {
                        string sourceDir = mappingParts[1];
                        string imgPath = Path.Combine(sourceDir, $"{allPartitions[idx].partitionName}.img");
                        if (File.Exists(imgPath))
                        {
                            superImgPaths.Add(imgPath);
                            if (firstSuperHeaderPath == null)
                            {
                                string headerPath = Path.Combine(sourceDir, $"{allPartitions[idx].partitionName}.img.header");
                                if (File.Exists(headerPath))
                                    firstSuperHeaderPath = headerPath;
                            }
                        }
                    }
                }

                if (superImgPaths.Count > 1)
                {
                    string mergedDir = Path.Combine(_dloadDirectory, "super_merged");
                    Directory.CreateDirectory(mergedDir);
                    string mergedSuperPath = Path.Combine(mergedDir, "super.img");
                    op.Step("super merge: " + superImgPaths.Count + " fragment(s) -> " + mergedSuperPath);

                    string currentInput = superImgPaths[0];
                    for (int i = 1; i < superImgPaths.Count; i++)
                    {
                        string nextOutput = i == superImgPaths.Count - 1
                            ? mergedSuperPath
                            : Path.Combine(mergedDir, $"super_intermediate_{i}.img");

                        SuperMerger.MergeSuperImages(currentInput, superImgPaths[i], nextOutput, p =>
                        {
                            OnExtractionProgress?.Invoke((int)p);
                        }).GetAwaiter().GetResult();

                        if (i > 1 && currentInput.StartsWith(mergedDir) && File.Exists(currentInput))
                        {
                            try { File.Delete(currentInput); }
                            catch (Exception ex)
                            {
                                op.Step(OpLog.DegradedPrefix + "intermediate super image left on disk: " + currentInput
                                    + " (" + ex.Message + ")");
                            }
                        }

                        currentInput = nextOutput;
                    }

                    if (firstSuperHeaderPath != null)
                    {
                        string mergedHeaderPath = Path.Combine(mergedDir, "super.img.header");
                        File.Copy(firstSuperHeaderPath, mergedHeaderPath, true);

                        long mergedSize = new FileInfo(mergedSuperPath).Length;
                        byte[] headerBytes = File.ReadAllBytes(mergedHeaderPath);
                        if (headerBytes.Length >= 28)
                        {
                            headerBytes[24] = (byte)(mergedSize & 0xFF);
                            headerBytes[25] = (byte)((mergedSize >> 8) & 0xFF);
                            headerBytes[26] = (byte)((mergedSize >> 16) & 0xFF);
                            headerBytes[27] = (byte)((mergedSize >> 24) & 0xFF);
                            File.WriteAllBytes(mergedHeaderPath, headerBytes);
                        }
                    }

                    int firstSuperIndex = superIndices[0];
                    for (int i = superIndices.Count - 1; i >= 0; i--)
                    {
                        allPartitions.RemoveAt(superIndices[i]);
                        mappingLines.RemoveAt(superIndices[i]);
                    }

                    allPartitions.Insert(firstSuperIndex, ("super", "Merged Super"));
                    mappingLines.Insert(firstSuperIndex, $"super|{mergedDir}");

                    op.Step(OpLog.OkPrefix + "super merged: " + OpLog.FileStep(mergedSuperPath));
                    OnExtractionProgress?.Invoke(100);
                }
            }

            if (allPartitions.Count == 0)
            {
                op.Result(false, null, null, null, null, "files-written:0",
                    "no partition was discovered in the selected packages; nothing was sent to the device");
                throw new Exception("No partitions found to flash.");
            }

            op.Step("discovered " + allPartitions.Count + " partition(s): "
                + string.Join(", ", allPartitions.Select(p => p.partitionName)));
            OnPartitionsDiscovered?.Invoke(allPartitions);

            // Redline gate. Placed after discovery (that is when the package names are
            // known) and before list.txt / any USB transfer, so a refused plan has not
            // written a single byte to the device.
            IReadOnlyList<string> redlineHits = FlashPolicy.RedlineHits(allPartitions.Select(p => p.partitionName).ToList());
            if (redlineHits.Count > 0)
                op.Step("redline: plan writes " + redlineHits.Count + " device-keyed partition(s): "
                    + string.Join(", ", redlineHits));
            try
            {
                EnforceRedlineWrite(allPartitions.Select(p => p.partitionName).ToList(), allowRedline, _log);
            }
            catch (InvalidOperationException ex)
            {
                op.Result(false, null, null, null, ex.Message, "redline-gate",
                    "refused after discovery, before list.txt / any USB transfer: no bytes were written");
                throw;
            }
            op.Step(redlineHits.Count == 0
                ? "redline gate: plan holds no device-keyed partition"
                : "redline gate: proceeding because allowRedline=true");

            var listLines = allPartitions.Select(p => $"{p.partitionName} 1").ToList();
            File.WriteAllLines(Path.Combine(_dloadDirectory, "list.txt"), listLines);
            File.WriteAllLines(Path.Combine(_dloadDirectory, "partition_mapping.txt"), mappingLines);
            op.Step(OpLog.OkPrefix + "list.txt + partition_mapping.txt written ("
                + allPartitions.Count + " entries) in " + _dloadDirectory);

            cancellationToken.ThrowIfCancellationRequested();

            // The required set is the caller's own selection, never a hardcoded partition list
            // (D-04). An empty selection means "the plan decides": every list.txt entry with
            // flag == 1 is required.
            List<string> requiredPartitions = filesData
                .Where(data => data.SelectedPartitions != null && data.SelectedPartitions.Count > 0)
                .SelectMany(data => data.SelectedPartitions ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var usbService = new UsbDownloadService(_log, _dloadDirectory);
            usbService.OnRetryRequired += (title, message) =>
            {
                return _onRetryRequired(title, message).GetAwaiter().GetResult();
            };

            usbService.OnProgressUpdate += (progress) => OnProgressUpdate?.Invoke(progress);
            usbService.OnPartitionStarted += (index, name) => OnPartitionProgress?.Invoke(index, name, 0);
            usbService.OnPartitionProgressUpdate += (index, name, prog) => OnPartitionProgress?.Invoke(index, name, prog);
            usbService.OnPartitionCompleted += (index, name, success, msg) =>
            {
                // Per-partition event of the transfer: the outcome and, on failure, the reason.
                op.Step(success
                    ? OpLog.OkPrefix + "transferred " + name + " (#" + index + ")"
                    : OpLog.FailedPrefix + "transferred " + name + " (#" + index + "): " + (msg ?? "(no reason returned)"));
                OnPartitionCompleted?.Invoke(index, name, success, msg);
            };

            UsbUpdateVerdict verdict;
            try
            {
                verdict = await Task.Run(() => usbService.FlashImagesWithVerdict(
                    requiredPartitions.Count == 0 ? null : requiredPartitions,
                    // Every degraded/skipped/missing/written fact becomes a structured op step:
                    // the event token is the log prefix, the fields carry partition/required.
                    onEvent: evt => op.Step(evt.Line, "usb-update.event", evt.Fields,
                        evt.Kind == UsbUpdateEventKind.Written ? true : (bool?)null,
                        "usb-event:" + evt.Token)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                op.Result(false, null, null, null, null, "cancelled", "the transfer was cancelled; the device state is unknown");
                throw;
            }
            catch (Exception ex)
            {
                op.Result(false, null, null, null, ex.Message, OpLog.ExceptionVerdict(ex),
                    "USB transfer threw; message and exception type are preserved for the caller");
                throw new Exception($"USB Flashing failed: {ex.Message}", ex);
            }

            // Three-state mapping (D-04, docs/usb-update-verdict.md): only `verified` is ok.
            op.Step("usb-update verdict: " + verdict.Describe());
            op.Result(verdict.Verified, null, null, null, null,
                "usb-verdict:" + verdict.Token + ":required=" + verdict.RequiredWrittenCount + "/" + verdict.RequiredPartitions.Count,
                verdict.Describe());
            return verdict;
        }
    }
}
