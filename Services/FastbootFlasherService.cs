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
using Kirin_Tool.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Kirin_Tool.Services
{
    public class FastbootFlasherService
    {
        private readonly FastbootClient _fastbootClient;

        public FastbootFlasherService(FastbootClient fastbootClient)
        {
            _fastbootClient = fastbootClient;
        }

        public async Task<List<FastbootPartition>> GetPartitionTableAsync()
        {
            var result = await _fastbootClient.QueryVariableAsync("ptable");
            return FastbootReadResponses.Partitions(result)
                .Select(name => new FastbootPartition(name)).ToList();
        }

        /// <summary>
        /// Dump one partition. When <paramref name="scope"/> is supplied (the pre-op backup does
        /// that) the steps land in the caller's op; otherwise this call opens its own
        /// <c>backup.dump</c> op so a standalone dump is still in the audit trail.
        /// Return values are unchanged: the op only records how the verdict was reached.
        /// </summary>
        public async Task<(bool IsSuccess, string Message)> DumpPartitionAsync(string partitionName, string savePath, OperationScope? scope = null)
        {
            using var own = scope == null
                ? OpLog.Begin(OpNames.BackupDump, new[] { partitionName, savePath }, "standalone partition dump")
                : null;
            OperationScope op = (scope ?? own)!;
            op.Step("dump target: " + partitionName + " -> " + savePath);
            try
            {
                // A stale file from an earlier run would otherwise make the
                // file-exists success check pass without a real dump.
                try { if (File.Exists(savePath)) File.Delete(savePath); }
                catch (Exception ex)
                {
                    op.Step(OpLog.DegradedPrefix + "stale dump file could not be deleted: " + savePath + " (" + ex.Message + ")");
                }

                var result = await _fastbootClient.OemCommandAsync($"dump-emmc {partitionName} \"{savePath}\"", timeoutMinutes: 300);
                bool wroteFile = DumpFileWasWritten(savePath);
                bool outputClean = !string.IsNullOrEmpty(result) &&
                                !result.ToLower().Contains("fail") &&
                                !result.ToLower().Contains("error");
                bool isSuccess = wroteFile && outputClean;
                op.Step("verb dump-emmc: " + (wroteFile ? "file written " + OpLog.FileStep(savePath) : "no file written")
                    + ", output-clean=" + (outputClean ? "yes" : "no"));

                if (isSuccess)
                {
                    op.Result(true, null, null, result, null, "file-written", "dump-emmc wrote a non-empty file");
                    return (true, result ?? "Dump completed");
                }

                var storageResult = await _fastbootClient.OemCommandAsync($"dump-storage {partitionName} \"{savePath}\"", timeoutMinutes: 300);
                bool storageWroteFile = DumpFileWasWritten(savePath);
                bool storageClean = !string.IsNullOrEmpty(storageResult) &&
                                     !storageResult.ToLower().Contains("fail") &&
                                     !storageResult.ToLower().Contains("error");
                bool storageSuccess = storageWroteFile && storageClean;
                op.Step("verb dump-storage fallback: " + (storageWroteFile ? "file written " + OpLog.FileStep(savePath) : "no file written")
                    + ", output-clean=" + (storageClean ? "yes" : "no"));
                if (!storageSuccess)
                {
                    string verdict = storageWroteFile ? "loader-capability:dump-verb" : "file-missing";
                    if (!string.IsNullOrEmpty(storageResult) &&
                        (storageResult.ToLower().Contains("invalid command") || storageResult.ToLower().Contains("not support")))
                        verdict = "loader-capability:invalid-command";
                    op.Result(false, null, null, null, storageResult, verdict,
                        "neither dump-emmc nor dump-storage produced a usable file; recorded as a loader capability gap");
                    return (false, string.IsNullOrEmpty(storageResult) || storageResult.ToLower().Contains("fail") || storageResult.ToLower().Contains("error")
                        ? $"Dump failed. The Huawei fastboot client normally writes '{savePath}' on the host; the system fastboot cannot save uploaded partition data."
                        : storageResult);
                }
                op.Result(true, null, null, storageResult, null, "file-written", "dump-storage wrote a non-empty file");
                return (true, storageResult ?? "Dump completed");
            }
            catch (TimeoutException ex)
            {
                op.Result(false, null, null, null, ex.Message, "timeout", "dump did not finish inside the client timeout");
                return (false, "Dump operation timed out - partition may be too large");
            }
            catch (Exception ex)
            {
                op.Result(false, null, null, null, ex.Message, OpLog.ExceptionVerdict(ex));
                return (false, $"Dump failed: {ex.Message}");
            }
        }

        // Stock fastboot passes "oem dump-*" through without saving the uploaded data,
        // so a "successful" response can still leave no file behind.
        private static bool DumpFileWasWritten(string savePath)
        {
            try
            {
                var info = new FileInfo(savePath);
                return info.Exists && info.Length > 0;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        /// Write one image. With an enclosing plan scope the per-image line becomes a
        /// <c>step</c> of that plan (CLI flash-xml); without one this call is its own
        /// <c>flash.image</c> op (the GUI loops call the flasher image by image).
        /// The verdict rule is the flasher's existing one: <see cref="ProcessResult.IsSuccess"/>.
        /// </summary>
        public async Task<(bool IsSuccess, string Message)> FlashPartitionAsync(string partitionName, string imagePath, OperationScope? scope = null)
        {
            using var own = scope == null
                ? OpLog.Begin(OpNames.FlashImage, OpLog.FlashTool, new[] { partitionName, imagePath }, "single image write")
                : null;
            OperationScope op = (scope ?? own)!;
            op.Step("flash " + partitionName + " <- " + OpLog.FileStep(imagePath)
                + (FlashPolicy.IsRedline(partitionName) ? " [redline partition]" : ""));
            try
            {
                var result = await _fastbootClient.FlashPartition(partitionName, imagePath);
                op.Step(OpLog.ResultStep(partitionName, result, result.IsSuccess));
                if (own != null)
                    op.Result(result.IsSuccess, result.ExitCode, null, result.Output, null,
                        OpLog.ExitVerdict(result), "verdict rule: fastboot exit code (GUI per-image path)");

                return (result.IsSuccess, result.Output);
            }
            catch (Exception ex)
            {
                // Surface the real failure to the caller; swallowing it made every
                // exception look like a generic "Failed" with no diagnostic value.
                op.Step(OpLog.FailedPrefix + partitionName + ": " + OpLog.ExceptionVerdict(ex) + ": " + ex.Message);
                if (own != null)
                    op.Result(false, null, null, null, ex.Message, OpLog.ExceptionVerdict(ex));
                return (false, $"Flash failed: {ex.Message}");
            }
        }

        public void GenerateFlashingXml(List<FastbootPartition> partitions, string outputPath)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "gb2312", "yes"),
                new XElement("configurations",
                    new XElement("configuration",
                        new XElement("fastbootimage",
                            partitions.Select(p =>
                                new XElement("image",
                                    new XAttribute("name", p.Name.ToUpperInvariant()),
                                    new XAttribute("identifier", p.Identifier),
                                    $"fastbootimage/{p.ImageFileName}")
                            )
                        )
                    )
                )
            );

            doc.Save(outputPath);
        }

        public List<FastbootPartition> ParseFlashingXml(string xmlPath)
        {
            var partitions = new List<FastbootPartition>();

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var settings = new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                IgnoreComments = true
            };

            XDocument doc;
            using (var reader = XmlReader.Create(xmlPath, settings))
            {
                doc = XDocument.Load(reader);
            }

            var fastbootImageSection = doc.Descendants("fastbootimage").FirstOrDefault();
            if (fastbootImageSection == null)
                return partitions;

            var images = fastbootImageSection.Elements("image");

            foreach (var image in images)
            {
                var name = image.Attribute("name")?.Value;
                var identifier = image.Attribute("identifier")?.Value;
                var fileName = image.Value?.Trim().Replace('\\', '/');

                if (string.IsNullOrEmpty(fileName))
                    continue;

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(identifier))
                {
                    if (identifier.Equals("huawei_crc_check", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var partition = new FastbootPartition(identifier)
                    {
                        Name = name,
                        IsSelected = true
                    };

                    // Path.Combine handles forward slashes correctly on both Windows and Linux in modern .NET

                    var xmlDirectory = Path.GetDirectoryName(xmlPath) ?? ".";
                    var parentDirectory = Path.GetDirectoryName(xmlDirectory);

                    if (Path.IsPathRooted(fileName))
                    {
                        partition.DumpPath = fileName;
                    }
                    else
                    {
                        var candidatePath = Path.Combine(xmlDirectory, fileName);

                        if (!File.Exists(candidatePath) && !string.IsNullOrEmpty(parentDirectory))
                        {
                            var parentCandidate = Path.Combine(parentDirectory, fileName);
                            if (File.Exists(parentCandidate))
                            {
                                candidatePath = parentCandidate;
                            }
                        }

                        partition.DumpPath = candidatePath;
                    }

                    partitions.Add(partition);
                }
            }

            return partitions;
        }

    }
}
