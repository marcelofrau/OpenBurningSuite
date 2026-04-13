// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Native El Torito (Bootable CD-ROM Format Specification 1.0) post-processor.
/// Patches the El Torito boot catalog in an existing ISO 9660 image to set
/// platform ID, emulation mode, load segment, sector count, and optionally
/// adds multi-boot entries (e.g., EFI + BIOS dual boot) and boot info table.
///
/// The initial boot structures (Boot Record Volume Descriptor and boot catalog)
/// are created by DiscUtils during the ISO build; this class patches and extends
/// them with additional El Torito features that DiscUtils does not expose.
/// </summary>
public static class ElToritoWriter
{
    // ----- El Torito constants -----
    private const int SectorSize = 2048;

    /// <summary>El Torito Boot Record Volume Descriptor type code.</summary>
    private const byte BrvdTypeCode = 0x00;

    /// <summary>El Torito boot system identifier: "EL TORITO SPECIFICATION" (padded to 32 bytes).</summary>
    private static readonly byte[] ElToritoSystemId =
        Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION\0\0\0\0\0\0\0\0\0");

    /// <summary>Offset within the BRVD where the boot catalog sector LBA is stored (4 bytes, LE).</summary>
    private const int BrvdCatalogLbaOffset = 71;

    // ----- Platform IDs (El Torito §2.1) -----
    private const byte PlatformX86 = 0x00;
    private const byte PlatformPowerPc = 0x01;
    private const byte PlatformMac = 0x02;
    private const byte PlatformEfi = 0xEF;

    // ----- Boot media types (El Torito §2.2) -----
    private const byte MediaNoEmulation = 0x00;
    private const byte MediaFloppy1200 = 0x01;
    private const byte MediaFloppy1440 = 0x02;
    private const byte MediaFloppy2880 = 0x03;
    private const byte MediaHardDisk = 0x04;

    /// <summary>
    /// Post-processes an ISO 9660 image to patch El Torito boot catalog entries.
    /// Finds the BRVD, reads the boot catalog location, and patches the validation
    /// entry and initial/default entry with the specified options.
    /// </summary>
    /// <returns>A list of log messages describing the operations performed.</returns>
    public static string[] PatchBootCatalog(BuildJob job)
    {
        var messages = new System.Collections.Generic.List<string>();

        if (!job.Bootable || !File.Exists(job.OutputImagePath))
            return messages.ToArray();

        using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.ReadWrite);

        // ---------------------------------------------------------------
        // Step 1: Locate the Boot Record Volume Descriptor (BRVD)
        // ---------------------------------------------------------------
        long brvdOffset = FindBrvd(stream);
        if (brvdOffset < 0)
        {
            messages.Add("[Warning] El Torito: Boot Record Volume Descriptor not found — skipping post-processing.");
            return messages.ToArray();
        }

        // Read the boot catalog sector LBA from the BRVD
        stream.Seek(brvdOffset + BrvdCatalogLbaOffset, SeekOrigin.Begin);
        var lbaBytes = new byte[4];
        if (stream.Read(lbaBytes, 0, 4) < 4)
        {
            messages.Add("[Warning] El Torito: Cannot read boot catalog LBA from BRVD.");
            return messages.ToArray();
        }
        uint catalogLba = BitConverter.ToUInt32(lbaBytes, 0);
        if (catalogLba == 0)
        {
            messages.Add("[Warning] El Torito: Boot catalog LBA is 0 — skipping.");
            return messages.ToArray();
        }

        long catalogOffset = (long)catalogLba * SectorSize;
        if (catalogOffset + SectorSize > stream.Length)
        {
            messages.Add("[Warning] El Torito: Boot catalog offset exceeds image size.");
            return messages.ToArray();
        }

