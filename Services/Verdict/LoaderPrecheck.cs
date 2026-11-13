using Kirin_Tool.Security;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Kirin_Tool.Services.Verdict;

/// <summary>One loader stage after pre-flight: the decrypted payload that is about to be sent.</summary>
public sealed record LoaderStagePayload(
    string Stage,
    string File,
    string Path,
    int Address,
    bool Pwn,
    byte[]? Data,
    string? Problem);

/// <summary>
/// Pre-flight for the VCOM loader chain: <b>every</b> selected stage must exist and decrypt
/// <i>before</i> the first frame is written. The shipped behaviour used to mark a missing
/// <c>.ktl</c> as <c>Skipped</c>, continue, and still end with <c>IsSuccess = true</c>
/// (audit F02/S15). A partial loader set cannot boot anything, so it is a hard failure now.
/// </summary>
public sealed record LoaderPrecheckResult(IReadOnlyList<LoaderStagePayload> Stages, IReadOnlyList<string> Problems, string? FirstMissingStage)
{
    public bool Ok => Problems.Count == 0;

    public IReadOnlyList<string> MissingFiles => Stages
        .Where(stage => stage.Data == null)
        .Select(stage => stage.File)
        .ToList();

    public string Describe =>
        Problems.Count == 0
            ? "all selected loaders present and decryptable"
            : string.Join("; ", Problems);
}

public static class LoaderPrecheck
{
    /// <summary>
    /// Reads + DTL-decrypts every selected stage of <paramref name="cpu"/>. Nothing here touches
    /// the device: the result decides whether a single frame may be written at all.
    /// </summary>
    /// <param name="stageSelected">Stage filter (the same one the upload loop uses); null = all stages.</param>
    public static async Task<LoaderPrecheckResult> RunAsync(
        string cpu,
        string loaderDirectory,
        bool useFastFlashLoader = false,
        Func<string, bool>? stageSelected = null)
    {
        var stages = new List<LoaderStagePayload>();
        var problems = new List<string>();
        string? firstMissing = null;

        if (!FirmwareUnlocker.CpuAddresses.TryGetValue(cpu, out var addressTable))
        {
            problems.Add($"unsupported CPU '{cpu}'");
            return new LoaderPrecheckResult(stages, problems, null);
        }

        if (!Directory.Exists(loaderDirectory))
        {
            problems.Add($"loader directory not found: {loaderDirectory}");
            return new LoaderPrecheckResult(stages, problems, null);
        }

        foreach (var loaderInfo in addressTable)
        {
            if (stageSelected != null && !stageSelected(loaderInfo.Name)) continue;

            string fileName = useFastFlashLoader && loaderInfo.Name == "FASTBOOT"
                ? "fastbootf.ktl"
                : loaderInfo.Name.ToLowerInvariant() + ".ktl";
            string filePath = Path.Combine(loaderDirectory, fileName);

            if (!File.Exists(filePath))
            {
                stages.Add(new LoaderStagePayload(loaderInfo.Name, fileName, filePath, loaderInfo.Address, loaderInfo.PwnFlag, null,
                    $"missing loader file: {fileName} ({filePath})"));
                problems.Add($"missing loader file: {fileName} ({filePath})");
                firstMissing ??= loaderInfo.Name;
                continue;
            }

            try
            {
                byte[] raw = await File.ReadAllBytesAsync(filePath);
                byte[] decrypted = CryptoUtil.DTL(raw);
                if (decrypted.Length == 0)
                    throw new InvalidDataException("decrypted payload is empty");
                stages.Add(new LoaderStagePayload(loaderInfo.Name, fileName, filePath, loaderInfo.Address, loaderInfo.PwnFlag, decrypted, null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stages.Add(new LoaderStagePayload(loaderInfo.Name, fileName, filePath, loaderInfo.Address, loaderInfo.PwnFlag, null,
                    $"cannot read/decrypt loader: {ex.Message}"));
                problems.Add($"loader '{fileName}' could not be read/decrypted: {ex.Message}");
                firstMissing ??= loaderInfo.Name;
            }
        }

        if (stages.Count == 0 && problems.Count == 0)
            problems.Add("no loader stage selected for this run");

        return new LoaderPrecheckResult(stages, problems, firstMissing);
    }
}
