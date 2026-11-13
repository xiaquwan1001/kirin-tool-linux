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
using System.IO.Ports;
using Kirin_Tool.Services.USBUpdate.Transport;
using System.Linq;
using System.Management;
using System.Threading;
using Kirin_Tool.Models;
using Kirin_Tool.Utils;

namespace Kirin_Tool.Services.USBUpdate
{
    public class UsbDownloadService
    {
        private readonly Action<string> _log;
        private readonly string _dloadDirectory;
        private readonly Func<IUpdateTransport?>? _connectionFactory;

        public string? LastError { get; private set; }

        /// <summary>The verdict of the most recent transfer (null before the first one).</summary>
        public UsbUpdateVerdict? LastVerdict { get; private set; }
        private static string FailureReason(Exception error) => error.InnerException == null
            ? error.Message : $"{error.Message} ({error.GetBaseException().Message})";
        private bool RecordFailure(string details)
        {
            LastError = details;
            _log("FAILED: " + details);
            return false;
        }

        public Func<string, string, bool>? OnRetryRequired;
        public Action<int>? OnProgressUpdate;
        public Action<int, string>? OnPartitionStarted;
        public Action<int, string, int>? OnPartitionProgressUpdate;
        public Action<int, string, bool, string>? OnPartitionCompleted;

        public UsbDownloadService(Action<string> log, string dloadDirectory)
            : this(log, dloadDirectory, null)
        {
        }

        /// <summary>
        /// Same channel, but the transport comes from <paramref name="connectionFactory"/> instead of
        /// <see cref="BuildConnection"/>. Nothing else changes: same commands, same waits, same 3
        /// handshake attempts. The seam exists so the verdict rules of D-04 can be exercised without
        /// a device; the shipped callers keep using the two-argument constructor.
        /// </summary>
        public UsbDownloadService(Action<string> log, string dloadDirectory, Func<IUpdateTransport?>? connectionFactory)
        {
            _log = log;
            _dloadDirectory = dloadDirectory;
            _connectionFactory = connectionFactory;
        }

        /// <summary>
        /// Where one partition's image bytes come from (default: a read-only <see cref="FileStream"/>).
        /// A seam for the same reason as the connection factory: an image stream that ends before its
        /// declared size is a real degradation and must be testable without a device.
        /// </summary>
        public Func<string, Stream>? OpenImageStream { get; set; }

        /// <summary>
        /// Legacy boolean view of the channel: true only for a <see cref="UsbUpdateOutcome.Verified"/>
        /// transfer. Callers that must tell "partial" from "failed" use
        /// <see cref="FlashImagesWithVerdict"/> and read <see cref="LastVerdict"/>.
        /// </summary>
        public bool FlashImages() => FlashImagesWithVerdict().Verified;