        // ---------------------------------------------------------------
        // Step 2: Read and patch the boot catalog
        // ---------------------------------------------------------------
        stream.Seek(catalogOffset, SeekOrigin.Begin);
        var catalog = new byte[SectorSize];
        if (stream.Read(catalog, 0, SectorSize) < SectorSize)
        {
            messages.Add("[Warning] El Torito: Cannot read boot catalog sector.");
            return messages.ToArray();
        }

        // --- Validation Entry (bytes 0-31) ---
        byte platformId = ResolvePlatformId(job.BootPlatform);
        catalog[0] = 0x01; // Header ID
        catalog[1] = platformId;
        // Bytes 2-3: Reserved
        catalog[2] = 0x00;
        catalog[3] = 0x00;
        // Manufacturer ID (bytes 4-27): "OpenBurningSuite" padded
        var mfrId = Encoding.ASCII.GetBytes("OpenBurningSuite");
        Array.Clear(catalog, 4, 24);
        Buffer.BlockCopy(mfrId, 0, catalog, 4, Math.Min(mfrId.Length, 24));
        // Key bytes (30-31): 0x55, 0xAA
        catalog[30] = 0x55;
        catalog[31] = 0xAA;
        // Checksum (bytes 28-29): two's complement so all words sum to 0
        catalog[28] = 0x00;
        catalog[29] = 0x00;
        ushort checksum = ComputeValidationChecksum(catalog, 0);
        catalog[28] = (byte)(checksum & 0xFF);
        catalog[29] = (byte)((checksum >> 8) & 0xFF);

        messages.Add($"[Info] El Torito: Validation entry patched — Platform: {job.BootPlatform} (0x{platformId:X2})");

        // --- Initial/Default Entry (bytes 32-63) ---
        byte mediaType = ResolveMediaType(job.BootEmulation);
        catalog[32] = 0x88; // Boot Indicator: bootable
        catalog[33] = mediaType;
        // Load segment (bytes 34-35, LE)
        ushort loadSeg = (ushort)job.BootLoadSegment;
        catalog[34] = (byte)(loadSeg & 0xFF);
        catalog[35] = (byte)((loadSeg >> 8) & 0xFF);
        // System type (byte 36): read from boot image partition table or 0
        // catalog[36] already set by DiscUtils
        // Unused (byte 37)
        catalog[37] = 0x00;
        // Sector count (bytes 38-39, LE)
        if (job.BootSectorCount > 0)
        {
            ushort sectorCount = (ushort)Math.Min(job.BootSectorCount, 65535);
            catalog[38] = (byte)(sectorCount & 0xFF);
            catalog[39] = (byte)((sectorCount >> 8) & 0xFF);
        }
        else if (mediaType == MediaNoEmulation && !string.IsNullOrWhiteSpace(job.BootImagePath) &&
                 File.Exists(job.BootImagePath))
        {
            // Auto-calculate: number of 512-byte virtual sectors
            long bootSize = new FileInfo(job.BootImagePath).Length;
            if (bootSize == 0)
            {
                messages.Add("[Warning] El Torito: Boot image file is empty — boot entry may be invalid");
            }
            ushort autoCount = (ushort)Math.Min((bootSize + 511) / 512, 65535);
            if (bootSize > 65535L * 512)
            {
                messages.Add($"[Warning] El Torito: Boot image ({bootSize} bytes) exceeds maximum sector count " +
                             "(65535 × 512 = ~32 MB). Sector count has been clamped.");
            }
            catalog[38] = (byte)(autoCount & 0xFF);
            catalog[39] = (byte)((autoCount >> 8) & 0xFF);
        }
        // Load RBA (bytes 40-43): already set by DiscUtils, keep as-is

        messages.Add($"[Info] El Torito: Initial entry patched — Emulation: {job.BootEmulation}, " +
                     $"LoadSegment: 0x{loadSeg:X4}, MediaType: 0x{mediaType:X2}");

