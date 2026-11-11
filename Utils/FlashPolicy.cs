// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Kirin_Tool.Utils;

/// <summary>
/// Single source of truth for the two write policies that used to be duplicated in
/// the GUI (MainWindow.IsSkipPartition / IsRedlinePartition) and in the CLI
/// (Cli/Commands/OtaFlashRunner.FlashPolicy).
///
///  - <see cref="ShouldSkip"/>: metadata entries of an UPDATE.APP that are never
///    flashed (checksums, version lists, package descriptors).
///  - <see cref="IsRedline"/>: device-keyed partitions (identity, baseband
///    calibration, key/DRM material). Writing one of them is unrecoverable without
///    a verified backup, so every writer has to gate on this explicitly.
///
/// The redline set is enforced below the UI as well (USBUpdateFlasherService), because
/// the USB-update path only learns its partition names after unpacking the package.
/// </summary>
public static class FlashPolicy
{
    /// <summary>Entries of the package descriptor family; never written to the device.</summary>
    private static readonly string[] SkipContains =
    {
        "sha256rsa", "crc", "curver", "verlist", "package_type",
        "base_verlist", "base_ver", "ptable_cust", "cust_verlist",
        "cust_ver", "preload_verlist", "preload_ver", "ptable_preload",
    };

    /// <summary>Substrings that mark device-keyed data (i.e. "modemnvm_1" is a hit).</summary>
    private static readonly string[] RedlineContains =
    {
        "oeminfo", "nvme", "modem_secure", "persist", "modemnvm",
        "certification", "hisee_encos", "veritykey",
    };

    /// <summary>
    /// Exact names of the baseband calibration set. Matched by equality (OrdinalIgnoreCase)
    /// on purpose: as substrings they are far too broad ("nvme" is not "nvm").
    /// </summary>
    private static readonly string[] RedlineExactNames =
    {
        "nvm", "modemst1", "modemst2", "fsg",
    };

    /// <summary>
    /// True when the package entry is descriptor metadata. Blank names are skipped too,
    /// so a malformed archive entry never reaches the writer.
    /// </summary>
    public static bool ShouldSkip(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName)) return true;
        string lower = partitionName.ToLowerInvariant();
        return SkipContains.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>True when writing this partition touches device-keyed (redline) data.</summary>
    public static bool IsRedline(string partitionName)
    {
        if (string.IsNullOrWhiteSpace(partitionName)) return false;

        string trimmed = partitionName.Trim();
        if (RedlineExactNames.Any(name => trimmed.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return true;

        string lower = trimmed.ToLowerInvariant();
        return RedlineContains.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>
    /// Redline members of <paramref name="partitionNames"/>, de-duplicated
    /// (case-insensitive) in first-seen order, with blank entries ignored.
    /// </summary>
    public static IReadOnlyList<string> RedlineHits(IEnumerable<string> partitionNames)
    {
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in partitionNames ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string trimmed = name.Trim();
            if (!IsRedline(trimmed)) continue;
            if (seen.Add(trimmed)) hits.Add(trimmed);
        }
        return hits;
    }
}
