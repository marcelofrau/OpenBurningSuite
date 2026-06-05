// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// MMC (Multi-Media Commands) and SCSI Primary Commands constants and
/// CDB (Command Descriptor Block) builder helpers for optical drive operations.
/// </summary>
public static class MmcCommands
{
    // -----------------------------------------------------------------------
    // SCSI Primary Commands (SPC) opcodes
    // -----------------------------------------------------------------------
    public const byte TestUnitReady     = 0x00;
    public const byte RequestSense      = 0x03;
    public const byte Inquiry           = 0x12;
    public const byte ModeSelect6       = 0x15;
    public const byte ModeSense6        = 0x1A;
    public const byte StartStopUnit     = 0x1B;
    public const byte ModeSelect10      = 0x55;
    public const byte ModeSense10       = 0x5A;

    // -----------------------------------------------------------------------
    // MMC (Multi-Media Commands) opcodes
    // -----------------------------------------------------------------------
    public const byte Read10            = 0x28;
    public const byte Read12            = 0xA8;
    public const byte ReadCapacity      = 0x25;
    public const byte Write10           = 0x2A;
    public const byte Write12           = 0xAA;
    public const byte SynchronizeCache  = 0x35;
    public const byte ReadTocPmaAtip    = 0x43;
    public const byte GetConfiguration  = 0x46;
    public const byte GetEventStatus    = 0x4A;
    public const byte ReadDiscInfo      = 0x51;
    public const byte ReadTrackInfo     = 0x52;
    public const byte ReserveTrack      = 0x53;
    public const byte SendOpcInfo       = 0x54;
    public const byte CloseTrackSession = 0x5B;
    public const byte ReadBufferCapacity = 0x5C;
    public const byte SendCueSheet      = 0x5D;
    public const byte Blank             = 0xA1;
    public const byte ReadCd            = 0xBE;
    public const byte SetStreaming      = 0xB6;
    public const byte Verify10           = 0x2F;
    public const byte Verify12           = 0xAF;
    public const byte PreventAllowMediumRemoval = 0x1E;
    public const byte FormatUnit        = 0x04;
    public const byte ReadFormatCapacities = 0x23;
    public const byte ReadHeader        = 0x44;
    public const byte ReadDiscStructure = 0xAD;
    public const byte SendDiscStructure = 0xBF;
    public const byte ReadSubChannel    = 0x42;
    public const byte LogSense         = 0x4D;
    public const byte MechanismStatus  = 0xBD;
    public const byte GetPerformance    = 0xAC;
    public const byte SetCdSpeed        = 0xBB;
    public const byte ReportKey         = 0xA4;
    public const byte SendKey           = 0xA3;

    // -----------------------------------------------------------------------
    // Write parameter mode page (Mode Page 05h)
    // -----------------------------------------------------------------------
    public const byte WriteParametersPage = 0x05;

    // Write types for mode page 05h
    public const byte WriteTypePacket       = 0x00;
    /// <summary>
    /// Alias for WriteTypePacket (0x00). For DVD-R/-RW Sequential Recording,
    /// write type 0x00 means Incremental Sequential Recording per MMC-5 §7.5.4.
    /// Using this alias in DVD-R code makes the intent clearer.
    /// </summary>
    public const byte WriteTypeIncremental  = 0x00;
    public const byte WriteTypeTao          = 0x01;
    public const byte WriteTypeSao          = 0x02;
    public const byte WriteTypeRaw          = 0x03;

    // Track modes
    public const byte TrackModeAudio        = 0x00;  // 2 audio channels
    public const byte TrackModeData         = 0x04;  // Data, uninterrupted
    public const byte TrackModeDataInc      = 0x05;  // Data, incremental

    // Data block types for mode page 05h
    public const byte BlockTypeRaw2352      = 0x00;
    public const byte BlockTypeRawPQ2368    = 0x01;
    public const byte BlockTypeRawPW2448    = 0x02;
    public const byte BlockTypeRawPW2448_R  = 0x03; // Raw data with R-W sub-channel (raw interleaved, 2448 bytes)
    public const byte BlockTypeMode1_2048   = 0x08;
    public const byte BlockTypeMode2_2336   = 0x09;
    public const byte BlockTypeMode2XA_2048 = 0x0A;
    public const byte BlockTypeMode2XA_2328 = 0x0B;
    public const byte BlockTypeMode2XA_2332 = 0x0C;

    // -----------------------------------------------------------------------
    // Blank types
    // -----------------------------------------------------------------------
    public const byte BlankTypeDisc         = 0x00;  // Blank entire disc
    public const byte BlankTypeMinimal      = 0x01;  // Blank PMA, TOC, pregap
    public const byte BlankTypeTrack        = 0x02;  // Blank track
    public const byte BlankTypeUnreserve    = 0x03;  // Unreserve track
    public const byte BlankTypeTrackTail    = 0x04;  // Blank track tail
    public const byte BlankTypeUncloseSession = 0x05;
    public const byte BlankTypeSession      = 0x06;  // Blank last session

    // -----------------------------------------------------------------------
    // Format Unit type codes for BD media
    // -----------------------------------------------------------------------
    // Per MMC-5 §6.24.3, Format Descriptor byte 4 layout:
    //   bits 7:2 = Format Type, bits 1:0 = Format Sub-Type
    // These constants store the pre-encoded byte values (format type << 2)
    // ready to be placed directly into the descriptor byte 4.
    // This matches what READ FORMAT CAPACITIES returns in the descriptor.
    public const byte FormatBdReFullFormat   = 0xC4; // Format type 0x31 << 2
    public const byte FormatBdReSparePrepare = 0xC8; // Format type 0x32 << 2
    public const byte FormatBdReSpareOnly    = 0xC0; // Format type 0x30 << 2 (quick format: spare area only)

