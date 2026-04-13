// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Services;

/// <summary>
/// Provides MDF/MDS-specific disc image verification methods.
/// Validates the MDS descriptor, MDF data file, and track layout integrity.
/// </summary>
public static class MdfMdsVerifier
{
    /// <summary>
    /// Validates an MDF/MDS disc image pair for structural correctness.
    /// Checks MDS signature, header fields, session/track descriptors,
    /// and MDF data file bounds.
    /// </summary>
    /// <param name="imagePath">Path to the .mds or .mdf file.</param>
    /// <returns>A list of validation messages. Empty means no issues found.</returns>
    public static List<string> ValidateMdfMdsImage(string imagePath)
    {
        var messages = new List<string>();

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            messages.Add($"[Error] Image file not found: {imagePath}");
            return messages;
        }

        try
        {
            // Resolve MDS path (user may pass .mdf or .mds)
            string ext = Path.GetExtension(imagePath);
            string mdsPath = ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(imagePath, ".mds")
                : imagePath;
            string mdfPath = ext.Equals(".mds", StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(imagePath, ".mdf")
                : imagePath;

            // Check MDS file
            if (!File.Exists(mdsPath))
            {
                messages.Add($"[Warning] MDS descriptor file not found: {Path.GetFileName(mdsPath)}");
                messages.Add("[Info] MDF data file may still be usable as a raw image.");
                return messages;
            }

            messages.Add($"[Info] MDS descriptor: {Path.GetFileName(mdsPath)}");

            // Parse MDS
            var image = Native.Iso.MdfParser.Parse(mdsPath);
            if (!image.IsValid)
            {
                messages.Add($"[Error] MDS parse failed: {image.ErrorMessage}");
                return messages;
            }

            messages.Add($"[Info] MDS version: {image.VersionString}");
            messages.Add($"[Info] Medium type: {image.MediumType}");
            messages.Add($"[Info] Sessions: {image.SessionCount}, Tracks: {image.TotalTrackCount}");

            // Check MDF data file
            if (!File.Exists(image.MdfFilePath))
            {
                messages.Add($"[Error] MDF data file not found: {Path.GetFileName(image.MdfFilePath)}");
                return messages;
            }

            long mdfSize = new FileInfo(image.MdfFilePath).Length;
            messages.Add($"[Info] MDF data file: {Path.GetFileName(image.MdfFilePath)} ({mdfSize:N0} bytes)");

            // Run structural validation
            var issues = Native.Iso.MdfParser.ValidateImage(image);
            foreach (var issue in issues)
            {
                messages.Add($"[Warning] {issue}");
            }

            // Report track details
            foreach (var session in image.Sessions)
            {
                foreach (var track in session.RegularTracks)
                {
                    string desc = Native.Iso.MdfParser.DescribeTrack(track);
                    messages.Add($"[Info] {desc}");
                }
            }

            // Check for ISO 9660 PVD in the largest data track
            var dataTrack = image.LargestDataTrack;
            if (dataTrack != null && File.Exists(image.MdfFilePath))
            {
                ValidateIso9660Pvd(image.MdfFilePath, dataTrack, messages);
            }

            if (issues.Count == 0)
                messages.Add("[Info] MDF/MDS image structure is valid.");
        }
        catch (Exception ex)
        {
            messages.Add($"[Error] MDF/MDS verification error: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Checks for a valid ISO 9660 Primary Volume Descriptor in a data track.
    /// </summary>
    private static void ValidateIso9660Pvd(
        string mdfFilePath, Models.MdsTrackBlock track, List<string> messages)
    {
        try
        {
            using var fs = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // PVD is at sector 16
            int sectorSize = track.SectorSize;
            int userDataOffset = track.UserDataOffset;
            int userData = track.UserDataSize;
            uint pregap = track.ExtraBlock?.Pregap ?? 0;

            long pvdSector = 16;
            long pvdFileOffset = (long)track.StartOffset + (pregap + pvdSector) * sectorSize + userDataOffset;

            if (pvdFileOffset + 6 > fs.Length)
                return;

            fs.Seek(pvdFileOffset, SeekOrigin.Begin);
            byte[] pvdHeader = new byte[6];
            int read = 0;
            while (read < 6)
            {
                int r = fs.Read(pvdHeader, read, 6 - read);
                if (r == 0) break;
                read += r;
            }
            if (read < 6) return;

            // Check for PVD type code (1) and "CD001" signature
            if (pvdHeader[0] == 0x01 &&
                pvdHeader[1] == (byte)'C' && pvdHeader[2] == (byte)'D' &&
                pvdHeader[3] == (byte)'0' && pvdHeader[4] == (byte)'0' &&
                pvdHeader[5] == (byte)'1')
            {
                messages.Add("[Info] ISO 9660 Primary Volume Descriptor found in data track.");

                // Try to read volume identifier (32 bytes at offset 40)
                if (pvdFileOffset + 72 <= fs.Length)
                {
                    fs.Seek(pvdFileOffset + 40, SeekOrigin.Begin);
                    byte[] volId = new byte[32];
                    read = 0;
                    while (read < 32)
                    {
                        int r = fs.Read(volId, read, 32 - read);
                        if (r == 0) break;
                        read += r;
                    }
                    if (read == 32)
                    {
                        string volumeLabel = Encoding.ASCII.GetString(volId).Trim();
                        if (!string.IsNullOrWhiteSpace(volumeLabel))
                            messages.Add($"[Info] Volume Identifier: {volumeLabel}");
                    }
                }
            }
            else
            {
                messages.Add("[Warning] No ISO 9660 PVD signature found at sector 16 of data track.");
            }
        }
        catch
        {
            // Best-effort — don't fail if PVD check fails
        }
    }
}