        // ---------------------------------------------------------------
        // Step 3: Multi-boot — add EFI boot entry if specified
        // ---------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(job.EfiBootImagePath) && File.Exists(job.EfiBootImagePath))
        {
            // We need to embed the EFI boot image into the ISO and create a section entry
            long efiBootLba = EmbedEfiBootImage(stream, job.EfiBootImagePath);
            if (efiBootLba > 0)
            {
                long efiBootSize = new FileInfo(job.EfiBootImagePath).Length;
                ushort efiSectorCount = (ushort)Math.Min((efiBootSize + 511) / 512, 65535);

                // Section Header Entry (bytes 64-95)
                catalog[64] = 0x91; // Section Header: final, more entries follow = no
                catalog[65] = PlatformEfi; // Platform ID: EFI
                // Number of section entries (bytes 66-67, LE): 1
                catalog[66] = 0x01;
                catalog[67] = 0x00;
                // Section ID string (bytes 68-95): "EFI Boot"
                Array.Clear(catalog, 68, 28);
                var efiLabel = Encoding.ASCII.GetBytes("EFI Boot");
                Buffer.BlockCopy(efiLabel, 0, catalog, 68, Math.Min(efiLabel.Length, 28));

                // Section Entry (bytes 96-127)
                catalog[96] = 0x88;  // Boot Indicator: bootable
                catalog[97] = MediaNoEmulation; // No emulation for EFI
                // Load segment: 0 (unused for EFI)
                catalog[98] = 0x00;
                catalog[99] = 0x00;
                // System type
                catalog[100] = 0x00;
                // Unused
                catalog[101] = 0x00;
                // Sector count (LE)
                catalog[102] = (byte)(efiSectorCount & 0xFF);
                catalog[103] = (byte)((efiSectorCount >> 8) & 0xFF);
                // Load RBA (4 bytes, LE)
                catalog[104] = (byte)(efiBootLba & 0xFF);
                catalog[105] = (byte)((efiBootLba >> 8) & 0xFF);
                catalog[106] = (byte)((efiBootLba >> 16) & 0xFF);
                catalog[107] = (byte)((efiBootLba >> 24) & 0xFF);
                // Selection criteria (bytes 108-127): unused
                Array.Clear(catalog, 108, 20);

                messages.Add($"[Info] El Torito: EFI boot entry added — LBA: {efiBootLba}, Sectors: {efiSectorCount}");
            }
            else
            {
                messages.Add("[Warning] El Torito: Failed to embed EFI boot image.");
            }
        }

        // Write the patched boot catalog back
        stream.Seek(catalogOffset, SeekOrigin.Begin);
        stream.Write(catalog, 0, SectorSize);

        // ---------------------------------------------------------------
        // Step 4: Boot info table (ISOLINUX / GRUB extension)
        // ---------------------------------------------------------------
        if (job.BootInfoTable && !string.IsNullOrWhiteSpace(job.BootImagePath))
        {
            PatchBootInfoTable(stream, catalog, catalogLba, messages);
        }