        /// <summary>
        /// Run the USB-update transfer and report what the device actually established (D-04).
        /// </summary>
        /// <param name="requiredPartitions">
        /// The set that must all be written for a `verified` verdict. Null/empty means "the plan
        /// decides": every entry list.txt selects (flag == 1) is required.
        /// </param>
        /// <param name="onEvent">Optional structured sink for every degraded/skipped/missing/written fact.</param>
        public UsbUpdateVerdict FlashImagesWithVerdict(IEnumerable<string>? requiredPartitions = null,
            Action<UsbUpdateEvent>? onEvent = null)
        {
            LastError = null;
            _log("Opening USB Update connection.");
            var verdictBuilder = new UsbUpdateVerdictBuilder(requiredPartitions, _log, onEvent);
            IUpdateTransport port = null;
            
            while (port == null)
            {
                port = BuildConnection();
                if (port == null)
                {
                    if (OnRetryRequired == null)
                    {
                        throw new OperationCanceledException("USB Update aborted: no HiSilicon device found and no retry handler registered.");
                    }

                    var result = OnRetryRequired.Invoke("Device Not Found",
                        "No HiSilicon device found in USB Update Mode.\n\nConnect the device and click Done! to retry, or Cancel to abort.");

                    if (result == false)
                    {
                        throw new OperationCanceledException("USB Update cancelled by user.");
                    }
                }
            }

            bool success = true;

            try
            {
                _log("USB Update connection opened; starting handshake.");
                if (DoHandshake(port))
                {
                    verdictBuilder.HandshakeEstablished();
                }
                else
                {
                    verdictBuilder.HandshakeMissing("3 handshake attempts got no expected answer (7E 26 00 00 25 A7)");
                }
                
                _log("Handshake routine returned; sending package authorization if present.");
                if (!SendUnlockCommand(port))
                {
                    verdictBuilder.AuthorizationMissing("package authorization (unlockcode) was sent but not acknowledged");
                }
                
                success = FlashPartitions(port, verdictBuilder);

                if (success)
                {
                    if (!SendCommand(port, CreateRebootCommand(), 0.3))
                    {
                        verdictBuilder.NoteDegraded("reboot command (0x0A) was not acknowledged by the device");
                    }
                    Thread.Sleep(50);
                    if (!SendCommand(port, CreateForceRebootCommand(), 0.3))
                    {
                        verdictBuilder.NoteDegraded("force-reboot command (0x32) was not acknowledged by the device");
                    }
                }
                else
                {
                    // Was an empty else (D-04): after a hard stop the reboot commands are deliberately
                    // NOT sent, and that decision is an explicit event instead of a silent branch.
                    verdictBuilder.NoteDegraded("reboot commands were not sent: the transfer stopped before every selected partition was written");
                }
            }
            catch (Exception ex)
            {
                RecordFailure(FailureReason(ex));
                _log(ex.ToString());
                throw;
            }
            finally
            {
                port?.Close();
                port?.Dispose();
            }

            UsbUpdateVerdict outcome = verdictBuilder.Build();
            LastVerdict = outcome;
            _log(outcome.Describe());
            if (outcome.Outcome == UsbUpdateOutcome.Failed && LastError == null)
            {
                // A failed transfer always carries a reason, even when nothing threw.
                LastError = outcome.Reason;
            }
            return outcome;
        }

        private IUpdateTransport BuildConnection()
        {
            SerialPort? serial = null;
            try
            {
                if (_connectionFactory != null) return _connectionFactory();
                if (OperatingSystem.IsLinux()) return BufferedUsbTransport.Open();
                string portName = FindHiSiliconUsbUpdatePort();
                if (portName == null) return null;
                serial = new SerialPort(portName, 9600)
                {
                    ReadTimeout = 5000, WriteTimeout = 5000,
                    WriteBufferSize = 8 * 1024 * 1024, ReadBufferSize = 1024 * 1024
                };
                serial.Open();
                return new SerialUpdateTransport(serial);
            }
            catch (Exception ex)
            {
                serial?.Dispose();
                _log("USB Update connection: " + ex.Message);
                return null;
            }
        }