    // -----------------------------------------------------------------------
    // Format Unit type codes for DVD media
    // -----------------------------------------------------------------------
    // Same encoding: format type << 2, placed directly into descriptor byte 4.
    /// <summary>DVD+RW full format (entire disc with defect management). Format type 0x00 &lt;&lt; 2.</summary>
    public const byte FormatDvdPlusRwFull       = 0x00;
    /// <summary>DVD+RW background format restart. Format type 0x26 &lt;&lt; 2.</summary>
    public const byte FormatDvdPlusRwRestart    = 0x98;
    /// <summary>DVD-RAM full format. Format type 0x00 &lt;&lt; 2.</summary>
    public const byte FormatDvdRamFull          = 0x00;
    /// <summary>DVD-RAM spare area expansion (quick format). Format type 0x01 &lt;&lt; 2.</summary>
    public const byte FormatDvdRamSpare         = 0x04;
    /// <summary>DVD-RW full format (Restricted Overwrite). Format type 0x10 &lt;&lt; 2.</summary>
    public const byte FormatDvdRwFull           = 0x40;
    /// <summary>DVD-RW quick format (Restricted Overwrite). Format type 0x15 &lt;&lt; 2.</summary>
    public const byte FormatDvdRwQuick          = 0x54;

    // -----------------------------------------------------------------------
    // READ DISC STRUCTURE format codes
    // -----------------------------------------------------------------------
    public const byte DiscStructureFormatPhysical     = 0x00; // DVD Physical Format Information
    public const byte DiscStructureFormatCopyright     = 0x01; // DVD Copyright Information
    public const byte DiscStructureFormatDiscKey       = 0x02; // DVD Disc Key (CSS/CPPM)
    public const byte DiscStructureFormatBca           = 0x03; // Burst Cutting Area
    public const byte DiscStructureFormatManufacturer  = 0x04; // DVD Manufacturer Information
    public const byte DiscStructureFormatCmi           = 0x05; // Copyright Management Information
    public const byte DiscStructureFormatMediaId       = 0x06; // Media Identifier (BD)
    public const byte DiscStructureFormatMediaKeyBlock = 0x07; // Media Key Block (BD)
    public const byte DiscStructureFormatDds           = 0x08; // Disc Definition Structure (DVD-RAM)
    public const byte DiscStructureFormatRamCartridge  = 0x09; // DVD-RAM Medium Status (Cartridge)
    public const byte DiscStructureFormatSpareArea     = 0x0A; // DVD-RAM Spare Area Information
    public const byte DiscStructureFormatRamRecording  = 0x0C; // DVD-RAM Recording Type Information
    public const byte DiscStructureFormatRma           = 0x0D; // RMA Information (DVD-R/-RW Recording Management Area)
    public const byte DiscStructureFormatPreRecordedLi = 0x0E; // Pre-recorded Information in Lead-in
    public const byte DiscStructureFormatUniqueDiscId  = 0x0F; // DVD-R/-RW Unique Disc Identifier
    public const byte DiscStructureFormatDcb           = 0x30; // Disc Control Block (DCB)
    public const byte DiscStructureFormatWriteProtect  = 0xC0; // Write Protection Status
    public const byte DiscStructureFormatStructureList = 0xFF; // List of recognised structure format codes

    // -----------------------------------------------------------------------
    // SEND DISC STRUCTURE format codes
    // -----------------------------------------------------------------------
    public const byte SendStructureFormatTimeStamp     = 0x0F; // Timestamp
    public const byte SendStructureFormatDcb           = 0x30; // Disc Control Block

    // -----------------------------------------------------------------------
    // Mode page constants
    // -----------------------------------------------------------------------
    public const byte ErrorRecoveryPage = 0x01;

    // -----------------------------------------------------------------------
    // LOG SENSE page codes
    // -----------------------------------------------------------------------
    /// <summary>Supported Log Pages (00h).</summary>
    public const byte LogPageSupportedPages   = 0x00;
    /// <summary>Write Error Counters log page (02h).</summary>
    public const byte LogPageWriteErrors      = 0x02;
    /// <summary>Read Error Counters log page (03h).</summary>
    public const byte LogPageReadErrors       = 0x03;
    /// <summary>Non-Medium Error log page (06h).</summary>
    public const byte LogPageNonMediumErrors  = 0x06;
    /// <summary>Temperature log page (0Dh).</summary>
    public const byte LogPageTemperature      = 0x0D;

    // -----------------------------------------------------------------------
    // GET EVENT STATUS NOTIFICATION event class request/notification bitmasks
    // -----------------------------------------------------------------------
    /// <summary>Operational Change event class (bit 1).</summary>
    public const byte EventClassOperational  = 0x02;
    /// <summary>Power Management event class (bit 2).</summary>
    public const byte EventClassPowerMgmt    = 0x04;
    /// <summary>External Request event class (bit 3).</summary>
    public const byte EventClassExternalReq  = 0x08;
    /// <summary>Media event class (bit 4).</summary>
    public const byte EventClassMedia        = 0x10;
    /// <summary>Multi-Host event class (bit 5).</summary>
    public const byte EventClassMultiHost    = 0x20;
    /// <summary>Device Busy event class (bit 6).</summary>
    public const byte EventClassDeviceBusy   = 0x40;

    // -----------------------------------------------------------------------
    // Close Track/Session function codes
    // -----------------------------------------------------------------------
    public const byte CloseTrack            = 0x01;
    public const byte CloseSession          = 0x02;
    /// <summary>
    /// Finalize disc: close all sessions and write lead-out.
    /// Per MMC-5 Table 29, Close Function 110b (0x06) finalizes the disc.
    /// Note: function 011b (0x03) is RESERVED and must not be used — drives
    /// reject it with "Illegal Request" (ASC=0x24 or ASC=0x2C).
    /// </summary>
    public const byte CloseSessionFinal     = 0x06;