        return messages.ToArray();
    }

    /// <summary>
    /// Verifies El Torito boot structures in an existing ISO image.
    /// Returns a list of validation messages (informational, warnings, errors).
    /// </summary>
    public static string[] VerifyBootCatalog(string imagePath)
    {
        var messages = new System.Collections.Generic.List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add("Error: Image file not found.");
            return messages.ToArray();
        }

        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Find BRVD
        long brvdOffset = FindBrvd(stream);
        if (brvdOffset < 0)
        {
            messages.Add("[Info] No El Torito Boot Record Volume Descriptor found — disc is not bootable.");
            return messages.ToArray();
        }

        messages.Add($"[Info] El Torito BRVD found at offset 0x{brvdOffset:X} (sector {brvdOffset / SectorSize})");

        // Read boot system identifier from BRVD
        stream.Seek(brvdOffset + 7, SeekOrigin.Begin);
        var bsId = new byte[32];
        stream.ReadExactly(bsId, 0, 32);
        var bsIdStr = Encoding.ASCII.GetString(bsId).TrimEnd('\0', ' ');
        if (!bsIdStr.StartsWith("EL TORITO SPECIFICATION", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add($"[Warning] BRVD boot system identifier is '{bsIdStr}' — expected 'EL TORITO SPECIFICATION'.");
        }

        // Read boot catalog LBA
        stream.Seek(brvdOffset + BrvdCatalogLbaOffset, SeekOrigin.Begin);
        var lbaBytes = new byte[4];
        stream.ReadExactly(lbaBytes, 0, 4);
        uint catalogLba = BitConverter.ToUInt32(lbaBytes, 0);
        long catalogOffset = (long)catalogLba * SectorSize;

        if (catalogLba == 0 || catalogOffset + SectorSize > stream.Length)
        {
            messages.Add($"[Error] Boot catalog LBA {catalogLba} is invalid or out of range.");
            return messages.ToArray();
        }

        messages.Add($"[Info] Boot catalog at sector {catalogLba} (offset 0x{catalogOffset:X})");

        // Read boot catalog
        stream.Seek(catalogOffset, SeekOrigin.Begin);
        var catalog = new byte[SectorSize];
        if (stream.Read(catalog, 0, SectorSize) < SectorSize)
        {
            messages.Add("[Error] Cannot read boot catalog sector.");
            return messages.ToArray();
        }

        // Validate Validation Entry
        if (catalog[0] != 0x01)
        {
            messages.Add($"[Error] Validation entry header ID is 0x{catalog[0]:X2} — expected 0x01.");
        }
        else
        {
            messages.Add("[Info] Validation entry header ID: OK (0x01)");
        }

        byte platformId = catalog[1];
        string platformName = platformId switch
        {
            PlatformX86 => "x86/BIOS",
            PlatformPowerPc => "PowerPC",
            PlatformMac => "Mac",
            PlatformEfi => "EFI",
            _ => $"Unknown (0x{platformId:X2})"
        };
        messages.Add($"[Info] Platform ID: {platformName}");

        // Key bytes
        if (catalog[30] != 0x55 || catalog[31] != 0xAA)
        {
            messages.Add($"[Warning] Validation entry key bytes: 0x{catalog[30]:X2}{catalog[31]:X2} — expected 0x55AA.");
        }

        // Checksum verification
        ushort sum = 0;
        for (int i = 0; i < 32; i += 2)
        {
            sum += (ushort)(catalog[i] | (catalog[i + 1] << 8));
        }
        if (sum != 0)
        {
            messages.Add($"[Warning] Validation entry checksum mismatch (sum=0x{sum:X4}, expected 0x0000).");
        }
        else
        {
            messages.Add("[Info] Validation entry checksum: OK");
        }

        // Validate Initial/Default Entry
        byte bootIndicator = catalog[32];
        if (bootIndicator == 0x88)
            messages.Add("[Info] Initial entry: Bootable (0x88)");
        else if (bootIndicator == 0x00)
            messages.Add("[Info] Initial entry: Not bootable (0x00)");
        else
            messages.Add($"[Warning] Initial entry boot indicator: 0x{bootIndicator:X2} — expected 0x88 or 0x00.");

        byte mediaType = catalog[33];
        string mediaName = mediaType switch
        {
            MediaNoEmulation => "No Emulation",
            MediaFloppy1200 => "1.2 MB Floppy",
            MediaFloppy1440 => "1.44 MB Floppy",
            MediaFloppy2880 => "2.88 MB Floppy",
            MediaHardDisk => "Hard Disk",
            _ => $"Unknown (0x{mediaType:X2})"
        };
        messages.Add($"[Info] Boot media type: {mediaName}");

        ushort loadSegment = (ushort)(catalog[34] | (catalog[35] << 8));
        messages.Add($"[Info] Load segment: 0x{loadSegment:X4}");

        ushort sectorCount = (ushort)(catalog[38] | (catalog[39] << 8));
        messages.Add($"[Info] Sector count: {sectorCount}");

        uint loadRba = BitConverter.ToUInt32(catalog, 40);
        messages.Add($"[Info] Load RBA (boot image sector): {loadRba}");

        if (loadRba > 0)
        {
            long bootImageOffset = (long)loadRba * SectorSize;
            if (bootImageOffset >= stream.Length)
                messages.Add("[Warning] Boot image LBA points beyond end of image.");
        }

        // Check for additional boot entries (multi-boot)
        int entryOffset = 64;
        int sectionIndex = 0;
        while (entryOffset + 32 <= SectorSize)
        {
            byte headerIndicator = catalog[entryOffset];
            if (headerIndicator == 0x00)
                break; // No more entries

            if (headerIndicator == 0x90 || headerIndicator == 0x91)
            {
                // Section Header Entry
                sectionIndex++;
                byte secPlatform = catalog[entryOffset + 1];
                ushort numEntries = (ushort)(catalog[entryOffset + 2] | (catalog[entryOffset + 3] << 8));
                string secPlatName = secPlatform switch
                {
                    PlatformX86 => "x86/BIOS",
                    PlatformPowerPc => "PowerPC",
                    PlatformMac => "Mac",
                    PlatformEfi => "EFI",
                    _ => $"0x{secPlatform:X2}"
                };

                bool isFinal = headerIndicator == 0x91;
                messages.Add($"[Info] Section {sectionIndex}: Platform={secPlatName}, " +
                             $"Entries={numEntries}{(isFinal ? " (final)" : "")}");

                // Read section entries
                for (int e = 0; e < numEntries && entryOffset + 32 + (e + 1) * 32 <= SectorSize; e++)
                {
                    int seOffset = entryOffset + 32 + e * 32;
                    byte seBootInd = catalog[seOffset];
                    byte seMedia = catalog[seOffset + 1];
                    ushort seLoadSeg = (ushort)(catalog[seOffset + 2] | (catalog[seOffset + 3] << 8));
                    ushort seSectorCnt = (ushort)(catalog[seOffset + 6] | (catalog[seOffset + 7] << 8));
                    uint seLba = BitConverter.ToUInt32(catalog, seOffset + 8);

                    messages.Add($"[Info]   Entry {e + 1}: Bootable={seBootInd == 0x88}, " +
                                 $"Media=0x{seMedia:X2}, LoadSeg=0x{seLoadSeg:X4}, " +
                                 $"Sectors={seSectorCnt}, LBA={seLba}");
                }

                entryOffset += 32 + numEntries * 32;
                if (isFinal) break;
            }
            else
            {
                break; // Unknown entry type
            }
        }

        return messages.ToArray();
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Scans the ISO volume descriptor area to find the Boot Record Volume Descriptor.
    /// Returns the byte offset of the BRVD, or -1 if not found.
    /// </summary>
    private static long FindBrvd(Stream stream)
    {
        // Volume descriptors start at sector 16 (offset 32768)
        long offset = 16L * SectorSize;
        var sector = new byte[SectorSize];

        // Scan up to 20 sectors for safety
        for (int i = 0; i < 20 && offset + SectorSize <= stream.Length; i++)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.Read(sector, 0, SectorSize) < 7) break;

            // Check for CD001 identifier at bytes 1-5
            if (sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
                sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1')
            {
                // BRVD has type code 0 and boot system identifier "EL TORITO SPECIFICATION"
                if (sector[0] == BrvdTypeCode)
                {
                    var bsId = Encoding.ASCII.GetString(sector, 7, 23);
                    if (bsId.StartsWith("EL TORITO SPECIFICATION", StringComparison.OrdinalIgnoreCase))
                        return offset;
                }

                // Volume Descriptor Set Terminator (type 255) — stop searching
                if (sector[0] == 0xFF)
                    break;
            }

            offset += SectorSize;
        }

        return -1;
    }

    /// <summary>
    /// Embeds an EFI boot image at the end of the ISO image and returns its sector LBA.
    /// The image is padded to a sector boundary.
    /// </summary>
    private static long EmbedEfiBootImage(Stream isoStream, string efiImagePath)
    {
        try
        {
            var efiData = File.ReadAllBytes(efiImagePath);
            if (efiData.Length == 0) return -1;

            // Align current end of image to sector boundary
            long currentLength = isoStream.Length;
            long alignedLength = ((currentLength + SectorSize - 1) / SectorSize) * SectorSize;
            if (alignedLength > currentLength)
            {
                isoStream.SetLength(alignedLength);
            }

            long efiLba = alignedLength / SectorSize;

            // Pad EFI image to sector boundary
            int paddedSize = ((efiData.Length + SectorSize - 1) / SectorSize) * SectorSize;
            var padded = new byte[paddedSize];
            Buffer.BlockCopy(efiData, 0, padded, 0, efiData.Length);

            isoStream.Seek(alignedLength, SeekOrigin.Begin);
            isoStream.Write(padded, 0, padded.Length);

            return efiLba;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Patches the boot info table into the boot image within the ISO.
    /// The boot info table (El Torito / ISOLINUX extension) is written at byte offset 8
    /// of the boot image and contains:
    ///   Offset  0 (8 in image): PVD LBA (4 bytes, LE) — always sector 16
    ///   Offset  4 (12 in image): Boot file LBA (4 bytes, LE)
    ///   Offset  8 (16 in image): Boot file length in bytes (4 bytes, LE)
    ///   Offset 12 (20 in image): Checksum (4 bytes, LE) — sum of all 32-bit words from offset 64 to end
    /// </summary>
    private static void PatchBootInfoTable(Stream stream, byte[] catalog, uint catalogLba,
        System.Collections.Generic.List<string> messages)
    {
        // Read boot image LBA from the initial entry
        uint bootLba = BitConverter.ToUInt32(catalog, 40);
        if (bootLba == 0)
        {
            messages.Add("[Warning] El Torito: Boot info table skipped — boot image LBA is 0.");
            return;
        }

        // Read the boot image to calculate its size and checksum
        long bootOffset = (long)bootLba * SectorSize;
        ushort sectorCount = (ushort)(catalog[38] | (catalog[39] << 8));
        int bootImageSize = sectorCount * 512;
        if (bootImageSize == 0)
            bootImageSize = SectorSize; // Minimum one sector

        // Cap at a reasonable size to prevent reading too much
        if (bootOffset + bootImageSize > stream.Length)
            bootImageSize = (int)(stream.Length - bootOffset);
        if (bootImageSize < 64)
        {
            messages.Add("[Warning] El Torito: Boot image too small for boot info table.");
            return;
        }

        var bootImage = new byte[bootImageSize];
        stream.Seek(bootOffset, SeekOrigin.Begin);
        int bytesRead = stream.Read(bootImage, 0, bootImageSize);
        if (bytesRead < 64)
        {
            messages.Add("[Warning] El Torito: Could not read boot image for boot info table.");
            return;
        }

        // Write boot info table at offset 8 within the boot image
        // PVD LBA (always 16)
        bootImage[8] = 16;
        bootImage[9] = 0;
        bootImage[10] = 0;
        bootImage[11] = 0;
        // Boot file LBA
        bootImage[12] = (byte)(bootLba & 0xFF);
        bootImage[13] = (byte)((bootLba >> 8) & 0xFF);
        bootImage[14] = (byte)((bootLba >> 16) & 0xFF);
        bootImage[15] = (byte)((bootLba >> 24) & 0xFF);
        // Boot file length
        bootImage[16] = (byte)(bootImageSize & 0xFF);
        bootImage[17] = (byte)((bootImageSize >> 8) & 0xFF);
        bootImage[18] = (byte)((bootImageSize >> 16) & 0xFF);
        bootImage[19] = (byte)((bootImageSize >> 24) & 0xFF);
        // Checksum: sum of 32-bit LE words from offset 64 to end of boot image
        // First clear the checksum field
        bootImage[20] = 0;
        bootImage[21] = 0;
        bootImage[22] = 0;
        bootImage[23] = 0;
        uint checksum = 0;
        for (int i = 64; i + 3 < bootImageSize; i += 4)
        {
            checksum += (uint)(bootImage[i] | (bootImage[i + 1] << 8) |
                               (bootImage[i + 2] << 16) | (bootImage[i + 3] << 24));
        }
        bootImage[20] = (byte)(checksum & 0xFF);
        bootImage[21] = (byte)((checksum >> 8) & 0xFF);
        bootImage[22] = (byte)((checksum >> 16) & 0xFF);
        bootImage[23] = (byte)((checksum >> 24) & 0xFF);

        // Write patched boot image back
        stream.Seek(bootOffset, SeekOrigin.Begin);
        stream.Write(bootImage, 0, bootImageSize);

        messages.Add($"[Info] El Torito: Boot info table patched at LBA {bootLba} " +
                     $"(size: {bootImageSize} bytes, checksum: 0x{checksum:X8})");
    }

    /// <summary>
    /// Computes the El Torito validation entry checksum.
    /// The checksum is computed such that all 16-bit words in the 32-byte
    /// validation entry sum to zero (two's complement).
    /// </summary>
    private static ushort ComputeValidationChecksum(byte[] catalog, int offset)
    {
        uint sum = 0;
        for (int i = offset; i < offset + 32; i += 2)
        {
            sum += (uint)(catalog[i] | (catalog[i + 1] << 8));
        }
        // Two's complement: the checksum value that makes the total sum zero
        return (ushort)(0x10000 - (sum & 0xFFFF));
    }

    /// <summary>Resolves the El Torito platform ID from a friendly string name.</summary>
    public static byte ResolvePlatformId(string? platform)
    {
        return (platform?.ToUpperInvariant()) switch
        {
            "X86" or "BIOS" or "PC" => PlatformX86,
            "POWERPC" or "PPC" => PlatformPowerPc,
            "MAC" or "MACINTOSH" => PlatformMac,
            "EFI" or "UEFI" => PlatformEfi,
            _ => PlatformX86
        };
    }

    /// <summary>Resolves the El Torito boot media type from a friendly string name.</summary>
    public static byte ResolveMediaType(string? emulation)
    {
        return (emulation?.ToUpperInvariant()) switch
        {
            "NOEMULATION" or "NO EMULATION" or "NONE" => MediaNoEmulation,
            "FLOPPY1200" or "1.2M FLOPPY" or "1200" => MediaFloppy1200,
            "FLOPPY1440" or "1.44M FLOPPY" or "1440" => MediaFloppy1440,
            "FLOPPY2880" or "2.88M FLOPPY" or "2880" => MediaFloppy2880,
            "HARDDISK" or "HARD DISK" or "HDD" => MediaHardDisk,
            _ => MediaNoEmulation
        };
    }

    /// <summary>
    /// Maps a friendly emulation name to the DiscUtils BootDeviceEmulation enum value.
    /// </summary>
    public static DiscUtils.Iso9660.BootDeviceEmulation ResolveDiscUtilsEmulation(string? emulation)
    {
        return (emulation?.ToUpperInvariant()) switch
        {
            "FLOPPY1200" or "1.2M FLOPPY" or "1200" => DiscUtils.Iso9660.BootDeviceEmulation.Diskette1200KiB,
            "FLOPPY1440" or "1.44M FLOPPY" or "1440" => DiscUtils.Iso9660.BootDeviceEmulation.Diskette1440KiB,
            "FLOPPY2880" or "2.88M FLOPPY" or "2880" => DiscUtils.Iso9660.BootDeviceEmulation.Diskette2880KiB,
            "HARDDISK" or "HARD DISK" or "HDD" => DiscUtils.Iso9660.BootDeviceEmulation.HardDisk,
            _ => DiscUtils.Iso9660.BootDeviceEmulation.NoEmulation
        };
    }
}