        private static string FindHiSiliconUsbUpdatePort()
        {
            if (OperatingSystem.IsLinux())
            {
                return LinuxSerialPortFinder.FindPort(
                    vendorId: 0x12D1,
                    descriptorMatches: descriptor =>
                        descriptor.Contains("DBAdapter", StringComparison.OrdinalIgnoreCase) ||
                        descriptor.Contains("Reserved Interface", StringComparison.OrdinalIgnoreCase),
                    interfaceMatches: LinuxSerialPortFinder.IsHuaweiUsbUpdateInterface);
            }

            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, DeviceID FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

                foreach (ManagementObject device in searcher.Get())
                {
                    string caption = device["Caption"]?.ToString() ?? string.Empty;
                    string deviceId = device["DeviceID"]?.ToString() ?? string.Empty;
                    if (!deviceId.Contains("VID_12D1", StringComparison.OrdinalIgnoreCase) ||
                        !caption.Contains("DBAdapter Reserved Interface", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int start = caption.IndexOf("(COM", StringComparison.Ordinal);
                    int end = caption.IndexOf(')', start);
                    if (start >= 0 && end > start)
                    {
                        return caption.Substring(start + 1, end - start - 1);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Send the handshake and report whether the expected answer was seen. Was: `void`, so a
        /// silent device left the caller unable to say anything about the link (D-04). The command
        /// bytes, the 3 attempts and the 50/200 ms waits are unchanged.
        /// </summary>
        private bool DoHandshake(IUpdateTransport port)
        {
            const int maxRetries = 3;
            byte[] expectedPrefix = new byte[] { 0x7E, 0x26, 0x00, 0x00, 0x25, 0xA7 };

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                byte[] cmd = CreateHandshakeCommand();
                port.Write(cmd, 0, cmd.Length);
                Thread.Sleep(50);

                byte[] response = ReadResponse(port);
                _log($"Handshake attempt {attempt}: {response.Length} response bytes; {Convert.ToHexString(response.AsSpan(0, Math.Min(64, response.Length)))}");
                if (ContainsSequence(response, expectedPrefix))
                {
                    _log("Expected handshake response received.");
                    return true;
                }

                if (attempt < maxRetries)
                {
                    Thread.Sleep(200);
                }
            }
            _log("Expected handshake response not observed; the link is unconfirmed, so the verdict can be at most partial.");
            return false;
        }

        /// <summary>
        /// Send the package-authorization command when the package carried an unlockcode. Returns
        /// false when the command was sent but not acknowledged (was: an empty if-body, D-04).
        /// </summary>
        private bool SendUnlockCommand(IUpdateTransport port)
        {
            string unlockPath = Path.Combine(_dloadDirectory, "unlockcode");
            if (!File.Exists(unlockPath))
            {
                _log("No unlockcode in the transfer directory; package authorization is not applicable.");
                return true;
            }

            byte[] unlockCode = File.ReadAllBytes(unlockPath);

            List<byte> cmd = new List<byte> { 0x0B };
            cmd.AddRange(unlockCode);

            byte[] crc = Crc16X25.CalculateBytes(cmd.ToArray());
            cmd.AddRange(crc);

            byte[] converted = ConvertData(cmd.ToArray());

            List<byte> finalCmd = new List<byte> { 0x7E };
            finalCmd.AddRange(converted);
            finalCmd.Add(0x7E);

            return SendCommand(port, finalCmd.ToArray(), 0.1);
        }

        /// <summary>
        /// Send every partition the plan selects. Returns false only when the transfer stopped at a
        /// partition; every skip / missing image / cut-off stream is recorded on
        /// <paramref name="verdict"/> instead of being silently continued (D-04).
        /// </summary>
        private bool FlashPartitions(IUpdateTransport port, UsbUpdateVerdictBuilder verdict)
        {
            string listPath = Path.Combine(_dloadDirectory, "list.txt");
            if (!File.Exists(listPath))
            {
                verdict.NoteFatal("transfer preparation: list.txt was not found");
                return RecordFailure("Transfer preparation: list.txt was not found.");
            }

            var lines = File.ReadAllLines(listPath);
            
            Dictionary<int, string> partitionSourceMap = new Dictionary<int, string>();
            string mappingPath = Path.Combine(_dloadDirectory, "partition_mapping.txt");
            if (File.Exists(mappingPath))
            {
                var mappingLines = File.ReadAllLines(mappingPath);
                for (int i = 0; i < mappingLines.Length && i < lines.Length; i++)
                {
                    var mappingParts = mappingLines[i].Split('|');
                    if (mappingParts.Length >= 2)
                    {
                        partitionSourceMap[i] = mappingParts[1];
                    }
                }
            }

            int lineIndex = 0;
            foreach (var line in lines)
            {
                var parts = line.Split(' ');
                if (parts.Length < 2)
                {
                    // Was a silent continue: a malformed plan line changes nothing, but it must not
                    // disappear. lineIndex is deliberately not advanced (plan mapping unchanged).
                    verdict.NoteDegraded("list.txt line " + (lineIndex + 1) + " is malformed; the line was ignored");
                    continue;
                }

                string name = parts[0];
                int flag = 0;
                int.TryParse(parts[1], out flag);

                if (flag != 1)
                {
                    // Declared but not selected: not required, never silent.
                    verdict.NoteSkipped(name, "not selected for transfer (list.txt flag=" + flag + ")");
                    lineIndex++;
                    continue;
                }

                verdict.DeclareRequired(name);

                if (partitionSourceMap.ContainsKey(lineIndex))
                {
                    string sourceDir = partitionSourceMap[lineIndex];
                    string sourceImg = Path.Combine(sourceDir, $"{name}.img");
                    string sourceHeader = Path.Combine(sourceDir, $"{name}.img.header");
                    string destImg = Path.Combine(_dloadDirectory, $"{name}.img");
                    string destHeader = Path.Combine(_dloadDirectory, $"{name}.img.header");
                    
                    if (File.Exists(sourceImg))
                    {
                        long sourceSize = new FileInfo(sourceImg).Length;
                        if (sourceSize == 0)
                        {
                            verdict.NoteMissing(name, "mapped source image is 0 bytes: " + sourceImg);
                            lineIndex++;
                            continue;
                        }
                        File.Copy(sourceImg, destImg, true);
                    }
                    if (File.Exists(sourceHeader))
                    {
                        File.Copy(sourceHeader, destHeader, true);
                    }
                }

                string imgPath = Path.Combine(_dloadDirectory, $"{name}.img");
                string headerPath = Path.Combine(_dloadDirectory, $"{name}.img.header");
                
                if (!File.Exists(imgPath))
                {
                    verdict.NoteSkipped(name, "image file is missing: " + imgPath);
                    lineIndex++;
                    continue;
                }
                
                if (!File.Exists(headerPath))
                {
                    verdict.NoteSkipped(name, "header file is missing: " + headerPath);
                    lineIndex++;
                    continue;
                }
                
                long fileSize = new FileInfo(imgPath).Length;
                if (fileSize == 0)
                {
                    verdict.NoteMissing(name, "image file is 0 bytes: " + imgPath);
                    lineIndex++;
                    continue;
                }

                OnPartitionStarted?.Invoke(lineIndex, name);
                OnPartitionProgressUpdate?.Invoke(lineIndex, name, 0);

                double fileSizeMB = fileSize / 1024.0 / 1024.0;
                double tailTimeout = Math.Max(35.0, Math.Min(180.0, 15.0 + (fileSizeMB / 10.0)));
                
                _log($"Partition {name}: sending header; image size {fileSize} bytes.");
                byte[] header = ReadHeader(name);
                try
                {
                    string? headError = SendCommandInternal(port, CreateHeadCommand(header), 2.0);
                    if (headError != null) throw new IOException(headError);
                }
                catch (Exception ex)
                {
                    string details = $"Partition {name}, header: {FailureReason(ex)}";
                    // Was: throw out of the transfer (no verdict was ever produced). The transfer
                    // now stops here like the data/tail stage does, so the caller always gets a
                    // verdict whose reason names this partition and stage (D-04).
                    verdict.NoteTransferStopped(name, details);
                    OnPartitionCompleted?.Invoke(lineIndex, name, false, details);
                    _log(ex.ToString());
                    return RecordFailure(details);
                }

                int blockSize = 0x20000;
                string stage = "data transfer";
                bool streamCutShort = false;
                long sentBytes = 0;
                try
                {
                    _log($"Partition {name}: sending data.");
                    string? imageError = SendImage(port, lineIndex, name, blockSize, out sentBytes, out streamCutShort);
                    if (imageError != null)
                    {
                        if (streamCutShort)
                        {
                            // Was: `break` + success. The cut-off stream is a missing partition now.
                            verdict.NoteStreamEndedEarly(name, sentBytes, fileSize);
                        }
                        throw new IOException(imageError);
                    }
                    stage = "finalization";
                    _log($"Partition {name}: finalizing; response timeout {tailTimeout:F1}s.");
                    string? tailError = SendCommandInternal(port, CreateTailCommand(header), tailTimeout);
                    if (tailError != null)
                    {
                        throw new IOException(tailError);
                    }
                    verdict.NoteWritten(name);
                    OnPartitionCompleted?.Invoke(lineIndex, name, true, "Success");
                    _log($"Partition {name}: completed successfully.");
                }
                catch (Exception ex)
                {
                    string details = $"Partition {name}, {stage}: {FailureReason(ex)}";
                    if (!streamCutShort)
                    {
                        verdict.NoteTransferStopped(name, details);
                    }
                    OnPartitionCompleted?.Invoke(lineIndex, name, false, details);
                    _log(ex.ToString());
                    return RecordFailure(details);
                }
                
                lineIndex++;
            }
            return true;
        }

        /// <summary>
        /// Push one image. A stream that ends before its declared size is an ERROR now (was: `break`
        /// followed by `return null`, i.e. success); the caller turns it into
        /// `NoteStreamEndedEarly`. The block size, compression, timeout and command bytes are
        /// unchanged.
        /// </summary>
        private string? SendImage(IUpdateTransport port, int lineIndex, string name, int blockSize,
            out long sentBytes, out bool streamCutShort)
        {
            sentBytes = 0;
            streamCutShort = false;
            string imgPath = Path.Combine(_dloadDirectory, $"{name}.img");
            long fileSize = new FileInfo(imgPath).Length;
            uint addr = 0;

            byte[] header = ReadHeader(name);
            byte[] fileSeq = new byte[4];
            if (header.Length >= 24)
            {
                Array.Copy(header, 20, fileSeq, 0, 4);
                Array.Reverse(fileSeq);
            }

            Func<string, Stream> openStream = OpenImageStream
                ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read));
            long totalSent = 0;
            using (Stream fs = openStream(imgPath))
            {
                long remaining = fileSize;
                
                byte[] readBuffer = new byte[blockSize];
                int lastReportedProgress = -1;

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(blockSize, remaining);
                    int bytesRead = fs.Read(readBuffer, 0, toRead);
                    if (bytesRead == 0)
                    {
                        // Was: `break` and then a null (= no error) return, so a truncated image
                        // counted as a written partition (D-04).
                        streamCutShort = true;
                        sentBytes = totalSent;
                        return "Stream ended early: " + totalSent + " of " + fileSize
                            + " bytes could be read; the remainder was never sent.";
                    }

                    byte[] compressed = Compression.ZlibCompress(readBuffer, 0, bytesRead);
                    byte[] dataCmd = CreateDataCommand(compressed, bytesRead, fileSeq, addr);

                    double compressedSizeMB = compressed.Length / 1024.0 / 1024.0;
                    double timeout = Math.Max(1.0, Math.Min(8.0, compressedSizeMB * 1.5));
                    
                    string? dataError;
                    try { dataError = SendCommandInternal(port, dataCmd, timeout); }
                    catch (Exception ex)
                    {
                        throw new IOException($"Data block offset 0x{addr:X}: {FailureReason(ex)}", ex);
                    }
                    if (dataError != null)
                    {
                        return $"Data block offset 0x{addr:X}: {dataError}";
                    }

                    addr += (uint)toRead;
                    totalSent += toRead;
                    remaining -= toRead;

                    if (fileSize > 0)
                    {
                        int progress = (int)((totalSent * 100) / fileSize);
                        if (progress != lastReportedProgress)
                        {
                            lastReportedProgress = progress;
                            OnProgressUpdate?.Invoke(progress);
                            OnPartitionProgressUpdate?.Invoke(lineIndex, name, progress);
                        }
                    }
                }
            }
            sentBytes = totalSent;
            return null;
        }

        private byte[] ReadHeader(string name)
        {
            string headerPath = Path.Combine(_dloadDirectory, $"{name}.img.header");
            byte[] header = File.ReadAllBytes(headerPath);
            
            if (header.Length > 93)
            {
                header[92] = 0;
                header[93] = 0;
            }
            
            byte[] result = new byte[header.Length + 1];
            Array.Copy(header, result, header.Length);
            result[header.Length] = 0x00;
            
            return result;
        }

        private bool SendCommand(IUpdateTransport port, byte[] cmd, double timeout)
        {
            string? error = SendCommandInternal(port, cmd, timeout);
            if (error != null)
            {
                _log("Command response failure: " + error);
                return false;
            }
            return true;
        }

        private string? SendCommandInternal(IUpdateTransport port, byte[] cmd, double timeout)
        {
            int timeoutMs = (int)(timeout * 1000);

            int offset = 0;
            port.DiscardInBuffer();
            while (offset < cmd.Length)
            {
                int toSend = Math.Min(0x10000, cmd.Length - offset);
                port.Write(cmd, offset, toSend);
                offset += toSend;
            }

            List<byte> responseList = new List<byte>();
            int originalTimeout = port.ReadTimeout;
            port.ReadTimeout = Math.Max(1, timeoutMs);
            
            try
            {
                while (true)
                {
                    int val = port.ReadByte();
                    if (val == -1) break;
                    
                    if (responseList.Count == 0 && val != 0x7E) continue;
                    
                    responseList.Add((byte)val);
                    if (responseList.Count >= 2 && responseList[0] == 0x7E && val == 0x7E)
                    {
                        break;
                    }
                }
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                port.ReadTimeout = originalTimeout;
            }
            
            byte[] response = responseList.ToArray();

            byte[] expectedResponse = new byte[] { 0x7E, 0x02, 0x6A, 0xD3, 0x7E };
            
            if (response.SequenceEqual(expectedResponse))
            {
            }
            else if (response.Length >= expectedResponse.Length && ContainsSequence(response, expectedResponse))
            {
            }
            else
            {
                if (response.Length > 0)
                {
                    string responseHex = string.Join(" ", response.Select(b => b.ToString("X2")));
                    if (response.Length >= 2 && response[0] == 0x7E && response[1] == 0x03)
                    {
                        return $"Device error (0x03). Response: {responseHex}";
                    }
                    return $"Unexpected response: {responseHex}";
                }
                else
                {
                    return $"No response within {timeoutMs}ms";
                }
            }

            Thread.Sleep(10);
            return null;
        }

        private byte[] ReadResponse(IUpdateTransport port)
        {
            int bytesToRead = port.BytesToRead;
            if (bytesToRead > 0)
            {
                byte[] buffer = new byte[bytesToRead];
                port.Read(buffer, 0, bytesToRead);
                return buffer;
            }
            return new byte[0];
        }

        private bool ContainsSequence(byte[] source, byte[] pattern)
        {
            if (source == null || pattern == null || source.Length < pattern.Length)
                return false;

            for (int i = 0; i <= source.Length - pattern.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (source[i + j] != pattern[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                    return true;
            }
            return false;
        }

        private byte[] ConvertData(byte[] data)
        {
            byte[] result = new byte[data.Length * 2];
            int idx = 0;
            
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == 0x7E)
                {
                    result[idx++] = 0x7D;
                    result[idx++] = 0x5E;
                }
                else if (b == 0x7D)
                {
                    result[idx++] = 0x7D;
                    result[idx++] = 0x5D;
                }
                else
                {
                    result[idx++] = b;
                }
            }

            byte[] finalResult = new byte[idx];
            Array.Copy(result, finalResult, idx);
            return finalResult;
        }

        private byte[] CreateHandshakeCommand()
        {
            byte[] cmd = new byte[] { 0x26, 0x00, 0x00, 0x25, 0xA7, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 };

            List<byte> payload = new List<byte>(cmd);
            payload.AddRange(Crc16X25.CalculateBytes(cmd));

            List<byte> result = new List<byte>(ConvertData(payload.ToArray()));
            result.Add(0x7E);

            return result.ToArray();
        }

        private byte[] CreateHeadCommand(byte[] headerData)
        {
            List<byte> cmd = new List<byte> { 0x41 };
            cmd.AddRange(headerData);

            byte[] crc = Crc16X25.CalculateBytes(cmd.ToArray());
            cmd.AddRange(crc);

            byte[] converted = ConvertData(cmd.ToArray());

            List<byte> result = new List<byte> { 0x7E };
            result.AddRange(converted);
            result.Add(0x7E);

            return result.ToArray();
        }

        private byte[] CreateTailCommand(byte[] headerData)
        {
            List<byte> cmd = new List<byte> { 0x43 };
            cmd.AddRange(headerData);

            byte[] crc = Crc16X25.CalculateBytes(cmd.ToArray());
            cmd.AddRange(crc);

            byte[] converted = ConvertData(cmd.ToArray());

            List<byte> result = new List<byte> { 0x7E };
            result.AddRange(converted);
            result.Add(0x7E);

            return result.ToArray();
        }

        private byte[] CreateDataCommand(byte[] compressedData, int originalLength, byte[] fileSeq, uint addr)
        {
            List<byte> cmd = new List<byte> { 0x0F };

            uint fileSeqInt = (uint)((fileSeq[0] << 24) | (fileSeq[1] << 16) | (fileSeq[2] << 8) | fileSeq[3]);
            uint combined = fileSeqInt + addr;
            byte[] combinedBytes = new byte[4];
            combinedBytes[0] = (byte)(combined >> 24);
            combinedBytes[1] = (byte)(combined >> 16);
            combinedBytes[2] = (byte)(combined >> 8);
            combinedBytes[3] = (byte)combined;
            cmd.AddRange(combinedBytes);

            byte[] lenBytes = new byte[4];
            lenBytes[0] = (byte)(originalLength >> 24);
            lenBytes[1] = (byte)(originalLength >> 16);
            lenBytes[2] = (byte)(originalLength >> 8);
            lenBytes[3] = (byte)originalLength;
            cmd.AddRange(lenBytes);

            cmd.AddRange(compressedData);

            byte[] crc = Crc16X25.CalculateBytes(cmd.ToArray());
            cmd.AddRange(crc);

            byte[] converted = ConvertData(cmd.ToArray());

            List<byte> result = new List<byte> { 0x7E };
            result.AddRange(converted);
            result.Add(0x7E);

            return result.ToArray();
        }

        private byte[] CreateSingleByteCommand(byte opcode)
        {
            List<byte> payload = new List<byte> { opcode };
            payload.AddRange(Crc16X25.CalculateBytes(new[] { opcode }));

            List<byte> result = new List<byte> { 0x7E };
            result.AddRange(ConvertData(payload.ToArray()));
            result.Add(0x7E);

            return result.ToArray();
        }

        private byte[] CreateRebootCommand()
        {
            return CreateSingleByteCommand(0x0A);
        }

        private byte[] CreateForceRebootCommand()
        {
            return CreateSingleByteCommand(0x32);
        }

        public void SendRebootCommands()
        {

            IUpdateTransport port = null;
            
            while (port == null)
            {
                port = BuildConnection();
                if (port == null)
                {
                    if (OnRetryRequired == null)
                    {
                        _log("SendRebootCommands: device not found and no retry handler registered; aborting.");
                        return;
                    }

                    var result = OnRetryRequired.Invoke("Device Not Found",
                        "No HiSilicon device found in USB Update Mode.\n\nConnect the device and click Done! to retry, or Cancel to abort.");

                    if (result == false)
                    {
                        return;
                    }
                }
            }

            try
            {
                SendCommand(port, CreateRebootCommand(), 0.3);
                Thread.Sleep(50);
                SendCommand(port, CreateForceRebootCommand(), 0.3);
            }
            finally
            {
                port?.Close();
                port?.Dispose();
            }
        }
    }
}
