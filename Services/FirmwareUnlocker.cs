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
using Kirin_Tool.Security;
using Kirin_Tool.Services.Verdict;
using Org.BouncyCastle.Crypto;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Kirin_Tool.Services
{

    /// <summary>Why an unlock attempt failed; the CLI maps this onto its stable exit codes.</summary>
    public enum UnlockFailureKind
    {
        None = 0,
        /// <summary>No VCOM (12d1:3609) device, unopenable port, or no reconnect after the replug.</summary>
        NoDevice,
        /// <summary>The device answered badly (strict ACK timeout / non-0xAA) or the tty died mid-transfer.</summary>
        DeviceRejected,
        /// <summary>Loader directory/file missing, or the .ktl payload could not be decrypted.</summary>
        LocalFile,
        /// <summary>The operator cancelled at a cable prompt.</summary>
        Cancelled,
        Other,
        /// <summary>
        /// The loader set is incomplete: a stage's <c>.ktl</c> is missing or does not DTL-decrypt.
        /// Detected before the first frame, so nothing was uploaded and no gate can be argued from
        /// "no exception thrown" (audit F02/S15).
        /// </summary>
        MissingLoader,
    }

    /// <summary>Which loader stages of the address table to run (split runs around a manual replug).</summary>
    public enum UnlockStageSelection
    {
        /// <summary>Every stage (GUI behaviour).</summary>
        All,
        /// <summary>START + null only; the replug is left to a follow-up <see cref="Rest"/> run.</summary>
        Null,
        /// <summary>START + null (+ replug/reconnect) + XLOADER; nothing after the pwn exploit.</summary>
        Xloader,
        /// <summary>Everything except null, and <b>no START frame</b> (the BootROM is already in loader mode).</summary>
        Rest,
    }

    /// <summary>One stage's outcome, reported to the CLI/MCP layer for the JSON summary.</summary>
    public sealed record UnlockStageReport(
        string Stage,
        string File,
        int Address,
        bool Pwn,
        long Bytes,
        int Frames,
        int Acks,
        string Status,
        long DurationMs,
        string? Note);

    /// <summary>
    /// CLI/MCP injection points. Left at defaults (options == null) the service behaves exactly
    /// like the shipped GUI: fixed loader directory, lenient ACK, no stage splitting, prompts via
    /// the caller's interaction handler.
    /// </summary>
    public sealed class UnlockOptions
    {
        /// <summary>Fully resolved per-chip loader directory; default <c>&lt;base&gt;/loaders/&lt;cpu&gt;</c>.</summary>
        public string? LoaderDirectory { get; init; }
        /// <summary>Flasher injection (explicit port, ACK policy, frame observer).</summary>
        public VcomFlasherOptions? Flasher { get; init; }
        public UnlockStageSelection Stages { get; init; } = UnlockStageSelection.All;
        public Action<UnlockStageReport>? StageObserver { get; init; }
        /// <summary>Retry window for the initial connect (used by a <c>rest</c> run right after a replug).</summary>
        public TimeSpan ConnectRetryWindow { get; init; } = TimeSpan.Zero;
        /// <summary>Settling delay after the null stage before the cable prompt (GUI: 1 s).</summary>
        public TimeSpan NullStageSettlingDelay { get; init; } = TimeSpan.FromSeconds(1);
        /// <summary>Session audit log: every progress line, stage boundary, prompt and exception.</summary>
        public VcomSessionLogger? Logger { get; init; }
        /// <summary>
        /// Called with the stage name right before its first frame goes out, so a frame observer
        /// can attribute ACK counts to a stage (addresses alone are ambiguous: on hisi970..990 the
        /// <c>null</c> and <c>XLOADER</c> stages share the 0x22000 load address).
        /// </summary>
        public Action<string>? StageBeginObserver { get; init; }
    }

    public class UnlockResult
    {
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public UnlockFailureKind FailureKind { get; set; } = UnlockFailureKind.None;
        /// <summary>Per-stage reports (empty for the GUI path, which reports through progressItems).</summary>
        public IReadOnlyList<UnlockStageReport> Stages { get; set; } = Array.Empty<UnlockStageReport>();
    }
    public class FirmwareUnlocker
    {
        public static readonly Dictionary<string, List<(string Name, int Address, bool PwnFlag)>> CpuAddresses = new Dictionary<string, List<(string, int, bool)>>
        {
            { "hisi65x_a", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi65x_b", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi620", new List<(string, int, bool)>{ ("XLOADER", unchecked((int)0xF9800800), false), ("FASTBOOT", 0x06800000, false) } },
            { "hisi620c", new List<(string, int, bool)>{ ("XLOADER", unchecked((int)0xF9800800), false), ("FASTBOOT", 0x06800000, false) } },
            { "hisi925", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi935", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi950", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi955", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("FASTBOOT", 0x10000000, false) } },
            { "hisi960", new List<(string, int, bool)>{ ("XLOADER", 0x00020000, false), ("UCE", unchecked((int)0x6A908000), false), ("FASTBOOT", 0x1AC00000, false) } },
            { "hisi970", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60049000), false), ("FASTBOOT", 0x16800000, false) } },
            { "hisi980", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60049000), false), ("FASTBOOT", unchecked((int)0x1A400000), false) } },
            { "hisik3v2", new List<(string, int, bool)>{ ("USBLOADER", unchecked((int)0xF8000000), false) } },
            { "hisi710", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x6000D000), false), ("FASTBOOT", 0x1C000000, false) } },
            { "hisi710a", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x6000D000), false), ("FASTBOOT", 0x1C000000, false) } },
            { "hisi810", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60000000), false), ("FASTBOOT", 0x1C000000, false) } },
            { "hisi820", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60000000), false), ("FASTBOOT", 0x1A400000, false), ("BL2", 0x1E400000, false) } },
            { "hisi985", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60000000), false), ("FASTBOOT", 0x1A400000, false), ("BL2", 0x1E400000, false) } },
            { "hisi990", new List<(string, int, bool)>{ ("null", 0x22000, false), ("XLOADER", 0x22000, true), ("UCE", unchecked((int)0x60000000), false), ("FASTBOOT", 0x1A400000, false), ("BL2", 0x1E400000, false) } },
        };

        public Task<UnlockResult> UnlockFastboot(string cpu, ObservableCollection<ProgressItemViewModel> progressItems, IProgress<string> overallProgress, Func<string, Task<bool>> interactionHandler = null, bool useFastFlashLoader = false) =>
            UnlockFastboot(cpu, progressItems, overallProgress, interactionHandler, useFastFlashLoader, null);

        /// <summary>
        /// Loads the unlocked-boot loaders into RAM over the VCOM BootROM. <paramref name="options"/>
        /// is the CLI/MCP injection point; when it is null the behaviour is byte-for-byte what the
        /// GUI has always done (fixed loader directory, lenient ACK, all stages, caller prompts).
        /// </summary>
        public async Task<UnlockResult> UnlockFastboot(string cpu, ObservableCollection<ProgressItemViewModel> progressItems, IProgress<string> overallProgress, Func<string, Task<bool>> interactionHandler, bool useFastFlashLoader, UnlockOptions options)
        {
            var stages = options?.Stages ?? UnlockStageSelection.All;
            bool sendStart = stages != UnlockStageSelection.Rest;
            bool includesNull = stages is UnlockStageSelection.All or UnlockStageSelection.Null or UnlockStageSelection.Xloader;
            bool continuesPastNull = stages is UnlockStageSelection.All or UnlockStageSelection.Xloader;
            var reports = new List<UnlockStageReport>();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var logger = options?.Logger;
            if (logger != null)
            {
                // Every overallProgress.Report(...) below is also written to the session log, and
                // cable prompts are recorded with the operator's answer.
                overallProgress = new VcomProgressTee(overallProgress, logger);
                if (interactionHandler != null) interactionHandler = WrapInteraction(interactionHandler, logger);
                logger.Event("unlock.start",
                    ("cpu", cpu),
                    ("stages", stages.ToString().ToLowerInvariant()),
                    ("loaderDirectory", options?.LoaderDirectory),
                    ("useFastFlashLoader", useFastFlashLoader));
            }

            try
            {
                if (!CpuAddresses.TryGetValue(cpu, out var addressTable))
                {
                    return Fail(UnlockFailureKind.LocalFile, $"Unsupported CPU '{cpu}'.");
                }

                var loaderDir = options?.LoaderDirectory ?? Path.Combine(AppContext.BaseDirectory, "loaders", cpu);
                if (!Directory.Exists(loaderDir))
                {
                    return Fail(UnlockFailureKind.LocalFile, $"Loader directory for {cpu} not found at '{loaderDir}'");
                }

                // ---- pre-flight: the loader set is verified before the first frame ----
                // A partial loader set cannot boot anything, so a missing/undecryptable stage is a
                // hard failure here (audit F02/S15: it used to be "Skipped" + IsSuccess = true).
                overallProgress.Report("Verifying the loader set before the first frame...");
                var precheck = await LoaderPrecheck.RunAsync(cpu, loaderDir, useFastFlashLoader, name => StageSelected(name, stages));
                foreach (var broken in precheck.Stages.Where(stage => stage.Data == null))
                {
                    var brokenItem = progressItems.FirstOrDefault(p => p.FileName == broken.Stage);
                    if (brokenItem != null)
                    {
                        brokenItem.ProgressValue = 0;
                        brokenItem.StatusText = "Missing";
                    }
                    Report(new UnlockStageReport(broken.Stage, broken.File, broken.Address, broken.Pwn, 0, 0, 0, "missing", 0, broken.Problem));
                }
                if (!precheck.Ok)
                {
                    overallProgress.Report($"Loader pre-check failed: {precheck.Describe}");
                    return Fail(UnlockFailureKind.MissingLoader,
                        $"Loader pre-check failed before any frame was sent: {precheck.Describe}");
                }
                logger?.Event("stage.precheck",
                    ("ok", true),
                    ("stages", precheck.Stages.Count),
                    ("bytes", precheck.Stages.Sum(stage => stage.Data!.LongLength)));

                using (var flasher = new VcomFlasher(options?.Flasher))
                {
                    overallProgress.Report("Attempting to connect to device in VCOM mode...");
                    await ConnectAsync(flasher, options);
                    if (sendStart)
                    {
                        overallProgress.Report("Device connected successfully! Sending handshake...");
                        await flasher.SendStartFrame();
                        overallProgress.Report("Handshake sent. Starting flash process...");
                    }
                    else
                    {
                        overallProgress.Report("Device connected successfully. Continuing after the cable replug: no START frame is re-sent.");
                    }

                    foreach (var loaderInfo in addressTable)
                    {
                        if (!StageSelected(loaderInfo.Name, stages)) continue;

                        var currentItem = progressItems.FirstOrDefault(p => p.FileName == loaderInfo.Name);
                        if (currentItem == null) continue;

                        string fileName = $"{loaderInfo.Name.ToLower()}.ktl";
                        if (useFastFlashLoader && loaderInfo.Name == "FASTBOOT")
                        {
                            fileName = "fastbootf.ktl";
                        }

                        var filePath = Path.Combine(loaderDir, fileName);
                        var payload = precheck.Stages.FirstOrDefault(stage =>
                            string.Equals(stage.Stage, loaderInfo.Name, StringComparison.Ordinal) &&
                            string.Equals(stage.File, fileName, StringComparison.Ordinal));
                        if (payload?.Data == null)
                        {
                            // Defence in depth: the pre-flight already refused the run, so this branch
                            // only fires when the loader vanished between the check and its stage.
                            currentItem.ProgressValue = 0;
                            currentItem.StatusText = "Missing";
                            overallProgress.Report($"Loader {fileName} is gone before its stage; aborting with no further frames.");
                            Report(new UnlockStageReport(loaderInfo.Name, fileName, loaderInfo.Address, loaderInfo.PwnFlag, 0, 0, 0, "missing", 0,
                                payload?.Problem ?? $"missing loader file: {fileName}"));
                            return Fail(UnlockFailureKind.MissingLoader, $"Loader '{fileName}' is missing; the chain was aborted before this stage.");
                        }

                        byte[] decryptedData = payload.Data;
                        currentItem.StatusText = "Verified";
                        overallProgress.Report($"Payload {Path.GetFileName(filePath)} verified ({decryptedData.Length} bytes).");
                        logger?.Event("stage.start",
                            ("stage", loaderInfo.Name),
                            ("file", fileName),
                            ("addr", "0x" + loaderInfo.Address.ToString("X8")),
                            ("pwn", loaderInfo.PwnFlag));
                        var stageWatch = System.Diagnostics.Stopwatch.StartNew();
                        int framesBefore = flasher.FramesSent;
                        int acksBefore = flasher.AckOk;

                        currentItem.StatusText = "Uploading...";
                        overallProgress.Report($"Uploading {loaderInfo.Name}...");

                        var fileProgress = new Progress<(long sent, long total)>(fp => {
                            if (fp.total > 0) currentItem.ProgressValue = ((double)fp.sent / fp.total) * 100;
                        });

                        options?.StageBeginObserver?.Invoke(loaderInfo.Name);
                        await flasher.UploadData(decryptedData, loaderInfo.Address, (loaderInfo.PwnFlag, cpu), fileProgress);

                        currentItem.ProgressValue = 100;
                        currentItem.StatusText = "Done";
                        overallProgress.Report($"Finished uploading {loaderInfo.Name}.");
                        Report(new UnlockStageReport(loaderInfo.Name, fileName, loaderInfo.Address, loaderInfo.PwnFlag,
                            decryptedData.LongLength, flasher.FramesSent - framesBefore, flasher.AckOk - acksBefore, "uploaded",
                            stageWatch.ElapsedMilliseconds, null));

                        if (continuesPastNull && includesNull && (cpu == "hisi980" || cpu == "hisi810" || cpu == "hisi820" || cpu == "hisi985" || cpu == "hisi990") && loaderInfo.Name == "null")
                        {
                            overallProgress.Report("Waiting for cable manipulation...");
                            await Task.Delay(options?.NullStageSettlingDelay ?? TimeSpan.FromSeconds(1));
                            // On Linux, release the old tty before the requested replug.
                            if (OperatingSystem.IsLinux() && interactionHandler != null) flasher.Dispose();
                            if (interactionHandler != null)
                            {
                                bool proceed = await interactionHandler("If you are using a modified cable (Harmony TP), please unplug it from the COMPUTER SIDE, wait a few seconds, then plug it back in.");
                                if (!proceed) return Fail(UnlockFailureKind.Cancelled, "Operation cancelled by user.");
                                if (OperatingSystem.IsLinux())
                                {
                                    overallProgress.Report("Waiting for COM 1.0 to reconnect...");
                                    await flasher.ReconnectAfterCableAsync();
                                    overallProgress.Report("COM 1.0 reconnected. Continuing with the next loader...");
                                }
                            }
                        }
                    }

                    if (stages == UnlockStageSelection.All && (cpu == "hisi980" || cpu == "hisi810" || cpu == "hisi820" || cpu == "hisi985" || cpu == "hisi990") && interactionHandler != null)
                    {
                        string replugStr = useFastFlashLoader ? "If you are using a modified cable (Harmony TP), please unplug it from BOTH SIDES, and connect the device with a standard cable." : "If you are using a modified cable (Harmony TP), please unplug it from the COMPUTER SIDE, wait a few seconds, then plug it back in.";
                        if (cpu == "hisi810" || cpu == "hisi820" || cpu == "hisi985" || cpu == "hisi990")
                        {
                            replugStr = "If you are using a modified cable (Harmony TP), please unplug it from BOTH SIDES, and connect the device with a standard cable.";
                        }

                        // await interactionHandler("If you are using a modified cable (Harmony TP):\n1. Unplug it from both sides\n2. Connect the device to the computer with a normal cable\n3. Wait a few seconds\n4. Reconnect using the modified cable");
                        await interactionHandler(replugStr);
                    }
                }

                // "Uploaded" is all this service can prove: it holds no device-side evidence (the
                // ACK ledger lives in the caller), so it must not claim more than the frames it wrote.
                overallProgress.Report("All selected loaders were uploaded; the device verdict is decided by the caller.");
                logger?.Event("unlock.done",
                    ("success", true),
                    ("message", "All selected loaders were uploaded."),
                    ("elapsedMs", stopwatch.ElapsedMilliseconds));
                var success = new UnlockResult { IsSuccess = true, Message = "All selected loaders were uploaded." };
                success.Stages = reports;
                return success;
            }
            catch (Exception ex)
            {
                logger?.Error("unlock", ex);
                overallProgress.Report($"Error: {ex.Message}");
                var failure = Fail(ClassifyFailure(ex), $"An error occurred: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"unlock failed after {stopwatch.ElapsedMilliseconds} ms");
                return failure;
            }

            void Report(UnlockStageReport report)
            {
                reports.Add(report);
                logger?.Event("stage.end",
                    ("stage", report.Stage),
                    ("file", report.File),
                    ("addr", "0x" + report.Address.ToString("X8")),
                    ("status", report.Status),
                    ("bytes", report.Bytes),
                    ("frames", report.Frames),
                    ("acks", report.Acks),
                    ("durationMs", report.DurationMs),
                    ("note", report.Note));
                options?.StageObserver?.Invoke(report);
            }

            UnlockResult Fail(UnlockFailureKind kind, string message)
            {
                var result = new UnlockResult { IsSuccess = false, Message = message, FailureKind = kind };
                result.Stages = reports;
                logger?.Event("unlock.done",
                    ("success", false),
                    ("failureKind", kind.ToString().ToLowerInvariant()),
                    ("message", message),
                    ("elapsedMs", stopwatch.ElapsedMilliseconds));
                return result;
            }
        }

        /// <summary>true when the stage belongs to the requested split run.</summary>
        private static bool StageSelected(string stageName, UnlockStageSelection stages) => stages switch
        {
            UnlockStageSelection.Null => stageName == "null",
            UnlockStageSelection.Xloader => stageName is "null" or "XLOADER",
            UnlockStageSelection.Rest => stageName != "null",
            _ => true,
        };

        /// <summary>
        /// Opens the port; a <see cref="UnlockOptions.ConnectRetryWindow"/> turns a missing device
        /// into a bounded wait (used by a <c>rest</c> run immediately after the replug).
        /// </summary>
        private static async Task ConnectAsync(VcomFlasher flasher, UnlockOptions options)
        {
            var window = options?.ConnectRetryWindow ?? TimeSpan.Zero;
            if (window <= TimeSpan.Zero)
            {
                flasher.Connect();
                return;
            }

            var deadline = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    flasher.Connect();
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (deadline.Elapsed >= window) throw;
                }
                await Task.Delay(250);
            }
        }

        /// <summary>Records the operator prompt and the answer next to the wire frames.</summary>
        private static Func<string, Task<bool>> WrapInteraction(Func<string, Task<bool>> inner, VcomSessionLogger logger) =>
            async message =>
            {
                logger.Event("prompt", ("text", message));
                bool proceed = await inner(message);
                logger.Event("prompt.answer", ("accepted", proceed));
                return proceed;
            };

        /// <summary>Forwards progress verbatim to the caller's sink and mirrors it into the log.</summary>
        private sealed class VcomProgressTee : IProgress<string>
        {
            private readonly IProgress<string> _inner;
            private readonly VcomSessionLogger _logger;

            public VcomProgressTee(IProgress<string> inner, VcomSessionLogger logger)
            {
                _inner = inner;
                _logger = logger;
            }

            public void Report(string value)
            {
                _logger.Progress(value);
                _inner?.Report(value);
            }
        }

        private static UnlockFailureKind ClassifyFailure(Exception ex) => ex switch
        {
            OperationCanceledException => UnlockFailureKind.Cancelled,
            IOException io when io.Message.Contains("No device in VCOM", StringComparison.OrdinalIgnoreCase)
                               || io.Message.Contains("VCOM port not found", StringComparison.OrdinalIgnoreCase)
                               || io.Message.Contains("did not reappear", StringComparison.OrdinalIgnoreCase)
                               || io.Message.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                               || io.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                => UnlockFailureKind.NoDevice,
            IOException => UnlockFailureKind.DeviceRejected,
            UnauthorizedAccessException => UnlockFailureKind.NoDevice,
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException => UnlockFailureKind.LocalFile,
            _ => UnlockFailureKind.Other,
        };
    }
}