    // -----------------------------------------------------------------------
    // REPORT KEY / SEND KEY key class and format constants
    // -----------------------------------------------------------------------
    /// <summary>Key class for AACS (Advanced Access Content System), used by Blu-ray.</summary>
    public const byte KeyClassAacs          = 0x02;
    /// <summary>Key class for CSS/CPPM (Content Scramble System), used by DVD.</summary>
    public const byte KeyClassCssCppm       = 0x00;

    // REPORT KEY key formats (AACS — Key Class 0x02)
    /// <summary>AGID for AACS (REPORT KEY).</summary>
    public const byte AacsReportAgid            = 0x00;
    /// <summary>Drive Certificate Challenge (REPORT KEY).</summary>
    public const byte AacsReportDriveCert       = 0x01;
    /// <summary>Drive Key (REPORT KEY).</summary>
    public const byte AacsReportDriveKey         = 0x02;

    // SEND KEY key formats (AACS — Key Class 0x02)
    /// <summary>Host Certificate Challenge (SEND KEY).</summary>
    public const byte AacsSendHostCert          = 0x01;
    /// <summary>Host Key (SEND KEY).</summary>
    public const byte AacsSendHostKey           = 0x02;

    // -----------------------------------------------------------------------
    // READ SUB-CHANNEL sub-channel data format codes
    // -----------------------------------------------------------------------
    /// <summary>Sub-Q channel current position (format 01h).</summary>
    public const byte SubChannelCurrentPosition = 0x01;
    /// <summary>Media Catalogue Number (MCN/UPC, format 02h).</summary>
    public const byte SubChannelMcn             = 0x02;
    /// <summary>Track International Standard Recording Code (ISRC, format 03h).</summary>
    public const byte SubChannelIsrc            = 0x03;

    // -----------------------------------------------------------------------
    // CDB builders
    // -----------------------------------------------------------------------

    /// <summary>Build TEST UNIT READY CDB (6 bytes).</summary>
    public static byte[] BuildTestUnitReady()
        => new byte[] { TestUnitReady, 0, 0, 0, 0, 0 };

    /// <summary>Build INQUIRY CDB (6 bytes).</summary>
    public static byte[] BuildInquiry(byte allocationLength = 96)
        => new byte[] { Inquiry, 0, 0, 0, allocationLength, 0 };

    /// <summary>Build INQUIRY CDB with VPD (Vital Product Data) page support (6 bytes).</summary>
    /// <param name="pageCode">VPD page code (e.g. 0x80 for Unit Serial Number).</param>
    /// <param name="allocationLength">Maximum response length.</param>
    /// <remarks>SPC-4 §6.4.2: EVPD=1 (byte 1 bit 0), Page Code in byte 2.</remarks>
    public static byte[] BuildInquiryVpd(byte pageCode, byte allocationLength = 252)
        => new byte[] { Inquiry, 0x01, pageCode, 0, allocationLength, 0 };

