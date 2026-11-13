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
using System.IO;
using System.Threading.Tasks;
using Kirin_Tool.Models;
using Kirin_Tool.Services.Diagnostics;

namespace Kirin_Tool.Services
{
    public class OemInfoService
    {
        private readonly FastbootClient _fastbootClient;
        private readonly string _tempOemInfoPath;

        public OemInfoService(FastbootClient fastbootClient)
        {
            _fastbootClient = fastbootClient;
            // Per-instance temp path so concurrent service instances never
            // clobber or delete each other's pulled OEMInfo image.
            _tempOemInfoPath = Path.Combine(Path.GetTempPath(), $"temp_oeminfo_{Guid.NewGuid():N}.img");
        }

        public async Task<OemInfoConversionResult> ConvertOemInfo(
            bool pullFromDevice,
            string inputFilePath,
            string model,
            string vendorCountry,
            IProgress<string> progress = null)
        {
            var result = new OemInfoConversionResult();

            // One op for the conversion (docs/op-scope-map.md row 11); the device pull is its
            // own oeminfo.read op, so "what came off the device" and "what was rewritten" are
            // separate evidence.
            using var op = OpLog.Begin(OpNames.OemInfoConvert, new[]
            {
                pullFromDevice ? "source=device" : "source=file",
                pullFromDevice ? "(device oeminfo)" : (inputFilePath ?? "(none)"),
                "model=" + (model ?? "(none)"),
                "region=" + (vendorCountry ?? "(none)"),
            }, "OEMINFO model/region conversion");
            op.Step("redline: oeminfo is a device-keyed partition; the converted image still needs an explicit "
                + "redline acknowledgement before it is written back");

            try
            {
                string sourceFilePath;
                if (pullFromDevice)
                {
                    progress?.Report("Pulling OEMInfo from device...");
                    ProcessResult pullResult;
                    using (var read = OpLog.Begin(OpNames.OemInfoRead, new[] { "oeminfo", _tempOemInfoPath },
                        "dump oeminfo from the device"))
                    {
                        read.Step("pull target: oeminfo -> " + _tempOemInfoPath);
                        pullResult = await _fastbootClient.PullOemInfo(_tempOemInfoPath);
                        long pulledSize = OpLog.SizeOrUnknown(_tempOemInfoPath);
                        read.Step(OpLog.ResultStep("dump oeminfo", pullResult, pullResult.IsSuccess)
                            + " file=" + OpLog.FileStep(_tempOemInfoPath));
                        if (!pullResult.IsSuccess || !File.Exists(_tempOemInfoPath))
                        {
                            read.Result(false, pullResult.ExitCode, null, pullResult.Output, null,
                                pullResult.IsSuccess ? "file-missing" : OpLog.ExitVerdict(pullResult),
                                "the loader did not hand back an oeminfo image");
                            result.ErrorMessage = "Failed to pull OEMInfo from device. Make sure device is connected in fastboot mode.";
                            op.Result(false, pullResult.ExitCode, null, pullResult.Output, null,
                                pullResult.IsSuccess ? "file-missing" : OpLog.ExitVerdict(pullResult),
                                "no device image to convert; the input file path was not consulted");
                            return result;
                        }
                        read.Result(true, pullResult.ExitCode, null, pullResult.Output, null, "file-written",
                            "pulled " + pulledSize + " byte(s)");
                    }
                    sourceFilePath = _tempOemInfoPath;
                    result.PulledFromDevice = true;
                    op.Step(OpLog.OkPrefix + "pulled oeminfo: " + OpLog.FileStep(sourceFilePath));
                }
                else
                {
                    if (string.IsNullOrEmpty(inputFilePath) || !File.Exists(inputFilePath))
                    {
                        op.Result(false, null, null, null, null, "arg-validation",
                            "input OEMINFO file does not exist: " + (inputFilePath ?? "(none)") + "; nothing was read or written");
                        result.ErrorMessage = "Input OEMInfo file does not exist.";
                        return result;
                    }
                    sourceFilePath = inputFilePath;
                    op.Step("input: " + OpLog.FileStep(sourceFilePath));
                }

                progress?.Report("Converting OEMInfo...");
                var originalData = await File.ReadAllBytesAsync(sourceFilePath);
                op.Step(OpLog.OkPrefix + "read " + originalData.Length + " byte(s) from " + sourceFilePath);
                var editor = new OemInfoEditor(originalData);
                var modifiedData = editor.EditEntries(model, vendorCountry);
                op.Step(OpLog.OkPrefix + "converted: " + originalData.Length + " -> " + modifiedData.Length
                    + " byte(s), model=" + (model ?? "(none)") + ", region=" + (vendorCountry ?? "(none)"));

                result.ConvertedData = modifiedData;
                result.IsSuccess = true;
                progress?.Report("Conversion completed successfully!");

                op.Result(true, null, null, null, null, "conversion:success",
                    "entries rewritten in memory; no device write happened in this op");
                return result;
            }
            catch (Exception ex)
            {
                // Unsupported version / undetectable magic are declared outcomes, not surprises.
                string verdict = ex is NotSupportedException ? "conversion:unsupported"
                    : ex is InvalidDataException ? "conversion:refused"
                    : OpLog.ExceptionVerdict(ex);
                op.Step(OpLog.FailedPrefix + "conversion: " + verdict + ": " + ex.Message);
                op.Result(false, null, null, null, ex.Message, verdict, "no converted image was produced");
                result.ErrorMessage = $"Error during conversion: {ex.Message}";
                return result;
            }
            finally
            {
                // The pulled temp image is fully consumed once conversion ran
                // (or failed); clean it up on every path.
                if (pullFromDevice)
                {
                    CleanupTempFiles();
                }
            }
        }

        public void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(_tempOemInfoPath))
                {
                    File.Delete(_tempOemInfoPath);
                }
            }
            catch
            {
            }
        }
    }
}