    /// <summary>Build READ TOC/PMA/ATIP CDB (10 bytes).</summary>
    public static byte[] BuildReadToc(byte format = 0, byte trackNumber = 1,
        ushort allocationLength = 2048)
    {
        // Byte 1 bit 1 = MSF flag; set MSF for Full TOC (format 2) for MSF timestamps
        return new byte[]
        {
            ReadTocPmaAtip,
            (byte)(format == 2 ? 0x02 : 0x00),  // MSF=1 for format 2 (Full TOC)
            (byte)(format & 0x0F),  // Format field is bits 3:0 of byte 2; mask to 4 bits per MMC-5
            0, 0, 0,
            trackNumber,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build GET CONFIGURATION CDB (10 bytes).</summary>
    /// <param name="startFeature">Starting feature number.</param>
    /// <param name="allocationLength">Maximum response length.</param>
    /// <param name="rt">Request Type: 0x00 = all supported features (for capability detection),
    /// 0x01 = only the specified feature, 0x02 = only currently active features.</param>
    public static byte[] BuildGetConfiguration(ushort startFeature = 0,
        ushort allocationLength = 512, byte rt = 0x00)
    {
        return new byte[]
        {
            GetConfiguration,
            (byte)(rt & 0x03),  // RT field (bits 1:0)
            (byte)(startFeature >> 8),
            (byte)(startFeature & 0xFF),
            0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build READ DISC INFORMATION CDB (10 bytes).</summary>
    public static byte[] BuildReadDiscInfo(ushort allocationLength = 34)
    {
        return new byte[]
        {
            ReadDiscInfo,
            0x00,  // Data type 000b (standard disc info)
            0, 0, 0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build READ TRACK INFORMATION CDB (10 bytes).</summary>
    public static byte[] BuildReadTrackInfo(uint trackNumber, ushort allocationLength = 36)
    {
        return new byte[]
        {
            ReadTrackInfo,
            0x01,  // Address/Number type: Track Number
            (byte)(trackNumber >> 24),
            (byte)(trackNumber >> 16),
            (byte)(trackNumber >> 8),
            (byte)(trackNumber & 0xFF),
            0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build READ(10) CDB for reading sectors.</summary>
    public static byte[] BuildRead10(uint lba, ushort sectorCount)
    {
        return new byte[]
        {
            Read10,
            0x00,  // flags
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            0,     // group number
            (byte)(sectorCount >> 8),
            (byte)(sectorCount & 0xFF),
            0      // control
        };
    }

    /// <summary>Build READ(12) CDB for reading sectors from DVD/BD media.</summary>
    /// <remarks>
    /// Per SBC-4 Table 62 and SPC-6, READ(12) uses a 12-byte CDB:
    ///   Byte 0:    Operation Code (A8h)
    ///   Byte 1:    Flags (DPO=0, FUA=0, reserved)
    ///   Bytes 2-5: Logical Block Address (big-endian, 32-bit)
    ///   Bytes 6-9: Transfer Length (big-endian, 32-bit sector count)
    ///   Byte 10:   Group Number (0)
    ///   Byte 11:   Control (0)
    ///
    /// READ(12) supports a 32-bit transfer length field (up to 4,294,967,295 sectors)
    /// compared to READ(10)'s 16-bit limit (65,535 sectors). This is preferred for
    /// DVD and Blu-ray media where larger contiguous reads improve performance and
    /// reduce command overhead. READ(10) remains suitable for CD media where per-
    /// command sector counts are smaller.
    /// </remarks>
    public static byte[] BuildRead12(uint lba, uint sectorCount)
    {
        return new byte[]
        {
            Read12,
            0x00,                          // Flags: DPO=0, FUA=0
            (byte)(lba >> 24),             // LBA MSB
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),            // LBA LSB
            (byte)(sectorCount >> 24),     // Transfer Length MSB
            (byte)(sectorCount >> 16),
            (byte)(sectorCount >> 8),
            (byte)(sectorCount & 0xFF),    // Transfer Length LSB
            0,                             // Group Number
            0                              // Control
        };
    }

    /// <summary>Build READ CD CDB (12 bytes) for raw/subchannel sector reads.</summary>
    public static byte[] BuildReadCd(uint lba, uint sectorCount,
        byte expectedSectorType = 0, byte mainChannelBits = 0xF8,
        byte subChannelBits = 0)
    {
        // READ CD transfer length is a 24-bit field (max 0xFFFFFF sectors)
        if (sectorCount > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(sectorCount),
                $"READ CD supports at most {0xFFFFFF} sectors per command, got {sectorCount}.");

        return new byte[]
        {
            ReadCd,
            (byte)(expectedSectorType << 2),
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            (byte)(sectorCount >> 16),
            (byte)(sectorCount >> 8),
            (byte)(sectorCount & 0xFF),
            mainChannelBits,  // Sync+Header+UserData+EDC/ECC+ErrorFlags
            subChannelBits,   // Subchannel selection
            0
        };
    }

    /// <summary>Build WRITE(10) CDB for writing sectors.</summary>
    public static byte[] BuildWrite10(uint lba, ushort sectorCount)
    {
        return new byte[]
        {
            Write10,
            0x00,
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            0,
            (byte)(sectorCount >> 8),
            (byte)(sectorCount & 0xFF),
            0
        };
    }

    /// <summary>Build WRITE(12) CDB for writing sectors to DVD/BD media.</summary>
    /// <remarks>
    /// Per SBC-4 Table 99 and SPC-6, WRITE(12) uses a 12-byte CDB:
    ///   Byte 0:    Operation Code (AAh)
    ///   Byte 1:    Flags (DPO=0, FUA=0, reserved)
    ///   Bytes 2-5: Logical Block Address (big-endian, 32-bit)
    ///   Bytes 6-9: Transfer Length (big-endian, 32-bit sector count)
    ///   Byte 10:   Group Number (0)
    ///   Byte 11:   Control (0)
    ///
    /// WRITE(12) supports a 32-bit transfer length field (up to 4,294,967,295 sectors)
    /// compared to WRITE(10)'s 16-bit limit (65,535 sectors). This is preferred for
    /// DVD and Blu-ray media where larger contiguous writes improve performance and
    /// reduce command overhead. WRITE(10) remains suitable for CD media where per-
    /// command sector counts are smaller.
    /// </remarks>
    public static byte[] BuildWrite12(uint lba, uint sectorCount)
    {
        return new byte[]
        {
            Write12,
            0x00,                          // Flags: DPO=0, FUA=0
            (byte)(lba >> 24),             // LBA MSB
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),            // LBA LSB
            (byte)(sectorCount >> 24),     // Transfer Length MSB
            (byte)(sectorCount >> 16),
            (byte)(sectorCount >> 8),
            (byte)(sectorCount & 0xFF),    // Transfer Length LSB
            0,                             // Group Number
            0                              // Control
        };
    }

    /// <summary>Build SYNCHRONIZE CACHE CDB (10 bytes).</summary>
    /// <remarks>
    /// Per SBC-4 §5.35 / MMC-5:
    ///   Byte 0: Operation Code (35h)
    ///   Byte 1: bit 1 = Immed (immediate reporting)
    ///   Bytes 2-5: LBA (ignored for full cache sync when Number of Blocks = 0)
    ///   Byte 6: Group Number
    ///   Bytes 7-8: Number of Logical Blocks (0 = all)
    ///   Byte 9: Control
    ///
    /// When Immed=1, the drive returns status immediately and performs the
    /// cache flush in the background. The host must poll with TEST UNIT READY
    /// to determine when the flush is complete. This avoids long synchronous
    /// waits that can cause IOKit transport timeouts on macOS, leading to
    /// segmentation faults when the kernel tears down the SCSI task mid-wait.
    /// </remarks>
    /// <param name="immediate">
    /// When true, the drive returns GOOD status immediately and flushes
    /// asynchronously. When false (default), the drive waits until the
    /// flush completes before returning status.
    /// </param>
    public static byte[] BuildSynchronizeCache(bool immediate = false)
        => new byte[] { SynchronizeCache, (byte)(immediate ? 0x02 : 0x00), 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Build BLANK CDB (12 bytes).</summary>
    public static byte[] BuildBlank(byte blankType, uint startAddress = 0, bool immediate = true)
    {
        return new byte[]
        {
            Blank,
            (byte)((immediate ? 0x10 : 0x00) | (blankType & 0x07)),
            (byte)(startAddress >> 24),
            (byte)(startAddress >> 16),
            (byte)(startAddress >> 8),
            (byte)(startAddress & 0xFF),
            0, 0, 0, 0, 0, 0
        };
    }

    /// <summary>Build CLOSE TRACK/SESSION CDB (10 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 Table 29:
    ///   Byte 0:   Operation Code (5Bh)
    ///   Byte 1:   bit 0 = Immed (immediate reporting)
    ///   Byte 2:   bits 2:0 = Close Function
    ///   Byte 3:   Reserved
    ///   Bytes 4-5: Track/Session Number (big-endian)
    ///   Bytes 6-8: Reserved
    ///   Byte 9:   Control
    /// </remarks>
    public static byte[] BuildCloseTrackSession(byte function, ushort trackNumber = 0,
        bool immediate = true)
    {
        return new byte[]
        {
            CloseTrackSession,
            (byte)(immediate ? 0x01 : 0x00),
            (byte)(function & 0x07),
            0,
            (byte)(trackNumber >> 8),
            (byte)(trackNumber & 0xFF),
            0, 0, 0, 0
        };
    }

    /// <summary>Build START STOP UNIT CDB (6 bytes) for eject/load.</summary>
    public static byte[] BuildStartStopUnit(bool start, bool loadEject)
    {
        byte operationByte = 0;
        if (start) operationByte |= 0x01;
        if (loadEject) operationByte |= 0x02;
        return new byte[] { StartStopUnit, 0x01, 0, 0, operationByte, 0 };
    }

    /// <summary>Build SET CD SPEED CDB (12 bytes).</summary>
    public static byte[] BuildSetCdSpeed(ushort readSpeedKBs, ushort writeSpeedKBs)
    {
        return new byte[]
        {
            SetCdSpeed,
            0,
            (byte)(readSpeedKBs >> 8),
            (byte)(readSpeedKBs & 0xFF),
            (byte)(writeSpeedKBs >> 8),
            (byte)(writeSpeedKBs & 0xFF),
            0, 0, 0, 0, 0, 0
        };
    }

    /// <summary>Build SET STREAMING CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 Table 480:
    ///   Byte 0:     Operation Code (B6h)
    ///   Byte 1:     Type (bits 2:0)
    ///   Bytes 2-9:  Reserved
    ///   Bytes 10-11: Parameter List Length (big-endian)
    /// </remarks>
    public static byte[] BuildSetStreaming(ushort parameterListLength = 28)
    {
        return new byte[]
        {
            SetStreaming,
            0, 0, 0, 0, 0, 0, 0, 0, 0,
            (byte)(parameterListLength >> 8),
            (byte)(parameterListLength & 0xFF)
        };
    }

    /// <summary>Build RESERVE TRACK CDB (10 bytes).</summary>
    public static byte[] BuildReserveTrack(uint size)
    {
        return new byte[]
        {
            ReserveTrack,
            0, 0, 0, 0,
            (byte)(size >> 24),
            (byte)(size >> 16),
            (byte)(size >> 8),
            (byte)(size & 0xFF),
            0
        };
    }

    /// <summary>Build MODE SENSE(10) CDB for a specific mode page.</summary>
    public static byte[] BuildModeSense10(byte pageCode, ushort allocationLength = 256)
    {
        return new byte[]
        {
            ModeSense10,
            0x08,  // DBD=1 (disable block descriptors)
            (byte)(pageCode & 0x3F),
            0, 0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build MODE SELECT(10) CDB.</summary>
    public static byte[] BuildModeSelect10(ushort parameterListLength)
    {
        return new byte[]
        {
            ModeSelect10,
            0x10,  // PF=1 (page format)
            0, 0, 0, 0, 0,
            (byte)(parameterListLength >> 8),
            (byte)(parameterListLength & 0xFF),
            0
        };
    }

    /// <summary>Build SEND OPC INFORMATION CDB (10 bytes).</summary>
    public static byte[] BuildSendOpcInfo(bool doOpc = true)
    {
        return new byte[]
        {
            SendOpcInfo,
            (byte)(doOpc ? 0x01 : 0x00),
            0, 0, 0, 0, 0, 0, 0, 0
        };
    }

    /// <summary>Build READ BUFFER CAPACITY CDB (10 bytes).</summary>
    public static byte[] BuildReadBufferCapacity(ushort allocationLength = 12)
    {
        return new byte[]
        {
            ReadBufferCapacity,
            0, 0, 0, 0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    // -----------------------------------------------------------------------
    // Helpers for parsing responses
    // -----------------------------------------------------------------------

    /// <summary>Reads a big-endian 16-bit value from a buffer.</summary>
    public static ushort ReadBE16(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) return 0;
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    /// <summary>Reads a big-endian 32-bit value from a buffer.</summary>
    public static uint ReadBE32(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return 0;
        // Explicitly cast each byte to uint before shifting to avoid signed int promotion
        // issues when the high bit of a byte is set (e.g. data[offset] = 0xFF would produce
        // a negative int after << 24 without the cast).
        return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) | (uint)data[offset + 3];
    }

    /// <summary>Writes a big-endian 16-bit value to a buffer.</summary>
    public static void WriteBE16(byte[] data, int offset, ushort value)
    {
        if (offset + 2 > data.Length) return;
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>Writes a big-endian 32-bit value to a buffer.</summary>
    public static void WriteBE32(byte[] data, int offset, uint value)
    {
        if (offset + 4 > data.Length) return;
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    // -----------------------------------------------------------------------
    // Additional CDB builders
    // -----------------------------------------------------------------------

    /// <summary>Build REQUEST SENSE CDB (6 bytes).</summary>
    public static byte[] BuildRequestSense(byte allocationLength = 18)
        => new byte[] { RequestSense, 0, 0, 0, allocationLength, 0 };

    /// <summary>Build READ CAPACITY CDB (10 bytes).</summary>
    public static byte[] BuildReadCapacity()
        => new byte[] { ReadCapacity, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Build READ FORMAT CAPACITIES CDB (10 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.24, this command returns a list of format descriptors
    /// indicating what format types and capacities the media supports.
    /// The response contains:
    ///   - Capacity List Header (4 bytes): byte 3 = Capacity List Length
    ///   - Current/Maximum Capacity Descriptor (8 bytes)
    ///   - Formattable Capacity Descriptors (8 bytes each)
    /// Each descriptor: bytes 0-3 = Number of Blocks, byte 4 = Descriptor Type (bits 7:2)
    ///   + Format Sub-Type (bits 1:0), bytes 5-7 = Type Dependent Parameter.
    /// </remarks>
    public static byte[] BuildReadFormatCapacities(ushort allocationLength = 252)
    {
        return new byte[]
        {
            ReadFormatCapacities,
            0, 0, 0, 0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build SEND CUE SHEET CDB (10 bytes).</summary>
    public static byte[] BuildSendCueSheet(uint cueSheetLength)
    {
        return new byte[]
        {
            SendCueSheet,
            0, 0, 0, 0, 0,
            (byte)(cueSheetLength >> 16),
            (byte)(cueSheetLength >> 8),
            (byte)(cueSheetLength & 0xFF),
            0
        };
    }

    /// <summary>Build READ DISC STRUCTURE CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 Table 531:
    ///   Byte 0:     Operation Code (0xAD)
    ///   Byte 1:     Media Type (bits 3:0)
    ///   Bytes 2-5:  Address (typically zero)
    ///   Byte 6:     Layer Number
    ///   Byte 7:     Format Code
    ///   Bytes 8-9:  Allocation Length (big-endian)
    ///   Byte 10:    AGID (bits 7:6), reserved
    ///   Byte 11:    Control
    /// </remarks>
    public static byte[] BuildReadDiscStructure(byte mediaType, byte layerNumber,
        byte format, ushort allocationLength)
    {
        return new byte[]
        {
            ReadDiscStructure,                        // [0]  Opcode
            (byte)(mediaType & 0x0F),                 // [1]  Media Type (bits 3:0)
            0, 0, 0, 0,                               // [2-5] Address (typically zero)
            layerNumber,                               // [6]  Layer Number
            format,                                    // [7]  Format Code
            (byte)(allocationLength >> 8),             // [8]  Allocation Length MSB
            (byte)(allocationLength & 0xFF),           // [9]  Allocation Length LSB
            0,                                         // [10] AGID (bits 7:6), reserved
            0                                          // [11] Control
        };
    }

    /// <summary>Build GET PERFORMANCE CDB (12 bytes).</summary>
    public static byte[] BuildGetPerformance(byte type, uint startLba, ushort maxDescriptors)
    {
        return new byte[]
        {
            GetPerformance, // GET PERFORMANCE
            (byte)(type & 0x1F), // Data Type
            (byte)(startLba >> 24),
            (byte)(startLba >> 16),
            (byte)(startLba >> 8),
            (byte)(startLba & 0xFF),
            0, 0,
            (byte)(maxDescriptors >> 8),
            (byte)(maxDescriptors & 0xFF),
            0, 0
        };
    }

    /// <summary>Build MODE SENSE(6) CDB for a specific mode page.</summary>
    public static byte[] BuildModeSense6(byte pageCode, byte allocationLength = 252)
    {
        return new byte[]
        {
            ModeSense6,
            0x08, // DBD=1 (disable block descriptors)
            (byte)(pageCode & 0x3F),
            0,
            allocationLength,
            0
        };
    }

    /// <summary>Build MODE SELECT(6) CDB.</summary>
    public static byte[] BuildModeSelect6(byte parameterListLength)
    {
        return new byte[]
        {
            ModeSelect6,
            0x10, // PF=1 (page format)
            0, 0,
            parameterListLength,
            0
        };
    }

    /// <summary>Build PREVENT/ALLOW MEDIUM REMOVAL CDB (6 bytes).</summary>
    public static byte[] BuildPreventAllowMediumRemoval(bool prevent)
    {
        return new byte[]
        {
            PreventAllowMediumRemoval,
            0, 0, 0,
            (byte)(prevent ? 0x01 : 0x00),
            0
        };
    }

    /// <summary>Build VERIFY(10) CDB for data verification.</summary>
    public static byte[] BuildVerify10(uint lba, ushort verificationLength)
    {
        return new byte[]
        {
            Verify10,
            0x00, // VRPROTECT=0, DPO=0, BYTCHK=0
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            0,     // group number
            (byte)(verificationLength >> 8),
            (byte)(verificationLength & 0xFF),
            0      // control
        };
    }

    /// <summary>Build VERIFY(12) CDB for data verification on larger address spaces.</summary>
    public static byte[] BuildVerify12(uint lba, uint verificationLength)
    {
        return new byte[]
        {
            Verify12,
            0x00, // VRPROTECT=0, DPO=0, BYTCHK=0
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            (byte)(verificationLength >> 24),
            (byte)(verificationLength >> 16),
            (byte)(verificationLength >> 8),
            (byte)(verificationLength & 0xFF),
            0,     // group number
            0      // control
        };
    }

    /// <summary>Build FORMAT UNIT CDB (6 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.3, CDB byte 1 layout:
    ///   bit 4 = FmtData (format parameter list is included in data-out)
    ///   bit 3 = CmpLst (defect list is complete)
    ///   bits 2:0 = Defect List Format (0 for optical media)
    /// NOTE: The Immediate (IMM) bit is NOT in the CDB. It belongs in the
    /// Format Parameter List Header (data byte 1, bit 1).
    /// </remarks>
    public static byte[] BuildFormatUnit(bool fmtData = true, bool cmpLst = false)
    {
        byte flags = 0;
        if (fmtData) flags |= 0x10; // bit 4: FmtData
        if (cmpLst) flags |= 0x08;  // bit 3: CmpLst
        // Per dvd+rw-tools reference implementation and MMC practice:
        // Defect List Format (bits 2:0) must be 001 when FmtData=1 for optical media.
        // Many drives reject Format Code=0 with ASC=0x24 (Invalid field in CDB).
        if (fmtData) flags |= 0x01; // bits 2:0 = 001
        return new byte[]
        {
            FormatUnit,
            flags,
            0, 0, 0, 0
        };
    }

    /// <summary>Build READ SUB-CHANNEL CDB (10 bytes).</summary>
    /// <remarks>
    /// Per MMC-3 §6.1.24 / SFF-8090:
    ///   Byte 0:   Operation Code (42h)
    ///   Byte 1:   bit 1 = MSF (0=LBA, 1=MSF addressing)
    ///   Byte 2:   bit 6 = SubQ (1=return sub-channel data, 0=return header only)
    ///   Byte 3:   Sub-Channel Data Format (01h=current position, 02h=MCN, 03h=ISRC)
    ///   Bytes 4-5: Reserved
    ///   Byte 6:   Track Number (required for ISRC format 03h)
    ///   Bytes 7-8: Allocation Length (big-endian)
    ///   Byte 9:   Control
    /// </remarks>
    public static byte[] BuildReadSubChannel(byte subChannelDataFormat, bool subQ = true,
        bool msf = false, byte trackNumber = 0, ushort allocationLength = 24)
    {
        return new byte[]
        {
            ReadSubChannel,
            (byte)(msf ? 0x02 : 0x00),
            (byte)(subQ ? 0x40 : 0x00),
            subChannelDataFormat,
            0, 0,
            trackNumber,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build REPORT KEY CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.25:
    ///   Byte 0:     Operation Code (A4h)
    ///   Byte 1:     Reserved
    ///   Bytes 2-5:  Reserved (Logical Block Address for some key formats — unused for AACS auth)
    ///   Byte 6:     Reserved
    ///   Byte 7:     Key Class (bits 1:0; 0x00=CSS/CPPM, 0x02=AACS)
    ///   Bytes 8-9:  Allocation Length (big-endian)
    ///   Byte 10:    AGID (bits 7:6), Key Format (bits 5:0)
    ///   Byte 11:    Control
    /// </remarks>
    public static byte[] BuildReportKey(byte keyClass, byte keyFormat,
        byte agid = 0, ushort allocationLength = 8)
    {
        return new byte[]
        {
            ReportKey,
            0, 0, 0, 0, 0, 0,
            (byte)(keyClass & 0x03),
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            (byte)(((agid & 0x03) << 6) | (keyFormat & 0x3F)),
            0
        };
    }

    /// <summary>Build SEND KEY CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.33:
    ///   Byte 0:     Operation Code (A3h)
    ///   Byte 1:     Reserved
    ///   Bytes 2-5:  Reserved
    ///   Byte 6:     Reserved
    ///   Byte 7:     Key Class (bits 1:0; 0x00=CSS/CPPM, 0x02=AACS)
    ///   Bytes 8-9:  Parameter List Length (big-endian)
    ///   Byte 10:    AGID (bits 7:6), Key Format (bits 5:0)
    ///   Byte 11:    Control
    /// </remarks>
    public static byte[] BuildSendKey(byte keyClass, byte keyFormat,
        byte agid = 0, ushort parameterListLength = 0)
    {
        return new byte[]
        {
            SendKey,
            0, 0, 0, 0, 0, 0,
            (byte)(keyClass & 0x03),
            (byte)(parameterListLength >> 8),
            (byte)(parameterListLength & 0xFF),
            (byte)(((agid & 0x03) << 6) | (keyFormat & 0x3F)),
            0
        };
    }

    /// <summary>Build SEND OPC INFORMATION CDB (10 bytes) with OPC table data.</summary>
    /// <remarks>
    /// Per MMC-5 §6.34:
    ///   Byte 0:   Operation Code (54h)
    ///   Byte 1:   bit 0 = DoOpc (1=perform OPC, 0=just send OPC table)
    ///   Bytes 2-6: Reserved
    ///   Bytes 7-8: Parameter List Length (big-endian; 0 when DoOpc=1 with no table)
    ///   Byte 9:   Control
    ///
    /// When DoOpc=false and parameterListLength &gt; 0, the host sends an OPC table
    /// to the drive containing speed/power pairs for optimized laser calibration.
    /// </remarks>
    public static byte[] BuildSendOpcInfoWithData(bool doOpc, ushort parameterListLength)
    {
        return new byte[]
        {
            SendOpcInfo,
            (byte)(doOpc ? 0x01 : 0x00),
            0, 0, 0, 0, 0,
            (byte)(parameterListLength >> 8),
            (byte)(parameterListLength & 0xFF),
            0
        };
    }

    /// <summary>Build READ TRACK INFORMATION CDB (10 bytes) with configurable address type.</summary>
    /// <remarks>
    /// Per MMC-5 §6.27:
    ///   Byte 0:   Operation Code (52h)
    ///   Byte 1:   Address/Number Type (bits 1:0):
    ///     0x00 = LBA (Logical Block Address in bytes 2-5)
    ///     0x01 = Track Number (bytes 2-5)
    ///     0x02 = Session Number (bytes 2-5)
    ///   Bytes 2-5: Logical Track/Session Number or LBA (big-endian, 32-bit)
    ///   Byte 6:   Reserved
    ///   Bytes 7-8: Allocation Length (big-endian)
    ///   Byte 9:   Control
    /// </remarks>
    public static byte[] BuildReadTrackInfoByType(byte addressType, uint addressOrNumber,
        ushort allocationLength = 48)
    {
        return new byte[]
        {
            ReadTrackInfo,
            (byte)(addressType & 0x03),
            (byte)(addressOrNumber >> 24),
            (byte)(addressOrNumber >> 16),
            (byte)(addressOrNumber >> 8),
            (byte)(addressOrNumber & 0xFF),
            0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build RESERVE TRACK CDB (10 bytes) with configurable ARSV bit.</summary>
    /// <remarks>
    /// Per MMC-5 §6.28:
    ///   Byte 0:   Operation Code (53h)
    ///   Byte 1:   bit 0 = ARSV (0=reservation size, 1=logical track number/address for BD-R SRM)
    ///   Bytes 2-4: Reserved
    ///   Bytes 5-8: Reservation Size or Logical Track Number (big-endian, 32-bit)
    ///   Byte 9:   Control
    /// </remarks>
    public static byte[] BuildReserveTrackEx(uint sizeOrTrackNumber, bool arsv = false)
    {
        return new byte[]
        {
            ReserveTrack,
            (byte)(arsv ? 0x01 : 0x00),
            0, 0, 0,
            (byte)(sizeOrTrackNumber >> 24),
            (byte)(sizeOrTrackNumber >> 16),
            (byte)(sizeOrTrackNumber >> 8),
            (byte)(sizeOrTrackNumber & 0xFF),
            0
        };
    }

    /// <summary>Build SEND DISC STRUCTURE CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 Table 497:
    ///   Byte 0:  Operation Code (0xBF)
    ///   Byte 1:  Media Type (bits 3:0)
    ///   Bytes 2-5: Reserved
    ///   Byte 6:  Reserved
    ///   Byte 7:  Format Code
    ///   Bytes 8-9: Parameter List Length (big-endian)
    ///   Byte 10: AGID (bits 7:6), bits 5:0 reserved
    ///   Byte 11: Control
    /// </remarks>
    public static byte[] BuildSendDiscStructure(byte mediaType, byte format,
        ushort parameterListLength)
    {
        return new byte[]
        {
            SendDiscStructure,                        // [0]  Opcode
            (byte)(mediaType & 0x0F),                 // [1]  Media Type (bits 3:0)
            0, 0, 0, 0,                               // [2-5] Reserved
            0,                                         // [6]  Reserved
            format,                                    // [7]  Format Code (per MMC-5 Table 537)
            (byte)(parameterListLength >> 8),          // [8]  Parameter List Length MSB
            (byte)(parameterListLength & 0xFF),        // [9]  Parameter List Length LSB
            0,                                         // [10] AGID (bits 7:6), reserved
            0                                          // [11] Control
        };
    }

    /// <summary>Build READ HEADER CDB (10 bytes).</summary>
    /// <remarks>
    /// Per MMC-3 §6.1.15 / SFF-8090:
    ///   Byte 0:   Operation Code (44h)
    ///   Byte 1:   bit 1 = MSF (0=LBA, 1=MSF addressing)
    ///   Bytes 2-5: Logical Block Address (big-endian, 32-bit)
    ///   Byte 6:   Reserved
    ///   Bytes 7-8: Allocation Length (big-endian)
    ///   Byte 9:   Control
    ///
    /// Returns the data block address header for the specified LBA,
    /// including the data mode (Mode 1 or Mode 2) of the block.
    /// </remarks>
    public static byte[] BuildReadHeader(uint lba, bool msf = false,
        ushort allocationLength = 8)
    {
        return new byte[]
        {
            ReadHeader,
            (byte)(msf ? 0x02 : 0x00),
            (byte)(lba >> 24),
            (byte)(lba >> 16),
            (byte)(lba >> 8),
            (byte)(lba & 0xFF),
            0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build LOG SENSE CDB (10 bytes).</summary>
    /// <remarks>
    /// Per SPC-4 §7.2:
    ///   Byte 0:   Operation Code (4Dh)
    ///   Byte 1:   bit 0 = SP (save parameters), bit 1 = PPC (parameter pointer control)
    ///   Byte 2:   PC (bits 7:6, page control) | Page Code (bits 5:0)
    ///   Byte 3:   Sub-Page Code
    ///   Bytes 4-5: Reserved
    ///   Bytes 6-7: Parameter Pointer (big-endian) — for PPC=1
    ///   Bytes 7-8: Allocation Length (big-endian)
    ///   Byte 9:   Control
    ///
    /// Page Control (PC) values:
    ///   0x00 = Current cumulative values
    ///   0x01 = Current threshold values
    ///   0x02 = Default threshold values
    ///   0x03 = Default cumulative values
    /// </remarks>
    public static byte[] BuildLogSense(byte pageCode, byte subPageCode = 0,
        byte pageControl = 0x00, ushort allocationLength = 512)
    {
        return new byte[]
        {
            LogSense,
            0x00,  // SP=0, PPC=0
            (byte)(((pageControl & 0x03) << 6) | (pageCode & 0x3F)),
            subPageCode,
            0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build GET EVENT STATUS NOTIFICATION CDB (10 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.5:
    ///   Byte 0:   Operation Code (4Ah)
    ///   Byte 1:   bit 0 = Polled (1 = polled, 0 = asynchronous — deprecated)
    ///   Bytes 2-3: Reserved
    ///   Byte 4:   Notification Class Request (bitmask of requested event classes)
    ///   Bytes 5-6: Reserved
    ///   Bytes 7-8: Allocation Length (big-endian)
    ///   Byte 9:   Control
    /// </remarks>
    public static byte[] BuildGetEventStatusNotification(byte notificationClassRequest,
        ushort allocationLength = 8)
    {
        return new byte[]
        {
            GetEventStatus,
            0x01,  // Polled = 1
            0, 0,
            notificationClassRequest,
            0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0
        };
    }

    /// <summary>Build MECHANISM STATUS CDB (12 bytes).</summary>
    /// <remarks>
    /// Per MMC-5 §6.12:
    ///   Byte 0:    Operation Code (BDh)
    ///   Bytes 1-7: Reserved
    ///   Bytes 8-9: Allocation Length (big-endian)
    ///   Bytes 10-11: Reserved/Control
    ///
    /// Returns mechanical status information including:
    ///   - Current slot (for changers)
    ///   - Changer mechanism state (ready, loading, unloading, initializing)
    ///   - Door open/closed status
    ///   - Current LBA of optical pickup
    /// </remarks>
    public static byte[] BuildMechanismStatus(ushort allocationLength = 8)
    {
        return new byte[]
        {
            MechanismStatus,
            0, 0, 0, 0, 0, 0, 0,
            (byte)(allocationLength >> 8),
            (byte)(allocationLength & 0xFF),
            0, 0
        };
    }
}
