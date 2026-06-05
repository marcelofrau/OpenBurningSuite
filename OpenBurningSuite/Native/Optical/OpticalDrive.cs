// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Text;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Optical;

/// <summary>
/// High-level abstraction for an optical drive, providing common operations
/// built on top of SCSI/MMC commands.
/// </summary>
public sealed class OpticalDrive : IDisposable
{
    private readonly IScsiTransport _transport;

    public string DevicePath { get; }

    public OpticalDrive(string devicePath)
    {
        DevicePath = devicePath;
        _transport = ScsiTransportFactory.Create(devicePath);
    }

    /// <summary>
    /// Creates an OpticalDrive with an externally-provided SCSI transport.
    /// Used for testing with mock transports and for advanced scenarios where
    /// the caller manages the transport lifecycle.
    /// </summary>
    internal OpticalDrive(string devicePath, IScsiTransport transport)
    {
        DevicePath = devicePath;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public void Dispose()
    {
        _transport.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Executes a raw SCSI command. For advanced use by platform-specific code.</summary>
    public ScsiResult ExecuteRaw(ScsiCommand command) => _transport.Execute(command);

    /// <summary>
    /// Prepares the drive for a destructive operation (BLANK, FORMAT UNIT).
    /// On Windows, this locks and dismounts the volume so the storage filter driver
    /// doesn't block SCSI passthrough commands on mounted optical media.
    /// Must be called before any FORMAT UNIT or BLANK command.
    /// </summary>
    public bool PrepareForDestructiveOperation()
    {
        return _transport.PrepareForWrite();
    }

    /// <summary>Tests whether the drive is ready.</summary>
    public bool TestUnitReady()
    {
        var cmd = new ScsiCommand(MmcCommands.BuildTestUnitReady(), ScsiDataDirection.None)
        {
            TimeoutMs = 5000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Polls the drive with TEST UNIT READY until it becomes ready or the timeout expires.
    /// Optical drives need time to spin up and recognize media after tray close/load
    /// (typically 2-15 seconds). This avoids premature "Drive is not ready" failures.
    /// </summary>
    /// <param name="maxWaitMs">Maximum time to wait in milliseconds (default: 30 seconds).</param>
    /// <param name="pollIntervalMs">Interval between polls in milliseconds (default: 500ms).</param>
    /// <returns>True if the drive became ready within the timeout, false otherwise.</returns>
    public bool WaitForDriveReady(int maxWaitMs = 30_000, int pollIntervalMs = 500)
    {
        if (TestUnitReady())
            return true;

        var deadline = Environment.TickCount64 + maxWaitMs;

        // First, issue a quick retry immediately — the first TUR after opening a handle
        // often fails with UNIT ATTENTION (sense key 0x06, ASC 0x28/0x29) which clears
        // after the first command. A second immediate TUR avoids a needless 500ms wait.
        if (TestUnitReady())
            return true;

        while (Environment.TickCount64 < deadline)
        {
            System.Threading.Thread.Sleep(pollIntervalMs);
            if (TestUnitReady())
                return true;
        }

        // TEST UNIT READY polling exhausted — try alternative methods.
        // Some drives (especially USB-attached or virtual) may not respond to TUR
        // via SCSI passthrough but still respond to other commands.

        // Fallback 1: GET EVENT STATUS NOTIFICATION (media event class)
        var mediaPresent = GetMediaPresent();
        if (mediaPresent == true)
            return true;

        // Fallback 2: GET CONFIGURATION (current profile) — a non-zero profile
        // indicates media is present and the drive is responsive.
        var profile = GetCurrentProfile();
        if (profile != 0)
            return true;

        return false;
    }

    /// <summary>Gets drive vendor, product, and revision from INQUIRY data.</summary>
    public (string Vendor, string Product, string Revision) Inquiry()
    {
        var cmd = new ScsiCommand(MmcCommands.BuildInquiry(96), ScsiDataDirection.In, 96);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 36)
            return ("Unknown", "Unknown", "");

        var data = cmd.DataBuffer;
        var vendor = Encoding.ASCII.GetString(data, 8, 8).Trim();
        var product = Encoding.ASCII.GetString(data, 16, 16).Trim();
        var revision = Encoding.ASCII.GetString(data, 32, 4).Trim();
        return (vendor, product, revision);
    }

    /// <summary>
    /// Retrieves the drive serial number via INQUIRY VPD page 0x80
    /// (Unit Serial Number). Returns null if the drive doesn't support
    /// this VPD page or if the command fails.
    /// </summary>
    /// <remarks>SPC-4 §7.8.1: VPD page 0x80 response layout:
    /// Bytes 0: Peripheral qualifier + device type
    /// Byte 1: Page code (0x80)
    /// Byte 3: Page length
    /// Bytes 4+: Product serial number (ASCII)
    /// </remarks>
    public string? InquirySerialNumber()
    {
        try
        {
            var cmd = new ScsiCommand(
                MmcCommands.BuildInquiryVpd(0x80, 252),
                ScsiDataDirection.In, 252);
            var result = _transport.Execute(cmd);
            if (!result.Success || result.DataTransferred < 4)
                return null;

            var data = cmd.DataBuffer;
            // Verify page code matches
            if (data[1] != 0x80)
                return null;

            int pageLength = data[3];
            if (pageLength <= 0 || result.DataTransferred < 4 + pageLength)
                return null;

            var serial = Encoding.ASCII.GetString(data, 4, pageLength).Trim();
            return string.IsNullOrWhiteSpace(serial) ? null : serial;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads disc information (disc status, state, sessions, tracks).</summary>
    public DiscInfoData? ReadDiscInfo()
    {
        var cmd = new ScsiCommand(MmcCommands.BuildReadDiscInfo(34), ScsiDataDirection.In, 34);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 12)
            return null;

        var data = cmd.DataBuffer;
        return new DiscInfoData
        {
            DiscStatus = (byte)(data[2] & 0x03),
            LastSessionState = (byte)((data[2] >> 2) & 0x03),
            Erasable = (data[2] & 0x10) != 0,
            NumberOfFirstTrack = data[3],
            NumberOfSessionsLsb = data[4],
            FirstTrackInLastSessionLsb = data[5],
            LastTrackInLastSessionLsb = data[6],
            DiscType = data[8],
        };
    }

    /// <summary>Reads track information for a given track number.</summary>
    public TrackInfoData? ReadTrackInfo(uint trackNumber)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadTrackInfo(trackNumber, 48),
            ScsiDataDirection.In, 48);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 28)
            return null;

        var data = cmd.DataBuffer;
        // Per MMC-5 READ TRACK INFORMATION response:
        //   Bytes 0-1: Data Length
        //   Byte 2:    Track Number LSB
        //   Byte 3:    Session Number LSB
        //   Byte 5:    bit 5 = Damage, bit 4 = Copy, bits 3:0 = Track Mode
        //   Byte 6:    bit 5 = RT (Reservation Type), bits 3:0 = Data Mode
        //   Byte 7:    bit 1 = LRA_V (Last Recorded Address Valid), bit 0 = NWA_V
        //   Bytes 8-11:  Track Start Address
        //   Bytes 12-15: Next Writable Address
        //   Bytes 16-19: Free Blocks
        //   Bytes 20-23: Fixed Packet Size / Blocking Factor
        //   Bytes 24-27: Track Size
        //   Bytes 28-31: Last Recorded Address
        //   Byte 32:   Track Number MSB (if data length >= 33)
        //   Byte 33:   Session Number MSB (if data length >= 34)
        var trackNumLsb = data[2];
        var sessionNumLsb = data[3];
        // Per MMC-5: Track Number MSB at byte 32, Session Number MSB at byte 33
        // combine LSB and MSB for full 16-bit values when response is long enough
        ushort trackNum = trackNumLsb;
        ushort sessionNum = sessionNumLsb;
        if (result.DataTransferred >= 34)
        {
            trackNum = (ushort)((data[32] << 8) | trackNumLsb);
            sessionNum = (ushort)((data[33] << 8) | sessionNumLsb);
        }
        else if (result.DataTransferred >= 33)
        {
            // Have byte 32 (Track Number MSB) but not byte 33 (Session Number MSB)
            trackNum = (ushort)((data[32] << 8) | trackNumLsb);
        }

        // Parse Last Recorded Address (LRA) — bytes 28-31, valid only if LRA_V (byte 7 bit 1) is set
        bool lraValid = (data[7] & 0x02) != 0;
        uint lastRecordedAddress = 0;
        if (lraValid && result.DataTransferred >= 32)
            lastRecordedAddress = MmcCommands.ReadBE32(data, 28);

        return new TrackInfoData
        {
            TrackNumber = trackNum,
            SessionNumber = sessionNum,
            TrackMode = (byte)(data[5] & 0x0F),
            DataMode = (byte)(data[6] & 0x0F),
            TrackStartAddress = MmcCommands.ReadBE32(data, 8),
            NextWritableAddress = MmcCommands.ReadBE32(data, 12),
            FreeBlocks = MmcCommands.ReadBE32(data, 16),
            FixedPacketSize = MmcCommands.ReadBE32(data, 20),
            TrackSize = MmcCommands.ReadBE32(data, 24),
            NwaValid = (data[7] & 0x01) != 0,
            LraValid = lraValid,
            LastRecordedAddress = lastRecordedAddress,
            Damage = (data[5] & 0x20) != 0,
            Copy = (data[5] & 0x10) != 0,
            ReservationTrack = (data[6] & 0x20) != 0,
        };
    }

    /// <summary>Reads the Table of Contents.</summary>
    public TocData? ReadToc()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadToc(0, 1, 2048),
            ScsiDataDirection.In, 2048);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        var tocLength = MmcCommands.ReadBE16(data, 0);
        var firstTrack = data[2];
        var lastTrack = data[3];

        var toc = new TocData
        {
            FirstTrack = firstTrack,
            LastTrack = lastTrack
        };

        // Parse track descriptors (8 bytes each, starting at offset 4)
        // Per MMC-5 §6.26, the TOC Data Length field (bytes 0-1) gives the number of
        // bytes following those 2 bytes. So total valid response = 2 + tocLength.
        // Use this to bound parsing instead of relying solely on DataTransferred,
        // which may include padding or extra data beyond the actual TOC.
        // Also clamp to the actual data buffer length to prevent out-of-bounds reads.
        int tocDataEnd = Math.Min(2 + tocLength, result.DataTransferred);
        tocDataEnd = Math.Min(tocDataEnd, data.Length);
        for (int offset = 4; offset + 8 <= tocDataEnd; offset += 8)
        {
            var entry = new TocEntry
            {
                SessionNumber = data[offset],
                Control = (byte)(data[offset + 1] & 0x0F),
                Adr = (byte)((data[offset + 1] >> 4) & 0x0F),
                TrackNumber = data[offset + 2],
                StartLba = MmcCommands.ReadBE32(data, offset + 4)
            };
            toc.Entries.Add(entry);
        }

        return toc;
    }

    /// <summary>
    /// Sets the write speed in KB/s. Uses SET STREAMING for DVD/BD media (preferred
    /// per MMC-5) and SET CD SPEED for CD media.
    /// Use 0xFFFF for maximum speed.
    /// </summary>
    public bool SetWriteSpeed(ushort writeSpeedKBs, bool isDvdOrBd = false)
    {
        if (isDvdOrBd)
        {
            // For DVD/BD media, try SET STREAMING first (preferred per MMC-5 §6.31).
            // SET STREAMING provides more precise speed control and is mandatory for BD.
            if (SetStreamingSpeed(writeSpeedKBs))
                return true;
        }

        // Try SET CD SPEED (simpler, works for CD and many DVD drives as fallback)
        var cmd = new ScsiCommand(
            MmcCommands.BuildSetCdSpeed(0xFFFF, writeSpeedKBs),
            ScsiDataDirection.None)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (result.Success) return true;

        // For CD media that rejected SET CD SPEED, try SET STREAMING as last resort
        if (!isDvdOrBd)
            return SetStreamingSpeed(writeSpeedKBs);

        return false;
    }

    /// <summary>
    /// Sets read/write speed via SET STREAMING command (preferred for DVD/BD).
    /// The Performance Descriptor specifies read and write speeds as
    /// "kilobytes per 1000 ms" (i.e. KB/s directly, not bytes/s).
    /// </summary>
    public bool SetStreamingSpeed(ushort speedKBs)
    {
        // SET STREAMING Performance Descriptor (28 bytes):
        //   Bytes 0-3: Byte 0 bits 2:0 = WRC (write rotation control), bit 3 = RDD
        //   Bytes 4-7: Start LBA (0 for entire disc)
        //   Bytes 8-11: End LBA (0xFFFFFFFF for entire disc)
        //   Bytes 12-15: Read Size (kilobytes per Read Time period)
        //   Bytes 16-19: Read Time (ms, typically 1000)
        //   Bytes 20-23: Write Size (kilobytes per Write Time period)
        //   Bytes 24-27: Write Time (ms, typically 1000)
        var descriptor = new byte[28];

        // End LBA = 0xFFFFFFFF (entire disc)
        MmcCommands.WriteBE32(descriptor, 8, 0xFFFFFFFF);

        // Read speed = max (0xFFFFFFFF KB per 1000ms)
        MmcCommands.WriteBE32(descriptor, 12, 0xFFFFFFFF);

        // Read time = 1000 ms
        MmcCommands.WriteBE32(descriptor, 16, 0x000003E8);

        // Write speed: KB per 1000ms = KB/s directly
        uint writeSpeed = speedKBs == 0xFFFF ? 0xFFFFFFFFu : (uint)speedKBs;
        MmcCommands.WriteBE32(descriptor, 20, writeSpeed);

        // Write time = 1000 ms
        MmcCommands.WriteBE32(descriptor, 24, 0x000003E8);

        var cmd = new ScsiCommand(
            MmcCommands.BuildSetStreaming(28),
            ScsiDataDirection.Out, descriptor)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Reads a mode page via MODE SENSE(10) and locates a specific page within the response.
    /// Returns (pageData, headerLen, pageOffset) or null if the page was not found.
    /// The returned pageOffset is the index into pageData where the requested page begins.
    /// </summary>
    private (byte[] PageData, int HeaderLen, int PageOffset)? ReadModePage(
        byte pageCode, int minPageBytes = 3)
    {
        var senseCmd = new ScsiCommand(
            MmcCommands.BuildModeSense10(pageCode, 256),
            ScsiDataDirection.In, 256)
        {
            TimeoutMs = 10_000
        };
        var senseResult = _transport.Execute(senseCmd);
        if (!senseResult.Success)
            return null;

        var pageData = senseCmd.DataBuffer;
        var headerLen = MmcCommands.ReadBE16(pageData, 0) + 2;
        headerLen = Math.Min(headerLen, pageData.Length);
        // MODE SENSE(10) header is 8 bytes; must have at least header + 2-byte page header
        if (headerLen < 10) return null;

        // Mode data header is 8 bytes for MODE SENSE(10)
        // Search for the requested page code in the returned data
        int pageOffset = 8;
        int dataLen = Math.Min(headerLen, pageData.Length);
        while (pageOffset + 2 <= dataLen)
        {
            if ((pageData[pageOffset] & 0x3F) == pageCode)
                break;
            var pageLen = pageData[pageOffset + 1] + 2;
            if (pageLen < 4) break; // Malformed page — minimum valid page is 2-byte header + some data
            // Ensure the next page offset doesn't exceed our data bounds
            if (pageOffset + pageLen > dataLen) break;
            pageOffset += pageLen;
        }

        // Verify we found the page and have enough data
        if (pageOffset + minPageBytes > dataLen)
            return null;

        return (pageData, headerLen, pageOffset);
    }

    /// <summary>
    /// Sends modified mode page data back to the drive via MODE SELECT(10).
    /// </summary>
    private bool SendModeSelect(byte[] pageData, int headerLen, int pageOffset)
    {
        // Clear PS bit (bit 7) for MODE SELECT per SPC-4.
        // Preserve SPF bit (bit 6) and page code bits (5:0).
        pageData[pageOffset] &= 0x7F;

        // Build MODE SELECT(10) parameter list from the full MODE SENSE(10) response.
        // Per SPC-5, the MODE SELECT(10) parameter list has the same layout as the
        // MODE SENSE(10) response: 8-byte header + block descriptors + mode pages.
        // The Mode Data Length field (bytes 0-1) is reserved and must be zero.
        // Medium Type (byte 2) and Device-Specific Parameter (byte 3) must also be zero.
        var selectData = new byte[headerLen];
        Array.Copy(pageData, 0, selectData, 0, Math.Min(headerLen, pageData.Length));

        // Zero out header fields per SPC-5 §7.5.9 (MODE SELECT):
        selectData[0] = 0; // Mode Data Length MSB (reserved for MODE SELECT)
        selectData[1] = 0; // Mode Data Length LSB (reserved for MODE SELECT)
        selectData[2] = 0; // Medium Type
        selectData[3] = 0; // Device-Specific Parameter

        var selectCmd = new ScsiCommand(
            MmcCommands.BuildModeSelect10((ushort)selectData.Length),
            ScsiDataDirection.Out, selectData)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(selectCmd).Success;
    }

    /// <summary>Sets write parameters mode page for TAO/SAO/RAW writing.</summary>
    /// <param name="writeType">Write type (TAO=0x01, SAO=0x02, RAW=0x03).</param>
    /// <param name="trackMode">Track mode (Audio=0x00, Data=0x04).</param>
    /// <param name="dataBlockType">Data block type (Raw2352=0x00, Mode1_2048=0x08, etc.).</param>
    /// <param name="burnFree">Enable buffer underrun protection.</param>
    /// <param name="sessionFormat">Session format: 0x00=CD-DA/CD-ROM, 0x10=CDI, 0x20=CD-ROM XA.
    /// Pass 0xFF to leave unchanged (uses drive's current setting).</param>
    public bool SetWriteParameters(byte writeType, byte trackMode, byte dataBlockType,
        bool burnFree = true, byte sessionFormat = 0xFF)
    {
        // Try MODE SENSE(10) / MODE SELECT(10) first — this is the standard for MMC drives.
        var modePage = ReadModePage(MmcCommands.WriteParametersPage, minPageBytes: 5);
        if (modePage != null)
        {
            var (pageData, headerLen, pageOffset) = modePage.Value;
            if (pageOffset + 5 <= pageData.Length)
            {
                ApplyWriteParameterFields(pageData, pageOffset, writeType, trackMode,
                    dataBlockType, burnFree, sessionFormat);

                if (SendModeSelect(pageData, headerLen, pageOffset))
                    return true;
            }
        }

        // Fallback: try MODE SENSE(6) / MODE SELECT(6).
        // Some drives (especially USB-connected or older models) only support the
        // 6-byte mode commands and reject MODE SENSE(10) / MODE SELECT(10).
        // Without this fallback, SetWriteParameters fails and SEND CUE SHEET is
        // rejected with "Command sequence error" (ASC=0x2C) because write parameters
        // don't match the CUE sheet content.
        var modePage6 = ReadModePage6(MmcCommands.WriteParametersPage, minPageBytes: 5);
        if (modePage6 != null)
        {
            var (pageData6, headerLen6, pageOffset6) = modePage6.Value;
            if (pageOffset6 + 5 <= pageData6.Length)
            {
                ApplyWriteParameterFields(pageData6, pageOffset6, writeType, trackMode,
                    dataBlockType, burnFree, sessionFormat);

                if (SendModeSelect6(pageData6, headerLen6, pageOffset6))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies write parameter field modifications to a mode page 05h buffer.
    /// Shared by both MODE SENSE(10) and MODE SENSE(6) paths.
    /// </summary>
    private static void ApplyWriteParameterFields(byte[] pageData, int pageOffset,
        byte writeType, byte trackMode, byte dataBlockType, bool burnFree, byte sessionFormat)
    {
        // Byte 2: bits 3:0 = Write Type, bit 6 = BUFE (buffer underrun free enable)
        // Clear both Write Type and BUFE bits, then set desired values
        pageData[pageOffset + 2] = (byte)((pageData[pageOffset + 2] & 0xB0) | (writeType & 0x0F));
        pageData[pageOffset + 3] = (byte)((pageData[pageOffset + 3] & 0xF0) | (trackMode & 0x0F));
        pageData[pageOffset + 4] = (byte)((pageData[pageOffset + 4] & 0xF0) | (dataBlockType & 0x0F));

        // BURNFREE (buffer underrun protection) — BUFE bit 6 of byte 2 of page
        if (burnFree)
            pageData[pageOffset + 2] |= 0x40;
        else
            pageData[pageOffset + 2] &= unchecked((byte)~0x40);

        // Session Format (byte 8 of mode page 05h):
        // 0x00 = CD-DA or CD-ROM (Mode 1)
        // 0x10 = CDI
        // 0x20 = CD-ROM XA (Mode 2)
        // This MUST match the CUE sheet's track data forms. A mismatch causes drives
        // to reject the SEND CUE SHEET command with "Command sequence error" (ASC=0x2C).
        if (sessionFormat != 0xFF && pageOffset + 9 <= pageData.Length)
        {
            pageData[pageOffset + 8] = sessionFormat;
        }
    }

    /// <summary>
    /// Reads a mode page via MODE SENSE(6) and locates a specific page within the response.
    /// MODE SENSE(6) has a 4-byte header (vs 8 bytes for MODE SENSE(10)).
    /// Used as fallback when MODE SENSE(10) is not supported by the drive.
    /// </summary>
    private (byte[] PageData, int HeaderLen, int PageOffset)? ReadModePage6(
        byte pageCode, int minPageBytes = 3)
    {
        var senseCmd = new ScsiCommand(
            MmcCommands.BuildModeSense6(pageCode, 252),
            ScsiDataDirection.In, 252)
        {
            TimeoutMs = 10_000
        };
        var senseResult = _transport.Execute(senseCmd);
        if (!senseResult.Success)
            return null;

        var pageData = senseCmd.DataBuffer;
        // MODE SENSE(6) header: byte 0 = Mode Data Length (single byte, excludes itself)
        var headerLen = pageData[0] + 1;
        headerLen = Math.Min(headerLen, pageData.Length);
        // MODE SENSE(6) header is 4 bytes; must have at least header + 2-byte page header
        if (headerLen < 6) return null;

        // Mode data header is 4 bytes for MODE SENSE(6)
        int pageOffset = 4;
        int dataLen = Math.Min(headerLen, pageData.Length);
        while (pageOffset + 2 <= dataLen)
        {
            if ((pageData[pageOffset] & 0x3F) == pageCode)
                break;
            var pageLen = pageData[pageOffset + 1] + 2;
            if (pageLen < 4) break;
            if (pageOffset + pageLen > dataLen) break;
            pageOffset += pageLen;
        }

        if (pageOffset + minPageBytes > dataLen)
            return null;

        return (pageData, headerLen, pageOffset);
    }

    /// <summary>
    /// Sends modified mode page data back to the drive via MODE SELECT(6).
    /// MODE SELECT(6) has a 4-byte header (vs 8 bytes for MODE SELECT(10)).
    /// Used as fallback when MODE SELECT(10) is not supported by the drive.
    /// </summary>
    private bool SendModeSelect6(byte[] pageData, int headerLen, int pageOffset)
    {
        // Clear PS bit (bit 7) for MODE SELECT per SPC-4.
        pageData[pageOffset] &= 0x7F;

        // Build MODE SELECT(6) parameter list from the full MODE SENSE(6) response.
        // Per SPC-5, the MODE SELECT(6) parameter list has the same layout as the
        // MODE SENSE(6) response: 4-byte header + block descriptors + mode pages.
        // The Mode Data Length field (byte 0) is reserved and must be zero.
        // Medium Type (byte 1) and Device-Specific Parameter (byte 2) must also be zero.
        var selectData = new byte[headerLen];
        Array.Copy(pageData, 0, selectData, 0, Math.Min(headerLen, pageData.Length));

        // Zero out header fields per SPC-5 §7.5.8 (MODE SELECT(6)):
        selectData[0] = 0; // Mode Data Length (reserved for MODE SELECT)
        selectData[1] = 0; // Medium Type
        selectData[2] = 0; // Device-Specific Parameter
        // selectData[3] = Block Descriptor Length — keep as-is from MODE SENSE

        if (selectData.Length > byte.MaxValue) return false;

        var selectCmd = new ScsiCommand(
            MmcCommands.BuildModeSelect6((byte)selectData.Length),
            ScsiDataDirection.Out, selectData)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(selectCmd).Success;
    }

    /// <summary>Performs Optimal Power Calibration.</summary>
    public bool PerformOpc()
    {
        var cmd = new ScsiCommand(MmcCommands.BuildSendOpcInfo(true), ScsiDataDirection.None)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Reads the format capacities from the drive using READ FORMAT CAPACITIES (0x23).
    /// Returns a list of format descriptors that the drive/media combination supports.
    /// Each descriptor contains the number of blocks, format type, and type-dependent parameter.
    /// This MUST be called before FORMAT UNIT to obtain the correct descriptor values.
    /// </summary>
    public FormatCapacityDescriptor[]? ReadFormatCapacities()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadFormatCapacities(252),
            ScsiDataDirection.In, 252)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        // Capacity List Header: byte 3 = Capacity List Length (bytes of descriptor data following header)
        var listLength = data[3];
        if (listLength < 8 || result.DataTransferred < 4 + listLength)
            return null;

        var descriptorCount = listLength / 8;
        var descriptors = new FormatCapacityDescriptor[descriptorCount];

        for (int i = 0; i < descriptorCount; i++)
        {
            int offset = 4 + (i * 8);
            // Bytes 0-3: Number of Blocks (big-endian)
            var numBlocks = MmcCommands.ReadBE32(data, offset);
            // Byte 4: bits 7:2 = Format Type (pre-shifted), bits 1:0 = Format Sub-Type
            // For the first descriptor (current/maximum capacity), bits 7:6 of byte 4
            // indicate Descriptor Type (0b01=unformatted, 0b10=formatted, 0b11=no media).
            // For formattable descriptors (i > 0), the full byte is Format Type + Sub-Type.
            var descriptorByte4 = data[offset + 4];
            // Bytes 5-7: Type Dependent Parameter (big-endian, 3 bytes)
            uint typeDepParam = ((uint)data[offset + 5] << 16) |
                                ((uint)data[offset + 6] << 8) |
                                (uint)data[offset + 7];

            descriptors[i] = new FormatCapacityDescriptor
            {
                NumberOfBlocks = numBlocks,
                FormatTypeByte = descriptorByte4,
                TypeDependentParameter = typeDepParam,
                IsCurrentCapacity = (i == 0)
            };
        }

        return descriptors;
    }

    /// <summary>
    /// Finds a matching format descriptor for the given format type byte from READ FORMAT CAPACITIES.
    /// The format type byte contains the format type in bits 7:2 (pre-shifted) and sub-type in bits 1:0.
    /// Skips the first descriptor (current/maximum capacity) and searches formattable descriptors.
    /// </summary>
    private static FormatCapacityDescriptor? FindFormatDescriptor(
        FormatCapacityDescriptor[] descriptors, byte formatTypeByte)
    {
        // Search formattable descriptors (skip first = current capacity descriptor)
        // Match on format type bits 7:2 only, ignoring sub-type bits 1:0
        byte targetFormatType = (byte)(formatTypeByte & 0xFC);
        for (int i = 1; i < descriptors.Length; i++)
        {
            if ((descriptors[i].FormatTypeByte & 0xFC) == targetFormatType)
                return descriptors[i];
        }

        return null;
    }

    /// <summary>
    /// Populates FORMAT UNIT data (bytes 4-11) from READ FORMAT CAPACITIES descriptors.
    /// Tries each format type byte in <paramref name="formatTypeCandidates"/> in order.
    /// If no matching formattable descriptor is found, falls back to the current capacity
    /// descriptor's NumberOfBlocks to avoid sending zeros (which many drives reject with
    /// "Invalid field in CDB" / ASC=0x24 or "Invalid field in parameter list" / ASC=0x26).
    /// </summary>
    /// <param name="capacityFallbackBlocks">Optional fallback from READ CAPACITY (LastLba + 1)
    /// used when READ FORMAT CAPACITIES returns null or has no usable descriptors.
    /// Prevents sending zero NumberOfBlocks which strict drives reject.</param>
    /// <returns>The format type byte to use in descriptor byte 8.</returns>
    private static byte PopulateFormatDescriptor(
        FormatCapacityDescriptor[]? descriptors,
        byte[] formatData,
        uint capacityFallbackBlocks,
        params byte[] formatTypeCandidates)
    {
        byte formatTypeByte = formatTypeCandidates[0];

        if (descriptors != null)
        {
            // Try each candidate format type in priority order
            FormatCapacityDescriptor? match = null;
            foreach (var candidate in formatTypeCandidates)
            {
                match = FindFormatDescriptor(descriptors, candidate);
                if (match != null)
                {
                    formatTypeByte = candidate;
                    break;
                }
            }

            if (match != null)
            {
                MmcCommands.WriteBE32(formatData, 4, match.NumberOfBlocks);
                formatTypeByte = match.FormatTypeByte;
                formatData[9] = (byte)(match.TypeDependentParameter >> 16);
                formatData[10] = (byte)(match.TypeDependentParameter >> 8);
                formatData[11] = (byte)(match.TypeDependentParameter & 0xFF);
            }
            else if (descriptors.Length > 0 && descriptors[0].NumberOfBlocks > 0)
            {
                // Fallback: use current capacity descriptor's NumberOfBlocks only.
                // The first descriptor (current/maximum capacity) has a DIFFERENT byte 4
                // encoding than formattable descriptors: its bytes 5-7 contain the
                // Block Length (e.g. 2048 for DVD/BD), NOT the Type Dependent Parameter.
                // Using Block Length as TDP would send an incorrect spare area size to
                // the drive, causing "Invalid field in CDB" (ASC=0x24) or "Invalid field
                // in parameter list" (ASC=0x26). Leave TDP at zeros and let the drive
                // use its default spare area configuration.
                MmcCommands.WriteBE32(formatData, 4, descriptors[0].NumberOfBlocks);
                // formatData[9..11] left at 0x000000 — drive uses default TDP
            }
            else if (capacityFallbackBlocks > 0)
            {
                // Fallback: use READ CAPACITY result when format descriptors have zero blocks
                MmcCommands.WriteBE32(formatData, 4, capacityFallbackBlocks);
            }
        }
        else if (capacityFallbackBlocks > 0)
        {
            // READ FORMAT CAPACITIES failed entirely — use READ CAPACITY fallback
            MmcCommands.WriteBE32(formatData, 4, capacityFallbackBlocks);
        }

        formatData[8] = formatTypeByte;
        return formatTypeByte;
    }

    /// <summary>
    /// Gets fallback block count from READ CAPACITY for use when READ FORMAT CAPACITIES fails.
    /// Returns LastLba + 1 as the total number of blocks, or 0 if READ CAPACITY also fails.
    /// </summary>
    private uint GetCapacityFallbackBlocks()
    {
        var cap = ReadCapacity();
        if (cap.HasValue && cap.Value.LastLba > 0)
            // Guard against uint overflow: if LastLba is uint.MaxValue, adding 1 wraps to 0.
            // Cap at uint.MaxValue to prevent sending zero blocks to FORMAT UNIT.
            return cap.Value.LastLba < uint.MaxValue
                ? cap.Value.LastLba + 1
                : uint.MaxValue;
        return 0;
    }

    /// <summary>
    /// Formats a DVD+RW disc using FORMAT UNIT command.
    /// DVD+RW does not support the BLANK command; FORMAT UNIT must be used instead.
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatDvdPlusRw(bool quickFormat)
    {
        // Prepare for destructive operation: on Windows, this locks and dismounts
        // the mounted volume so the storage filter driver allows FORMAT UNIT through.
        PrepareForDestructiveOperation();

        // Clear any pending UNIT ATTENTION conditions and ensure disc is spinning
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        // Read format capacities to get the correct descriptor values
        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        formatData[0] = 0x00; // reserved
        // Per dvd+rw-tools: DVD+RW uses IMMED only (no FOV, no DCRT).
        // The drive uses its default format options with FOV=0.
        formatData[1] = 0x02; // Immed=1 (bit 1) only
        formatData[2] = 0x00;
        formatData[3] = 0x08; // descriptor length = 8

        // Per dvd+rw-tools: DVD+RW format type selection depends on quick vs full:
        //   Quick format: Use format type 0x26 (Background Format Restart) as primary —
        //     this restarts background formatting, effectively performing a quick erase
        //     by resetting the format state. Falls back to 0x00 (Full Format).
        //   Full format: Use format type 0x00 (Full Format) as primary — this performs
        //     a complete reformat with full defect management initialization.
        //     Falls back to 0x26 (Background Format Restart).
        if (quickFormat)
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdPlusRwRestart, MmcCommands.FormatDvdPlusRwFull);
        else
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdPlusRwFull, MmcCommands.FormatDvdPlusRwRestart);

        // FORMAT UNIT CDB is 6 bytes per SPC/MMC spec
        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: false);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full DVD+RW format (IMM=1 but timeout must cover initial phase)
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02), the drive may be
        // temporarily unavailable after PrepareForWrite unmounted/reclaimed the volume.
        // This is common on macOS where DA unmount causes a brief transition state.
        // Wait for the drive to stabilize, then retry.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT with IMM=1 fails, retry with IMM=0 (synchronous).
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = 0x00; // IMM=0
            result = _transport.Execute(cmd);
        }

        // Fallback: try FORMAT UNIT with FOV=0 and original descriptor order.
        if (!result.Success && result.SenseKey == 0x05)
        {
            var minimalData = new byte[4];
            minimalData[1] = 0x02; // FOV=0, Immed=1 only
            // descriptor length = 0
            var minimalCmd = new ScsiCommand(cdb, ScsiDataDirection.Out, minimalData)
            {
                TimeoutMs = 3_600_000
            };
            result = _transport.Execute(minimalCmd);
        }

        return result;
    }

    /// <summary>Writes sectors to the disc at the given LBA.</summary>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="data">Data buffer to write.</param>
    /// <param name="sectorSize">Size of each sector in bytes.</param>
    /// <param name="sectorCount">Number of sectors to write.</param>
    /// <param name="dataLength">Optional explicit data length (-1 to use data.Length).</param>
    /// <param name="useDvdBdMode">
    /// When true, uses WRITE(12) which has a 32-bit transfer length field
    /// suitable for DVD/Blu-ray media. When false, uses WRITE(10) with a
    /// 16-bit transfer length field for standard Audio/Data CDs.
    /// Per MMC-5/SBC-4, WRITE(12) (opcode AAh) is preferred for DVD and Blu-ray
    /// because its 32-bit transfer length allows larger contiguous writes,
    /// reducing SCSI command overhead on high-speed media. WRITE(10) (opcode 2Ah)
    /// remains the standard for CD burning where sector counts per command are small.
    /// </param>
    public ScsiResult WriteSectors(uint lba, byte[] data, int sectorSize, int sectorCount,
        int dataLength = -1, bool useDvdBdMode = false, bool simulate = false)
    {
        if (useDvdBdMode)
        {
            // WRITE(12) supports a full 32-bit transfer length (up to 4,294,967,295 sectors).
            // Only validate non-negative — the uint cast of a positive int is always in range.
            if (sectorCount < 0)
                throw new ArgumentOutOfRangeException(nameof(sectorCount),
                    $"Sector count must be non-negative, got {sectorCount}.");
        }
        else
        {
            // WRITE(10) CDB only supports up to 65,535 sectors per command (16-bit field)
            if (sectorCount < 0 || sectorCount > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(sectorCount),
                    $"Sector count must be between 0 and {ushort.MaxValue}, got {sectorCount}.");
        }

        var len = dataLength >= 0 ? dataLength : data.Length;
        // Use span-based slicing to avoid allocating a new array when the full buffer isn't needed
        byte[] writeData;
        if (len < data.Length)
        {
            writeData = data.AsSpan(0, len).ToArray();
        }
        else
        {
            writeData = data;
        }

        // Select the appropriate WRITE CDB based on media type:
        // - WRITE(12) for DVD/Blu-ray: 32-bit transfer length, reduced command overhead
        // - WRITE(10) for CD: 16-bit transfer length, standard CD burning command
        var cdb = useDvdBdMode
            ? MmcCommands.BuildWrite12(lba, (uint)sectorCount)
            : MmcCommands.BuildWrite10(lba, (ushort)sectorCount);

        // Set the Simu bit (byte 1, bit 1) when simulating — the drive performs all
        // mechanical operations (spindle, tracking, laser calibration) but with the
        // laser at a non-recording power level. No data is actually written to the
        // disc, but the drive validates the entire burn pipeline (timing, wobble,
        // ATIP/LPP detection, OPC, buffer management) as if a real burn were in
        // progress. This is how Nero/ImgBurn simulation works at the SCSI level.
        if (simulate)
            cdb[1] |= 0x02;

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, writeData)
        {
            TimeoutMs = 60_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>Reads sectors from the disc at the given LBA using READ(10).</summary>
    public (ScsiResult Result, byte[] Data) ReadSectors(uint lba, ushort sectorCount, int sectorSize)
    {
        var bufferSize = (int)Math.Min((long)sectorCount * sectorSize, int.MaxValue);
        var cmd = new ScsiCommand(
            MmcCommands.BuildRead10(lba, sectorCount),
            ScsiDataDirection.In, bufferSize)
        {
            TimeoutMs = 30_000
        };
        var result = _transport.Execute(cmd);
        return (result, cmd.DataBuffer);
    }

    /// <summary>Reads sectors from the disc at the given LBA.</summary>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="sectorCount">Number of sectors to read.</param>
    /// <param name="sectorSize">Size of each sector in bytes.</param>
    /// <param name="useDvdBdMode">
    /// When true, uses READ(12) which has a 32-bit transfer length field
    /// suitable for DVD/Blu-ray media. When false, uses READ(10) with a
    /// 16-bit transfer length field for standard Audio/Data CDs.
    /// Per SBC-4/MMC-5, READ(12) (opcode A8h) is preferred for DVD and Blu-ray
    /// because its 32-bit transfer length allows larger contiguous reads,
    /// reducing SCSI command overhead on high-speed media. READ(10) (opcode 28h)
    /// remains the standard for CD reading where sector counts per command are small.
    /// </param>
    public (ScsiResult Result, byte[] Data) ReadSectors(uint lba, uint sectorCount, int sectorSize,
        bool useDvdBdMode)
    {
        if (useDvdBdMode)
        {
            // READ(12) supports a full 32-bit transfer length
            var bufferSize = (int)Math.Min((long)sectorCount * sectorSize, int.MaxValue);
            var cdb = MmcCommands.BuildRead12(lba, sectorCount);
            var cmd = new ScsiCommand(cdb, ScsiDataDirection.In, bufferSize)
            {
                TimeoutMs = 30_000
            };
            var result = _transport.Execute(cmd);
            return (result, cmd.DataBuffer);
        }
        else
        {
            // READ(10) CDB only supports up to 65,535 sectors per command (16-bit field)
            if (sectorCount > ushort.MaxValue)
                sectorCount = ushort.MaxValue;
            return ReadSectors(lba, (ushort)sectorCount, sectorSize);
        }
    }

    /// <summary>Reads raw CD sectors including subchannel data.</summary>
    public (ScsiResult Result, byte[] Data) ReadCdRaw(uint lba, uint sectorCount,
        byte expectedSectorType = 0, bool includeSubchannel = false,
        string subchannelMode = "RW")
    {
        return ReadCdRaw(lba, sectorCount, expectedSectorType, includeSubchannel,
            subchannelMode, c2ErrorMode: 0);
    }

    /// <summary>
    /// Reads raw CD sectors with optional C2 error flags and subchannel data.
    /// C2 error flags are essential for byte-perfect disc dumping — they identify
    /// which individual bytes in a sector had uncorrectable C2-level errors.
    /// Per MMC-5, the C2 Error Block Data is a 294-byte bitmap (2352 bits) where
    /// each bit corresponds to one byte in the 2352-byte raw sector. A set bit
    /// indicates that the corresponding byte could not be corrected by the drive's
    /// C2 error correction.
    /// </summary>
    /// <param name="lba">Starting logical block address.</param>
    /// <param name="sectorCount">Number of sectors to read.</param>
    /// <param name="expectedSectorType">Expected sector type filter (0 = any).</param>
    /// <param name="includeSubchannel">Whether to include subchannel data.</param>
    /// <param name="subchannelMode">Subchannel mode: "RW", "RW_RAW", or "P-W".</param>
    /// <param name="c2ErrorMode">
    /// C2 error data mode:
    ///   0 = None (default, compatible with all drives)
    ///   1 = C2 Error Block Data (294 bytes per sector — 2352-bit error bitmap)
    ///   2 = C2 and Block Error Flags (296 bytes per sector — bitmap + 2-byte error count)
    /// </param>
    public (ScsiResult Result, byte[] Data) ReadCdRaw(uint lba, uint sectorCount,
        byte expectedSectorType, bool includeSubchannel,
        string subchannelMode, int c2ErrorMode)
    {
        // Clamp sector count to prevent excessive memory allocation
        // Max reasonable read: 32 sectors × 2448 bytes = ~78 KB
        const uint maxSectorsPerRead = 32;
        if (sectorCount > maxSectorsPerRead)
            sectorCount = maxSectorsPerRead;

        // Main channel: sync + header + user data + EDC/ECC = 2352 bytes
        // CDB byte 9 bit layout per MMC-5:
        //   Bit 7 (0x80) = Sync, Bit 6 (0x40) = Sub-Header, Bit 5 (0x20) = Header,
        //   Bit 4 (0x10) = User Data, Bit 3 (0x08) = EDC/ECC,
        //   Bits 2:1 = Error Flags (C2): 00=none, 01=C2 block (294B), 10=C2+block error (296B)
        //   Bit 0 = Reserved
        byte mainBits = 0xF8; // Sync+SubHeader+Header+UserData+EDC/ECC, no C2
        int c2Bytes = 0;
        if (c2ErrorMode == 1)
        {
            mainBits |= 0x02; // Error Flags bits 2:1 = 01 → C2 Error Block Data (294 bytes)
            c2Bytes = 294;
        }
        else if (c2ErrorMode == 2)
        {
            mainBits |= 0x04; // Error Flags bits 2:1 = 10 → C2 and Block Error Flags (296 bytes)
            c2Bytes = 296;
        }

        // Subchannel selection bits for READ CD (byte 10):
        // 0x00 = none, 0x01 = raw P-W (96 bytes), 0x02 = formatted Q (16 bytes),
        // 0x04 = R-W de-interleaved and error-corrected (96 bytes)
        byte subBits;
        int subchannelBytes;
        if (!includeSubchannel)
        {
            subBits = 0x00;
            subchannelBytes = 0;
        }
        else
        {
            switch (subchannelMode)
            {
                case "RW_RAW":
                    subBits = 0x01; // Raw P-W subchannel (96 bytes, interleaved)
                    subchannelBytes = 96;
                    break;
                case "P-W":
                    subBits = 0x04; // R-W de-interleaved and error-corrected (96 bytes)
                    subchannelBytes = 96;
                    break;
                default: // "RW" or any other
                    subBits = 0x01; // Raw P-W subchannel (96 bytes)
                    subchannelBytes = 96;
                    break;
            }
        }
        // Per MMC-5 §6.26, returned data order per sector is:
        // [main channel (2352)] [C2 data (0/294/296)] [subchannel (0/96)]
        int sectorBytes = 2352 + c2Bytes + subchannelBytes;

        var bufferSize = (int)(sectorCount * sectorBytes);
        var buffer = new byte[bufferSize];
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadCd(lba, sectorCount, expectedSectorType, mainBits, subBits),
            ScsiDataDirection.In, buffer)
        {
            TimeoutMs = 60_000
        };
        var result = _transport.Execute(cmd);
        return (result, cmd.DataBuffer);
    }

    /// <summary>Flushes the drive's write cache.</summary>
    /// <remarks>
    /// On macOS, the synchronous SYNCHRONIZE CACHE (IMM=0) can cause the IOKit
    /// kernel driver to time out the SCSI task internally, tearing down the user
    /// client and causing a segmentation fault when the managed code attempts to
    /// access the now-invalidated vtable pointers. To avoid this, on macOS we
    /// use IMM=1 (immediate) mode: the drive returns GOOD immediately and flushes
    /// in the background. We then poll with TEST UNIT READY until the drive
    /// reports ready (meaning the flush completed). This matches the approach
    /// used by cdrecord/cdrtools for long-running cache flush operations.
    ///
    /// On Linux and Windows, the synchronous approach (IMM=0) is used because
    /// those platforms handle long SCSI timeouts correctly without tearing down
    /// the transport.
    /// </remarks>
    public bool SynchronizeCache()
    {
        // Clear any pending UNIT ATTENTION conditions that may have queued
        // during the write sequence. Per SPC-5, each TEST UNIT READY clears
        // one UNIT ATTENTION; issue multiple to drain the queue.
        TestUnitReady();
        TestUnitReady();

        if (OperatingSystem.IsMacOS())
        {
            // macOS: use IMM=1 to avoid IOKit transport timeout during long sync.
            // The drive returns GOOD immediately and flushes in the background.
            var immCmd = new ScsiCommand(
                MmcCommands.BuildSynchronizeCache(immediate: true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 30_000 // Short timeout since IMM=1 returns immediately
            };
            var immResult = _transport.Execute(immCmd);
            if (!immResult.Success)
            {
                // If IMM=1 is not supported (some older drives), fall back to
                // synchronous mode with a generous timeout.
                if (immResult.SenseKey == 0x05) // Illegal Request — not supported
                {
                    var syncCmd = new ScsiCommand(
                        MmcCommands.BuildSynchronizeCache(immediate: false),
                        ScsiDataDirection.None)
                    {
                        TimeoutMs = 300_000
                    };
                    return _transport.Execute(syncCmd).Success;
                }
                return false;
            }

            // Poll with TEST UNIT READY until the drive reports ready (flush complete).
            // During the background flush, the drive returns "Not Ready, long write
            // in progress" (SK=0x02, ASC=0x04, ASCQ=0x08) or similar. When TUR
            // returns GOOD (status 0x00), the cache is synchronized.
            return WaitForDriveReady(maxWaitMs: 300_000, pollIntervalMs: 1000);
        }

        // Linux / Windows: use synchronous SYNCHRONIZE CACHE (IMM=0).
        var cmd = new ScsiCommand(MmcCommands.BuildSynchronizeCache(immediate: false), ScsiDataDirection.None)
        {
            TimeoutMs = 300_000 // Can take a long time
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>Blanks a rewritable disc.</summary>
    public ScsiResult BlankDisc(bool quick)
    {
        // Prepare for destructive operation: on Windows, this locks and dismounts
        // the mounted volume so the storage filter driver allows BLANK through.
        // On macOS, this re-claims via DA and unmounts.
        PrepareForDestructiveOperation();

        // Clear any pending UNIT ATTENTION conditions before blanking.
        // After media operations (burn, probe), drives may have queued sense data
        // that causes the next command to fail. Issue multiple TURs to clear all
        // queued UNIT ATTENTION conditions (drives may queue more than one).
        // Per SPC-5, each TEST UNIT READY clears one UNIT ATTENTION.
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();

        // Ensure the disc motor is spinning — some drives need this before BLANK.
        StartStopUnit(true);

        // StartStopUnit may itself generate a UNIT ATTENTION (ASC 0x28 "Not Ready
        // to Ready Change" or ASC 0x29 "Power On, Reset, or Bus Device Reset").
        // Clear it before issuing BLANK. Also wait briefly for the drive to settle
        // after spin-up — some USB-connected drives need time between START UNIT
        // and the next command.
        TestUnitReady();
        if (!WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500))
        {
            // Drive still not ready — try one more START UNIT cycle
            StartStopUnit(true);
            TestUnitReady();
        }

        var blankType = quick ? MmcCommands.BlankTypeMinimal : MmcCommands.BlankTypeDisc;
        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(blankType, 0, true),
            ScsiDataDirection.None)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full blank
        };
        var result = _transport.Execute(cmd);

        // If BLANK fails with "Not Ready" (sense key 0x02), the drive may be
        // temporarily unavailable after PrepareForWrite unmounted/reclaimed the volume.
        // This is common on macOS where DA unmount causes a brief transition state.
        // Wait for the drive to stabilize, then retry.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If BLANK fails with UNIT ATTENTION (sense key 0x06), retry once after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady(); // Clear the UNIT ATTENTION
            result = _transport.Execute(cmd);
        }

        // If minimal blank fails with Illegal Request (sense key 0x05), try full blank.
        // Some drives/media combinations reject BlankTypeMinimal but accept BlankTypeDisc.
        if (!result.Success && result.SenseKey == 0x05 && quick)
        {
            var fullCmd = new ScsiCommand(
                MmcCommands.BuildBlank(MmcCommands.BlankTypeDisc, 0, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            var fallbackResult = _transport.Execute(fullCmd);
            if (fallbackResult.Success)
                return fallbackResult;
        }

        // If full blank fails with Illegal Request, try minimal blank as fallback.
        // Some drives reject full blank on already partially-blanked media.
        if (!result.Success && result.SenseKey == 0x05 && !quick)
        {
            var minCmd = new ScsiCommand(
                MmcCommands.BuildBlank(MmcCommands.BlankTypeMinimal, 0, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            var fallbackResult = _transport.Execute(minCmd);
            if (fallbackResult.Success)
                return fallbackResult;
        }

        // If BLANK with IMM=1 (immediate) failed, retry with IMM=0 (synchronous).
        // Some drives don't support immediate mode for BLANK.
        if (!result.Success && (result.SenseKey == 0x05 || result.Asc == 0x24))
        {
            var syncCmd = new ScsiCommand(
                MmcCommands.BuildBlank(blankType, 0, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            var syncResult = _transport.Execute(syncCmd);
            if (syncResult.Success)
                return syncResult;
        }

        return result;
    }

    /// <summary>Closes a track or session.</summary>
    /// <remarks>
    /// Per MMC-5 §6.1, after a write/synchronize cache sequence the drive may have
    /// pending UNIT ATTENTION conditions. These must be cleared before CLOSE TRACK/SESSION.
    /// Also retries with IMM=0 (synchronous) if IMM=1 (immediate) is rejected, and
    /// falls back to CloseSession (function 0x02) if CloseSessionFinal (0x06) fails —
    /// closing the last session on a single-session disc effectively finalizes it.
    ///
    /// On macOS IOKit, transport-level failures (Status=0xFF) and transient NOT READY
    /// (SK=0x02) states are common after write operations. The retry strategy uses
    /// gentle recovery: TUR draining + WaitForDriveReady (no StartStopUnit, which
    /// can cause IOKit to re-evaluate the device and invalidate the user client on
    /// macOS, leading to SIGILL crashes on subsequent vtable calls).
    /// </remarks>
    public bool CloseTrackSession(byte function, ushort trackNumber = 0)
    {
        // Clear any pending UNIT ATTENTION conditions that may have queued
        // during the write/synchronize cache sequence. Per SPC-5, each TEST UNIT READY
        // clears one UNIT ATTENTION; issue multiple to drain the queue.
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildCloseTrackSession(function, trackNumber, true),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000 // Finalizing can take time
        };
        var result = _transport.Execute(cmd);
        if (result.Success) return true;

        // Handle "Not Ready" (SK=0x02) or transport-level failures (Status=0xFF).
        // The drive may still be busy processing after SYNCHRONIZE CACHE (e.g.,
        // writing run-out blocks, updating internal state). On macOS IOKit, the
        // driver surfaces transient "Not Ready" states and transport errors that
        // other platforms handle internally.
        //
        // IMPORTANT: Do NOT use StartStopUnit(true) during recovery here. On macOS,
        // StartStopUnit can cause IOKit's SCSI Architecture Model family to
        // re-evaluate the device state, potentially tearing down and recreating
        // the user client. This invalidates our cached vtable pointers (from
        // IOCreatePlugInInterfaceForService / QueryInterface), and subsequent
        // vtable calls crash with SIGILL (illegal hardware instruction) because
        // the function pointers now point to freed memory. Instead, use gentle
        // recovery: TUR draining + WaitForDriveReady polling only.
        if (result.SenseKey == 0x02 || result.Status == 0xFF)
        {
            // First recovery attempt: drain UNIT ATTENTION + wait for ready.
            // Per SPC-5, each TEST UNIT READY clears one queued UNIT ATTENTION.
            // Issue two to handle the common case of multiple queued conditions
            // (e.g., "Power On" + "Not Ready to Ready Change" after spin-up).
            TestUnitReady();
            TestUnitReady();
            WaitForDriveReady(maxWaitMs: 60_000, pollIntervalMs: 1000);

            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(function, trackNumber, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
            if (result.Success) return true;

            // Second recovery attempt: wait longer, try IMM=0 (synchronous)
            if (result.SenseKey == 0x02 || result.Status == 0xFF)
            {
                // Drain additional UNIT ATTENTION (see comment above)
                TestUnitReady();
                TestUnitReady();
                WaitForDriveReady(maxWaitMs: 120_000, pollIntervalMs: 2000);
            }

            // Try with IMM=0 after "Not Ready" recovery
            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(function, trackNumber, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
            if (result.Success) return true;
        }

        // Retry with IMM=0 (synchronous) — some drives reject immediate mode for close/finalize
        if (result.SenseKey == 0x05 || result.SenseKey == 0x06)
        {
            // Clear UNIT ATTENTION if that was the issue
            if (result.SenseKey == 0x06)
            {
                TestUnitReady();
                TestUnitReady();
            }

            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(function, trackNumber, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
            if (result.Success) return true;
        }

        // If this was a finalize request (function 0x06) that failed, fall back to
        // CloseSession (function 0x02). Closing the last incomplete session on a
        // single-session disc effectively finalizes it on most drives.
        // Also try function 0x02 if the drive is an older model that only supports MMC-2
        // where there was no explicit finalize function.
        // Accept both ILLEGAL REQUEST (SK=0x05) and transport failures for this fallback.
        if (function == MmcCommands.CloseSessionFinal &&
            (result.SenseKey == 0x05 || result.Status == 0xFF))
        {
            // Clear any pending state before fallback
            TestUnitReady();
            WaitForDriveReady(maxWaitMs: 30_000, pollIntervalMs: 1000);

            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSession, trackNumber, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
            if (result.Success) return true;

            // Try CloseSession with IMM=0
            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSession, trackNumber, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
            if (result.Success) return true;
        }

        return false;
    }

    /// <summary>Ejects the disc tray.</summary>
    /// <remarks>
    /// Clears any pending UNIT ATTENTION before the eject to maximize reliability.
    /// Does NOT call PreventMediumRemoval or StartStopUnit(start) here — callers
    /// (e.g., BurnEngine) are responsible for unlocking the tray before calling Eject.
    /// Issuing StartStopUnit(start) during the eject sequence can cause IOKit on macOS
    /// to re-evaluate the device, potentially invalidating vtable pointers and causing
    /// SIGILL crashes.
    /// </remarks>
    public bool Eject()
    {
        // Clear any pending UNIT ATTENTION that could interfere with eject
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildStartStopUnit(false, true),
            ScsiDataDirection.None)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (result.Success) return true;

        // If eject failed with UNIT ATTENTION (SK=0x06), clear it and retry
        if (result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
            if (result.Success) return true;
        }

        // If eject failed with "Not Ready" (SK=0x02) or transport error (0xFF),
        // wait briefly for drive to settle and retry
        if (result.SenseKey == 0x02 || result.Status == 0xFF)
        {
            WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);
            result = _transport.Execute(cmd);
        }

        return result.Success;
    }

    /// <summary>Loads/closes the disc tray.</summary>
    public bool LoadTray()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildStartStopUnit(true, true),
            ScsiDataDirection.None)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Starts or stops the disc motor without ejecting/loading the tray.
    /// Used to force the drive to re-read the medium after a burn operation.
    /// </summary>
    /// <param name="start">True to spin up the motor, false to spin down.</param>
    public bool StartStopUnit(bool start)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildStartStopUnit(start, false),
            ScsiDataDirection.None)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>Reads the drive buffer capacity.</summary>
    public (uint TotalLength, uint BlankLength)? ReadBufferCapacity()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadBufferCapacity(12),
            ScsiDataDirection.In, 12);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 12)
            return null;

        var data = cmd.DataBuffer;
        return (MmcCommands.ReadBE32(data, 4), MmcCommands.ReadBE32(data, 8));
    }

    /// <summary>Reserves space for a track.</summary>
    public bool ReserveTrack(uint sizeInSectors)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReserveTrack(sizeInSectors),
            ScsiDataDirection.None)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>Gets multi-session info (start sector, next writable address).</summary>
    public (uint StartSector, uint NextWritableAddress)? GetMultiSessionInfo()
    {
        // Read TOC format 1 (multi-session info) to get start of last session
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadToc(1, 0, 12),
            ScsiDataDirection.In, 12);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 12)
            return null;

        var data = cmd.DataBuffer;
        var lastSessionStart = MmcCommands.ReadBE32(data, 8);

        // Try READ TRACK INFORMATION on the invisible/last track to get
        // the actual Next Writable Address (more accurate than TOC format 1)
        var lastTrackInfo = ReadTrackInfo(0xFF);  // 0xFF = last track
        if (lastTrackInfo?.NwaValid == true)
            return (lastSessionStart, lastTrackInfo.NextWritableAddress);

        return (lastSessionStart, lastSessionStart);
    }

    /// <summary>Gets ATIP (Absolute Time In Pregroove) data for media type detection.</summary>
    public AtipData? ReadAtip()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadToc(4, 0, 2048),
            ScsiDataDirection.In, 2048);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        var data = cmd.DataBuffer;
        return new AtipData
        {
            WritableDisc = (data[6] & 0x40) != 0,
            IsRewritable = (data[6] & 0x04) != 0,
            RawData = data[..result.DataTransferred]
        };
    }

    /// <summary>Gets drive profile (current media type) via GET CONFIGURATION.</summary>
    public ushort GetCurrentProfile()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildGetConfiguration(0, 8),
            ScsiDataDirection.In, 8);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return 0;

        return MmcCommands.ReadBE16(cmd.DataBuffer, 6);
    }

    /// <summary>
    /// Gets media event status via GET EVENT STATUS NOTIFICATION.
    /// Returns true if media is present, false if no media.
    /// </summary>
    public bool? GetMediaPresent()
    {
        var cdb = MmcCommands.BuildGetEventStatusNotification(MmcCommands.EventClassMedia, 8);
        var cmd = new ScsiCommand(cdb, ScsiDataDirection.In, 8)
        {
            TimeoutMs = 5000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        var data = cmd.DataBuffer;
        // Per MMC-5 §6.5, GET EVENT STATUS NOTIFICATION response:
        //   Bytes 0-1: Event Descriptor Length
        //   Byte 2: bit 7 = NEA (No Event Available), bits 2:0 = Notification Class
        //   Byte 3: Supported Event Classes
        //   Byte 4: Event Code (bits 3:0): 0=NoChange, 1=Eject, 2=NewMedia, 3=MediaRemoval
        //   Byte 5: bit 1 = Media Present, bit 0 = Door/Tray Open
        //
        // If NEA=1, no event class is supported and the data fields are not valid.
        // If Notification Class != 4 (Media), we didn't get media event data.
        if ((data[2] & 0x80) != 0) // NEA = No Event Available
            return null;
        if ((data[2] & 0x07) != 0x04) // Notification Class should be 4 (Media)
            return null;

        var mediaPresent = (data[5] & 0x02) != 0;
        return mediaPresent;
    }

    /// <summary>
    /// Gets the list of supported write speeds from the drive.
    /// First tries GET PERFORMANCE (Type 03h - Write Speed) per MMC-5,
    /// then falls back to GET CONFIGURATION feature 0x0107 descriptors.
    /// Returns speeds in KB/s.
    /// </summary>
    public System.Collections.Generic.List<uint> GetSupportedWriteSpeeds()
    {
        var speeds = new System.Collections.Generic.List<uint>();

        // Method 1: GET PERFORMANCE Type 03h (Write Speed descriptors) per MMC-5 §6.8
        // This is the standard way to query write speed descriptors.
        // Each descriptor is 16 bytes per MMC-5 Table 606:
        //   Bytes 0-3: Flags (WRC/RDD/Exact/MRW) + reserved
        //   Bytes 4-7: End LBA (big-endian)
        //   Bytes 8-11: Read Speed in KB/s (big-endian)
        //   Bytes 12-15: Write Speed in KB/s (big-endian)
        try
        {
            var perfCmd = new ScsiCommand(
                MmcCommands.BuildGetPerformance(0x03, 0, 50),
                ScsiDataDirection.In, 2048);
            var perfResult = _transport.Execute(perfCmd);
            if (perfResult.Success && perfResult.DataTransferred >= 12)
            {
                var perfData = perfCmd.DataBuffer;
                // Response header: 4 bytes (performance data length — excludes the 4-byte header itself)
                // Per MMC-5 §6.8, the Performance Data Length field at bytes 0-3 gives the number of
                // bytes of data following the header, NOT including the 4-byte header.
                var dataLength = MmcCommands.ReadBE32(perfData, 0);
                // Write Speed Descriptors start at offset 8 (4-byte header + 4-byte misc fields)
                // Each descriptor is 16 bytes per MMC-5 Table 606:
                //   Bytes 0-3: Flags (WRC bits 4:3, RDD bit 2, Exact bit 1, MRW bit 0) + reserved
                //   Bytes 4-7: End LBA (big-endian)
                //   Bytes 8-11: Read Speed in KB/s (big-endian)
                //   Bytes 12-15: Write Speed in KB/s (big-endian)
                int descOffset = 8;
                int descEnd = Math.Min(4 + (int)dataLength, perfResult.DataTransferred);
                while (descOffset + 16 <= descEnd)
                {
                    var speedKBs = MmcCommands.ReadBE32(perfData, descOffset + 12);
                    if (speedKBs > 0 && !speeds.Contains(speedKBs))
                        speeds.Add(speedKBs);
                    descOffset += 16;
                }
            }
        }
        catch (InvalidOperationException) { /* GET PERFORMANCE not supported — fall through to fallback */ }
        catch (NotSupportedException) { /* Command not supported on this transport */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpticalDrive] GET PERFORMANCE failed: {ex.Message}");
        }

        if (speeds.Count > 0)
            return speeds;

        // Method 2: Fallback — MODE SENSE page 2Ah (Capabilities and Mechanical Status)
        // Older drives and some USB enclosures expose current/max write speed here.
        try
        {
            var modePage = ReadModePage(0x2A, minPageBytes: 16);
            if (modePage != null)
            {
                var (pageData, _, pageOffset) = modePage.Value;
                var pageLen = pageData[pageOffset + 1] + 2;
                // Byte 28-29: Current Write Speed (bytes at pageOffset+28..29)
                if (pageLen >= 30 && pageOffset + 30 <= pageData.Length)
                {
                    var currentWriteSpeed = (uint)MmcCommands.ReadBE16(pageData, pageOffset + 28);
                    if (currentWriteSpeed > 0 && !speeds.Contains(currentWriteSpeed))
                        speeds.Add(currentWriteSpeed);
                }
                // If page is long enough, it may contain write speed descriptors
                // starting at byte 32 with 2-byte count at byte 30
                if (pageLen >= 32 && pageOffset + 32 <= pageData.Length)
                {
                    var numDescriptors = MmcCommands.ReadBE16(pageData, pageOffset + 30);
                    // Each descriptor is 4 bytes (2 reserved + 2 speed)
                    for (int d = 0; d < numDescriptors; d++)
                    {
                        int descStart = pageOffset + 32 + d * 4;
                        if (descStart + 4 > pageData.Length || descStart + 4 > pageOffset + pageLen)
                            break;
                        var speedKBs = (uint)MmcCommands.ReadBE16(pageData, descStart + 2);
                        if (speedKBs > 0 && !speeds.Contains(speedKBs))
                            speeds.Add(speedKBs);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpticalDrive] MODE SENSE 2Ah write speed fallback failed: {ex.Message}");
        }

        return speeds;
    }

    /// <summary>
    /// Queries the drive for supported read speeds using GET PERFORMANCE (Data Type 00h, Type=0)
    /// and MODE SENSE page 2Ah as fallback.
    /// Returns a list of read speeds in KB/s (unique, sorted).
    /// </summary>
    public System.Collections.Generic.List<uint> GetSupportedReadSpeeds()
    {
        var speeds = new System.Collections.Generic.List<uint>();

        // Method 1: GET PERFORMANCE Data Type 00h (Nominal Performance) with Type=0 (Read)
        // Per MMC-5 §6.8 Table 590:
        //   CDB byte 1 bits 4:0 = Data Type (00h = Nominal Performance)
        //   CDB byte 10 = Type (00h = Read, 01h = Write)
        // Response has 16-byte descriptors per MMC-5 Table 593:
        //   Bytes 0-3: Start LBA
        //   Bytes 4-7: Start Performance (1000 bytes/sec)
        //   Bytes 8-11: End LBA
        //   Bytes 12-15: End Performance (1000 bytes/sec)
        try
        {
            var perfCmd = new ScsiCommand(
                MmcCommands.BuildGetPerformance(0x00, 0, 50), // Data Type 00h, Type at byte 10 defaults to 0 (read)
                ScsiDataDirection.In, 2048);
            var perfResult = _transport.Execute(perfCmd);
            if (perfResult.Success && perfResult.DataTransferred >= 12)
            {
                var perfData = perfCmd.DataBuffer;
                var dataLength = MmcCommands.ReadBE32(perfData, 0);
                // Descriptors start at offset 8 (4-byte data length + 4-byte header fields)
                int descOffset = 8;
                int descEnd = Math.Min(4 + (int)dataLength, perfResult.DataTransferred);
                while (descOffset + 16 <= descEnd)
                {
                    // Use End Performance (bytes 12-15) as the read speed at that LBA range
                    var speedKBs = MmcCommands.ReadBE32(perfData, descOffset + 12);
                    if (speedKBs > 0 && !speeds.Contains(speedKBs))
                        speeds.Add(speedKBs);
                    // Also check Start Performance for variable-speed drives (CAV)
                    var startSpeed = MmcCommands.ReadBE32(perfData, descOffset + 4);
                    if (startSpeed > 0 && startSpeed != speedKBs && !speeds.Contains(startSpeed))
                        speeds.Add(startSpeed);
                    descOffset += 16;
                }
            }
        }
        catch (InvalidOperationException) { /* GET PERFORMANCE not supported */ }
        catch (NotSupportedException) { /* Command not supported on this transport */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpticalDrive] GET PERFORMANCE (read) failed: {ex.Message}");
        }

        if (speeds.Count > 0)
        {
            speeds.Sort();
            return speeds;
        }

        // Method 2: Fallback — MODE SENSE page 2Ah (Capabilities and Mechanical Status)
        // Per MMC-3, bytes 8-9: Maximum Read Speed (obsolete in MMC-5 but still reported by most drives)
        // Bytes 14-15: Current Read Speed Selected (obsolete)
        try
        {
            var modePage = ReadModePage(0x2A, minPageBytes: 16);
            if (modePage != null)
            {
                var (pageData, _, pageOffset) = modePage.Value;
                var pageLen = pageData[pageOffset + 1] + 2;

                // Byte 8-9: Maximum Read Speed (obsolete but widely supported)
                if (pageLen >= 10 && pageOffset + 10 <= pageData.Length)
                {
                    var maxReadSpeed = (uint)MmcCommands.ReadBE16(pageData, pageOffset + 8);
                    if (maxReadSpeed > 0 && !speeds.Contains(maxReadSpeed))
                        speeds.Add(maxReadSpeed);
                }

                // Byte 14-15: Current Read Speed Selected (obsolete)
                if (pageLen >= 16 && pageOffset + 16 <= pageData.Length)
                {
                    var currentReadSpeed = (uint)MmcCommands.ReadBE16(pageData, pageOffset + 14);
                    if (currentReadSpeed > 0 && !speeds.Contains(currentReadSpeed))
                        speeds.Add(currentReadSpeed);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpticalDrive] MODE SENSE 2Ah read speed fallback failed: {ex.Message}");
        }

        speeds.Sort();
        return speeds;
    }

    /// <summary>Translates an MMC profile number to a media type string.</summary>
    public static string ProfileToMediaType(ushort profile) => profile switch
    {
        // Non-removable / generic
        0x0001 => "Non-removable Disc",
        0x0002 => "Removable Disc",
        // CD profiles
        0x0008 => "CD-ROM",
        0x0009 => "CD-R",
        0x000A => "CD-RW",
        // DVD profiles
        0x0010 => "DVD-ROM",
        0x0011 => "DVD-R",
        0x0012 => "DVD-RAM",
        0x0013 => "DVD-RW (Restricted Overwrite)",
        0x0014 => "DVD-RW (Sequential)",
        0x0015 => "DVD-R DL (Sequential)",
        0x0016 => "DVD-R DL (Layer Jump)",
        0x001A => "DVD+RW",
        0x001B => "DVD+R",
        0x002A => "DVD+RW DL",
        0x002B => "DVD+R DL",
        // Blu-ray profiles
        0x0040 => "BD-ROM",
        0x0041 => "BD-R (SRM)",
        0x0042 => "BD-R (RRM)",
        0x0043 => "BD-RE",
        0x0044 => "BD-ROM DL",
        0x0045 => "BD-R DL",
        0x0046 => "BD-RE DL",
        // BDXL profiles (per SFF-8090)
        0x004A => "BD-R TL (BDXL)",
        0x004B => "BD-RE TL (BDXL)",
        0x004C => "BD-R QL (BDXL)",
        0x004D => "BD-RE QL (BDXL)",
        // HD DVD profiles
        0x0050 => "HD DVD-ROM",
        0x0051 => "HD DVD-R",
        0x0052 => "HD DVD-RAM",
        0x0053 => "HD DVD-RW",
        0x0058 => "HD DVD-R DL",
        0x005A => "HD DVD-RW DL",
        _ => profile == 0 ? "No Media" : $"Unknown (0x{profile:X4})"
    };

    /// <summary>Returns true if the given profile represents a writable media type.</summary>
    public static bool IsProfileWritable(ushort profile) => profile switch
    {
        0x0009 or 0x000A => true,   // CD-R, CD-RW
        0x0011 or 0x0012 or 0x0013 or 0x0014 or 0x0015 or 0x0016 => true, // DVD-R, DVD-RAM, DVD-RW, DVD-R DL
        0x001A or 0x001B => true,   // DVD+RW, DVD+R
        0x002A or 0x002B => true,   // DVD+RW DL, DVD+R DL
        0x0041 or 0x0042 or 0x0043 => true, // BD-R (SRM), BD-R (RRM), BD-RE
        0x0045 or 0x0046 => true,   // BD-R DL, BD-RE DL
        0x004A or 0x004B or 0x004C or 0x004D => true, // BDXL (BD-R TL, BD-RE TL, BD-R QL, BD-RE QL)
        0x0051 or 0x0052 or 0x0053 or 0x0058 or 0x005A => true, // HD DVD-R, HD DVD-RAM, HD DVD-RW, HD DVD-R DL, HD DVD-RW DL
        _ => false
    };

    /// <summary>
    /// Reads the disc capacity via READ CAPACITY command.
    /// Returns the last logical block address and the block length.
    /// </summary>
    public (uint LastLba, uint BlockLength)? ReadCapacity()
    {
        var cmd = new ScsiCommand(MmcCommands.BuildReadCapacity(), ScsiDataDirection.In, 8)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        var data = cmd.DataBuffer;
        var lastLba = MmcCommands.ReadBE32(data, 0);
        var blockLength = MmcCommands.ReadBE32(data, 4);
        return (lastLba, blockLength);
    }

    /// <summary>
    /// Sets the read speed. Uses SET CD SPEED for CD media and
    /// SET STREAMING for DVD/BD media.
    /// </summary>
    public bool SetReadSpeed(ushort readSpeedKBs, bool isDvdOrBd = false)
    {
        if (isDvdOrBd)
        {
            // For DVD/BD media, try SET STREAMING first (preferred per MMC-5 §6.31)
            if (SetStreamingReadSpeed(readSpeedKBs))
                return true;
        }

        // Try SET CD SPEED (simpler, works for CD and many DVD drives as fallback)
        var cmd = new ScsiCommand(
            MmcCommands.BuildSetCdSpeed(readSpeedKBs, 0xFFFF),
            ScsiDataDirection.None)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (result.Success) return true;

        // For CD media that rejected SET CD SPEED, try SET STREAMING as last resort
        if (!isDvdOrBd)
            return SetStreamingReadSpeed(readSpeedKBs);

        return false;
    }

    /// <summary>
    /// Sets read speed via SET STREAMING command (preferred for DVD/BD).
    /// </summary>
    private bool SetStreamingReadSpeed(ushort speedKBs)
    {
        var descriptor = new byte[28];

        // End LBA = 0xFFFFFFFF (entire disc)
        MmcCommands.WriteBE32(descriptor, 8, 0xFFFFFFFF);

        // Read speed
        uint readSpeed = speedKBs == 0xFFFF ? 0xFFFFFFFFu : (uint)speedKBs;
        MmcCommands.WriteBE32(descriptor, 12, readSpeed);

        // Read time = 1000 ms
        MmcCommands.WriteBE32(descriptor, 16, 0x000003E8);

        // Write speed = max
        MmcCommands.WriteBE32(descriptor, 20, 0xFFFFFFFF);

        // Write time = 1000 ms
        MmcCommands.WriteBE32(descriptor, 24, 0x000003E8);

        var cmd = new ScsiCommand(
            MmcCommands.BuildSetStreaming(28),
            ScsiDataDirection.Out, descriptor)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Configures the error recovery mode page (01h) for read operations.
    /// Sets retry count and error recovery flags.
    /// </summary>
    public bool SetErrorRecovery(byte retryCount, bool transferBadBlocks = false)
    {
        // Try MODE SENSE(10) / MODE SELECT(10) first
        var modePage = ReadModePage(MmcCommands.ErrorRecoveryPage, minPageBytes: 4);
        if (modePage != null)
        {
            var (pageData, headerLen, pageOffset) = modePage.Value;
            ApplyErrorRecoveryFields(pageData, pageOffset, retryCount, transferBadBlocks);
            if (SendModeSelect(pageData, headerLen, pageOffset))
                return true;
        }

        // Fallback: try MODE SENSE(6) / MODE SELECT(6)
        var modePage6 = ReadModePage6(MmcCommands.ErrorRecoveryPage, minPageBytes: 4);
        if (modePage6 != null)
        {
            var (pageData6, headerLen6, pageOffset6) = modePage6.Value;
            ApplyErrorRecoveryFields(pageData6, pageOffset6, retryCount, transferBadBlocks);
            if (SendModeSelect6(pageData6, headerLen6, pageOffset6))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Applies error recovery field modifications to a mode page 01h buffer.
    /// </summary>
    private static void ApplyErrorRecoveryFields(byte[] pageData, int pageOffset,
        byte retryCount, bool transferBadBlocks)
    {
        if (transferBadBlocks)
            pageData[pageOffset + 2] |= 0x20; // TB (Transfer Block) = 1
        else
            pageData[pageOffset + 2] &= unchecked((byte)~0x20); // TB = 0
        pageData[pageOffset + 3] = retryCount;
    }

    /// <summary>
    /// Sends a CUE sheet to the drive for SAO or DAO writing.
    /// Required before SAO/DAO write operations (both use SCSI Write Type 0x02).
    /// </summary>
    public ScsiResult SendCueSheet(byte[] cueSheetData)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildSendCueSheet((uint)cueSheetData.Length),
            ScsiDataDirection.Out, cueSheetData)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>
    /// Reads disc structure information (DVD Physical Format, Copyright, BCA, etc.).
    /// Used for getting detailed media information like layer break position,
    /// disc category, and manufacturer information.
    /// </summary>
    public (ScsiResult Result, byte[] Data) ReadDiscStructure(
        byte mediaType = 0, byte layerNumber = 0, byte format = 0, ushort allocationLength = 2052)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadDiscStructure(mediaType, layerNumber, format, allocationLength),
            ScsiDataDirection.In, allocationLength)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        return (result, cmd.DataBuffer);
    }

    /// <summary>
    /// Gets the layer break position for dual-layer media.
    /// Returns the LBA of the layer break, or null if not available.
    /// </summary>
    public uint? GetLayerBreakPosition()
    {
        var (result, data) = ReadDiscStructure(0, 0, MmcCommands.DiscStructureFormatPhysical, 2052);
        if (!result.Success || result.DataTransferred < 20)
            return null;

        // READ DISC STRUCTURE response for Physical Format Information (format 0x00):
        //   Bytes 0-1: Data Length (big-endian, excluding these 2 bytes)
        //   Bytes 2-3: Reserved
        //   Physical Format Information starts at byte 4:
        //     Byte 4: Book Type (bits 7:4), Part Version (bits 3:0)
        //     Byte 5: Disc Size (bits 7:4), Maximum Rate (bits 3:0)
        //     Byte 6: Number of Layers (bits 6:5), Track Path (bit 4), Layer Type (bits 3:0)
        //     Byte 7: Linear Density (bits 7:4), Track Density (bits 3:0)
        //     Bytes 8-11: Starting Physical Sector of Data Area (32 bits, big-endian)
        //     Bytes 12-15: End Physical Sector of Data Area (32 bits, big-endian)
        //     Bytes 16-19: End Physical Sector of Layer 0 (32 bits, big-endian) — layer break
        //     Byte 20: BCA flag (bit 7)
        var numLayers = (data[6] >> 5) & 0x03; // 0 = single-layer, 1 = dual-layer
        if (numLayers == 0) return null; // Single layer, no layer break

        // End sector of Layer 0 = layer break position
        // Per ECMA-267/MMC-5, bytes 16-19 contain the End Physical Sector Number of Layer 0
        // as a 32-bit big-endian value. This is the last sector of layer 0;
        // it represents the layer break position directly (where layer 1 data begins).
        // Must read all 4 bytes — reading only 3 (24 bits) would truncate the value
        // for BDXL media where sector numbers can exceed 16,777,215 (0xFFFFFF).
        uint endSectorL0 = MmcCommands.ReadBE32(data, 16);
        return endSectorL0;
    }

    /// <summary>
    /// Formats a BD-R disc using FORMAT UNIT command.
    /// BD-R requires formatting before first use (unless drive handles it automatically).
    /// Per MMC spec, BD-R only supports format type 0x31 (Full Format).
    /// Format type 0x32 (Spare Area Preparation) is BD-RE only and must not be used for BD-R.
    /// Per MMC-5, format type occupies bits 7:2 of descriptor byte 4 (pre-shifted in constants).
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatBdR(bool sparePrepare)
    {
        // BD-R only supports Full Format (0x31). Spare area preparation (0x32)
        // is a BD-RE-only operation. Ignore the sparePrepare parameter for BD-R
        // and always use Full Format.
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        // Per dvd+rw-tools: BD-R uses IMMED only (no FOV).
        // With FOV=0, the drive uses its own default format options.
        formatData[1] = 0x02; // Immed=1 (bit 1) only
        formatData[3] = 0x08; // descriptor length = 8

        // Try Full Format (0x31) first, fall back to Spare Prepare (0x32) then Spare Only (0x30)
        PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
            MmcCommands.FormatBdReFullFormat,
            MmcCommands.FormatBdReSparePrepare,
            MmcCommands.FormatBdReSpareOnly);

        // CmpLst=0: no defect list follows the format descriptor.
        // Per MMC-5 §6.3, CmpLst=1 is invalid when no defect list is provided.
        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: false);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full format (IMM=1 but timeout must cover initial phase)
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        return result;
    }

    /// <summary>
    /// Formats a BD-RE disc using FORMAT UNIT command.
    /// BD-RE uses Format Type 0x30 (Spare Area) for reformatting. Quick vs full
    /// is controlled by the format descriptor sub-type: 3 = Quick Certification,
    /// 2 = Full Certification (per dvd+rw-tools reference implementation).
    /// Unlike BD-R, BD-RE can be re-formatted multiple times.
    /// Per MMC-5, format type occupies bits 7:2 of descriptor byte 4 (pre-shifted in constants).
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatBdRe(bool quickFormat)
    {
        // Prepare for destructive operation: on Windows, this locks and dismounts
        // the mounted volume so the storage filter driver allows FORMAT UNIT through.
        PrepareForDestructiveOperation();

        // Clear any pending UNIT ATTENTION conditions
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();

        // Ensure the disc motor is spinning
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        // Read format capacities to get the correct descriptor values.
        // Many BD-RE drives require the Number of Blocks field to match what
        // READ FORMAT CAPACITIES returns; zero is rejected with "Invalid field in CDB".
        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        // Per dvd+rw-tools: BD-RE uses FOV=1 + Immed=1 (0x82).
        // DCRT is NOT used for BD-RE — certification level is controlled
        // through the format descriptor sub-type bits instead.
        formatData[1] = 0x82; // FOV=1 (bit 7), Immed=1 (bit 1)
        formatData[3] = 0x08; // descriptor length = 8

        // BD-RE format type order depends on quick vs full:
        // Quick erase: Prefer Spare Area Only (0x30) — fastest erase method.
        //   Falls back to Full Format (0x31), then Spare Prepare (0x32).
        // Full erase: Prefer Full Format (0x31) — complete reformatting with
        //   full certification. Falls back to Spare Area Only (0x30).
        if (quickFormat)
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatBdReSpareOnly,
                MmcCommands.FormatBdReFullFormat,
                MmcCommands.FormatBdReSparePrepare);
        else
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatBdReFullFormat,
                MmcCommands.FormatBdReSpareOnly,
                MmcCommands.FormatBdReSparePrepare);

        // Per dvd+rw-tools: For BD-RE format type 0x30 (Spare Area), the sub-type
        // bits (1:0 of descriptor byte 4) control certification level:
        //   Sub-type 3 = Quick Certification (fast, skips verification)
        //   Sub-type 2 = Full Certification (performs full verification)
        // This is the correct way to control quick vs full for BD-RE, NOT the DCRT flag.
        byte currentFormatType = (byte)((formatData[8] >> 2) & 0x3F);
        if (currentFormatType == 0x30)
        {
            formatData[8] = (byte)((formatData[8] & 0xFC) | (quickFormat ? 0x03 : 0x02));
        }

        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: false);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full BD-RE format
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT with IMM=1 fails, retry with IMM=0 (synchronous).
        // Some drives don't support immediate mode for FORMAT UNIT.
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = 0x80; // FOV=1, IMM=0
            result = _transport.Execute(cmd);
        }

        // If BD-RE-specific format types fail, try format type 0x00 (generic Full Format).
        // Some BD-RE drives (especially those with firmware that follows the older
        // BD-RE specification) only accept format type 0x00 for reformatting
        // already-formatted media. This is the same format type used by DVD+RW.
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = 0x82; // Restore FOV=1, IMM=1
            // Use format type 0x00 with the current capacity blocks
            // Try to find a type 0x00 descriptor from the drive
            if (descriptors != null)
            {
                var genericMatch = FindFormatDescriptor(descriptors, 0x00);
                if (genericMatch != null)
                {
                    MmcCommands.WriteBE32(formatData, 4, genericMatch.NumberOfBlocks);
                    formatData[8] = genericMatch.FormatTypeByte;
                    formatData[9] = (byte)(genericMatch.TypeDependentParameter >> 16);
                    formatData[10] = (byte)(genericMatch.TypeDependentParameter >> 8);
                    formatData[11] = (byte)(genericMatch.TypeDependentParameter & 0xFF);
                }
                else
                {
                    // No type 0x00 descriptor — use current capacity with format type 0x00
                    MmcCommands.WriteBE32(formatData, 4,
                        descriptors.Length > 0 && descriptors[0].NumberOfBlocks > 0
                            ? descriptors[0].NumberOfBlocks
                            : fallbackBlocks);
                    formatData[8] = 0x00; // Format type 0x00
                    formatData[9] = 0x00;
                    formatData[10] = 0x00;
                    formatData[11] = 0x00;
                }
            }
            else if (fallbackBlocks > 0)
            {
                MmcCommands.WriteBE32(formatData, 4, fallbackBlocks);
                formatData[8] = 0x00;
                formatData[9] = 0x00;
                formatData[10] = 0x00;
                formatData[11] = 0x00;
            }

            result = _transport.Execute(cmd);

            // Try IMM=0 with format type 0x00 as well
            if (!result.Success && result.SenseKey == 0x05)
            {
                formatData[1] = 0x80; // FOV=1, IMM=0
                result = _transport.Execute(cmd);
            }
        }

        // Last resort: try FORMAT UNIT with FOV=0 (let the drive choose format options).
        // Per MMC-5 §6.3, when FOV=0 the drive ignores the format descriptor and uses
        // its own defaults. This is a minimalist approach that works on many drives.
        if (!result.Success && result.SenseKey == 0x05)
        {
            // FOV=0: some drives expect descriptor length = 0 (no descriptor data).
            // Send a 4-byte header only (no 8-byte descriptor).
            var minimalData4 = new byte[4];
            minimalData4[1] = 0x02; // FOV=0, Immed=1 only
            minimalData4[3] = 0x00; // descriptor length = 0

            var minimalCmd4 = new ScsiCommand(cdb, ScsiDataDirection.Out, minimalData4)
            {
                TimeoutMs = 3_600_000
            };
            result = _transport.Execute(minimalCmd4);

            // If 4-byte header fails, try with 12-byte (descriptor length = 8) and NumberOfBlocks
            if (!result.Success && result.SenseKey == 0x05)
            {
                var minimalData = new byte[12];
                minimalData[1] = 0x02; // FOV=0, Immed=1 only
                minimalData[3] = 0x08; // descriptor length = 8
                if (fallbackBlocks > 0)
                    MmcCommands.WriteBE32(minimalData, 4, fallbackBlocks);
                else if (descriptors != null && descriptors.Length > 0)
                    MmcCommands.WriteBE32(minimalData, 4, descriptors[0].NumberOfBlocks);

                var minimalCmd = new ScsiCommand(cdb, ScsiDataDirection.Out, minimalData)
                {
                    TimeoutMs = 3_600_000
                };
                result = _transport.Execute(minimalCmd);

                // Try without Immed
                if (!result.Success && result.SenseKey == 0x05)
                {
                    minimalData[1] = 0x00; // FOV=0, Immed=0
                    result = _transport.Execute(minimalCmd);
                }
            }
        }

        // Absolute last resort: try FORMAT UNIT with FmtData=0 (no parameter data at all).
        // The drive must use its own defaults entirely. This works on some BD-RE drives
        // that reject all forms of format parameter data.
        if (!result.Success && result.SenseKey == 0x05)
        {
            var noDataCdb = MmcCommands.BuildFormatUnit(fmtData: false, cmpLst: false);
            var noDataCmd = new ScsiCommand(noDataCdb, ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            result = _transport.Execute(noDataCmd);
        }

        // Final fallback: try BLANK command (A1h).
        // Per MMC-5, BD-RE officially uses FORMAT UNIT, but some drives (especially
        // USB-attached or with non-standard firmware) accept the BLANK command for
        // reinitializing already-formatted BD-RE media. This is non-standard but
        // commonly works in practice.
        if (!result.Success && result.SenseKey == 0x05)
        {
            var blankType = quickFormat ? MmcCommands.BlankTypeMinimal : MmcCommands.BlankTypeDisc;
            var blankCmd = new ScsiCommand(
                MmcCommands.BuildBlank(blankType, 0, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            var blankResult = _transport.Execute(blankCmd);
            if (blankResult.Success)
                result = blankResult;
        }

        return result;
    }

    /// <summary>
    /// Requests sense data from the drive.
    /// Useful for getting detailed error information after a failed command.
    /// Supports both fixed-format (0x70/0x71) and descriptor-format (0x72/0x73) sense data.
    /// </summary>
    public (byte SenseKey, byte Asc, byte Ascq) RequestSense()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildRequestSense(18),
            ScsiDataDirection.In, 18)
        {
            TimeoutMs = 5000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return (0, 0, 0);

        var data = cmd.DataBuffer;
        var responseCode = (byte)(data[0] & 0x7F);

        return responseCode switch
        {
            // Descriptor-format: SenseKey=byte1, ASC=byte2, ASCQ=byte3
            0x72 or 0x73 when result.DataTransferred >= 4
                => ((byte)(data[1] & 0x0F), data[2], data[3]),
            // Fixed-format: SenseKey=byte2, ASC=byte12, ASCQ=byte13
            _ when result.DataTransferred >= 14
                => ((byte)(data[2] & 0x0F), data[12], data[13]),
            _ => (0, 0, 0)
        };
    }

    /// <summary>
    /// Configures BD-R recording mode via MODE SELECT (Mode Page 05h).
    /// BD-R supports SRM (Sequential Recording Mode) and optionally RRM (Random Recording Mode).
    /// SRM is the standard mode; SRM+POW (Pseudo Overwrite) allows limited random writes.
    /// </summary>
    public bool ConfigureBdRMode(string mode)
    {
        // Read and locate mode page 05h (write parameters)
        var modePage = ReadModePage(MmcCommands.WriteParametersPage, minPageBytes: 3);
        if (modePage == null) return false;

        var (pageData, headerLen, pageOffset) = modePage.Value;

        // BD-R mode encoding in write parameters page:
        // Write Type (byte 2, bits 3:0):
        //   0x04 = Sequential Recording Mode (SRM)
        //   0x04 with POW bit = SRM + Pseudo Overwrite
        // For RRM, the disc must be pre-formatted with FORMAT UNIT and then
        // write type remains 0x04 but random addressing is allowed.
        var modeUpper = mode.ToUpperInvariant();
        byte writeType = 0x04; // SRM (default)

        // Set write type in lower nibble, preserve upper nibble flags
        pageData[pageOffset + 2] = (byte)((pageData[pageOffset + 2] & 0xF0) | (writeType & 0x0F));

        // Set Pseudo Overwrite (POW) bit if SRM+POW mode requested
        // POW is bit 6 of mode page 05h byte 2 (the BUFE/LS_V/TestWrite byte)
        // on drives that support it. This enables pseudo-overwrite capability.
        if (modeUpper.Contains("POW"))
        {
            pageData[pageOffset + 2] |= 0x40; // Set POW bit (bit 6)
        }

        return SendModeSelect(pageData, headerLen, pageOffset);
    }

    /// <summary>Returns true if the given profile represents erasable/rewritable media.</summary>
    public static bool IsProfileErasable(ushort profile) => profile switch
    {
        0x000A => true,  // CD-RW
        0x0012 => true,  // DVD-RAM
        0x0013 => true,  // DVD-RW Restricted Overwrite
        0x0014 => true,  // DVD-RW Sequential
        0x001A => true,  // DVD+RW
        0x002A => true,  // DVD+RW DL
        0x0043 => true,  // BD-RE
        0x0046 => true,  // BD-RE DL
        0x004B => true,  // BD-RE TL (BDXL)
        0x004D => true,  // BD-RE QL (BDXL)
        0x0052 => true,  // HD DVD-RAM
        0x0053 => true,  // HD DVD-RW
        _ => false
    };

    /// <summary>Returns true if the given profile represents HD DVD media.</summary>
    public static bool IsProfileHdDvd(ushort profile) =>
        profile >= 0x0050 && profile <= 0x005F;

    /// <summary>Returns true if the given profile represents Blu-ray media.</summary>
    public static bool IsProfileBluRay(ushort profile) =>
        profile >= 0x0040 && profile <= 0x004F;

    /// <summary>Returns true if the given profile represents DVD media.</summary>
    /// <remarks>
    /// DVD profiles span 0x0010-0x002B per MMC-5. Profiles 0x002C-0x003F are reserved
    /// but should still be treated as DVD-family to avoid misidentifying future profiles
    /// as CD. This is consistent with the isDvdOrBd >= 0x0010 check used elsewhere.
    /// </remarks>
    public static bool IsProfileDvd(ushort profile) =>
        profile >= 0x0010 && profile <= 0x003F;

    /// <summary>
    /// Detects whether the currently inserted media is M-DISC (Millennial Disc).
    /// M-DISC uses the same MMC profiles as standard DVD+R (0x001B) or BD-R (0x0041/0x0042)
    /// but with an inorganic recording layer for archival longevity.
    /// Detection is performed by reading the disc manufacturer information from the ADIP
    /// (Address In Pre-groove) data via READ DISC STRUCTURE (format 0x04) and checking
    /// for Millenniata/Verbatim M-DISC manufacturer identifiers.
    /// </summary>
    public bool IsMDiscMedia()
    {
        var profile = GetCurrentProfile();
        // M-DISC is only available as DVD+R or BD-R variants
        if (profile is not (0x001B or 0x0041 or 0x0042 or 0x0045 or 0x004A))
            return false;

        try
        {
            // Read disc manufacturer information (format code 0x04)
            var (result, data) = ReadDiscStructure(0, 0, MmcCommands.DiscStructureFormatManufacturer, 2052);
            if (!result.Success || result.DataTransferred < 8)
                return false;

            // Data layout: bytes 0-1 = data length (big-endian), bytes 2-3 = reserved,
            // followed by manufacturer-specific data starting at byte 4.
            var dataLen = (data[0] << 8) | data[1];
            if (dataLen < 4) return false;

            // Extract manufacturer ID string from disc structure data.
            // M-DISC media is manufactured by Millenniata (now Verbatim M-DISC).
            // Known manufacturer identifiers: "MILLENIATA" (early production),
            // "VERBAT-M" (current Verbatim M-DISC), "MKM-" (Mitsubishi/Verbatim prefix).
            int stringLen = Math.Min(dataLen, result.DataTransferred - 4);
            stringLen = Math.Min(stringLen, data.Length - 4); // Guard against buffer overrun
            if (stringLen <= 0) return false;
            var manufacturerStr = Encoding.ASCII.GetString(data, 4, stringLen).TrimEnd('\0', ' ');
            var mfgUpper = manufacturerStr.ToUpperInvariant();

            return mfgUpper.Contains("MILLENIATA") ||
                   mfgUpper.Contains("MILLENNIATA") ||
                   mfgUpper.Contains("M-DISC") ||
                   mfgUpper.Contains("MDISC") ||
                   (mfgUpper.Contains("VERBAT") && mfgUpper.Contains("-M"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the M-DISC media type string based on the current profile, or null if not M-DISC.
    /// Must be called after confirming <see cref="IsMDiscMedia"/> returns true;
    /// does not re-verify M-DISC status to avoid redundant SCSI round trips.
    /// </summary>
    public string? GetMDiscMediaType()
    {
        var profile = GetCurrentProfile();
        return profile switch
        {
            0x001B => "M-DISC DVD",
            0x0041 or 0x0042 => "M-DISC BD-R",
            0x0045 => "M-DISC BD-R DL",
            0x004A => "M-DISC BD-R XL",
            _ => null
        };
    }

    /// <summary>
    /// Prevents or allows medium removal (locks/unlocks the tray).
    /// Useful during burn operations to prevent accidental ejection.
    /// </summary>
    public bool PreventMediumRemoval(bool prevent)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildPreventAllowMediumRemoval(prevent),
            ScsiDataDirection.None)
        {
            TimeoutMs = 5000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Verifies sectors on the disc using VERIFY(10) command.
    /// The drive reads the specified sectors and checks ECC but does not transfer data.
    /// Returns true if all sectors verified successfully.
    /// </summary>
    public bool VerifySectors(uint lba, ushort sectorCount)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildVerify10(lba, sectorCount),
            ScsiDataDirection.None)
        {
            TimeoutMs = 60_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Formats a DVD-RAM disc using FORMAT UNIT command.
    /// DVD-RAM uses Format Type 0x01 (DVD-RAM spare area expansion) for quick format
    /// and 0x00 (full format) for full format per MMC-3 §6.18.
    /// Per MMC-5, format type occupies bits 7:2 of descriptor byte 4 (pre-shifted in constants).
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatDvdRam(bool quickFormat)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        // Per dvd+rw-tools: DVD-RAM uses FOV=1 (bit 7), Immed=1 (bit 1).
        // For quick format (non-spare-area-expansion descriptors), DCRT is also set.
        // Mt. Fuji/INF-8090i places DCRT at bit 5 (0x20), not bit 6 (0x40).
        formatData[1] = (byte)(quickFormat ? 0xA2 : 0x82);
        formatData[3] = 0x08; // descriptor length = 8

        // Quick: try spare area expansion (0x01) then full (0x00)
        // Full: try full format (0x00) then spare (0x01)
        byte usedFormatType;
        if (quickFormat)
            usedFormatType = PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdRamSpare, MmcCommands.FormatDvdRamFull);
        else
            usedFormatType = PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdRamFull, MmcCommands.FormatDvdRamSpare);

        // Per dvd+rw-tools: For DVD-RAM quick format when using format type other than 0x01
        // (spare area expansion), set CmpLst=1 in CDB along with DCRT in header.
        // For spare area expansion (0x01) or full format, CmpLst=0.
        // FormatDvdRamSpare (0x04) is format type 0x01 pre-shifted by 2 bits.
        bool needsCmpLst = quickFormat && (usedFormatType & 0xFC) != MmcCommands.FormatDvdRamSpare;
        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: needsCmpLst);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full DVD-RAM format
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT with IMM=1 fails, retry with IMM=0 (synchronous).
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = (byte)(quickFormat ? 0xA0 : 0x80); // Same flags but IMM=0
            result = _transport.Execute(cmd);
        }

        return result;
    }

    /// <summary>
    /// Formats a DVD-RW disc in Restricted Overwrite mode using FORMAT UNIT.
    /// This converts a sequentially-written DVD-RW into overwritable mode.
    /// Format Type 0x15 for DVD-RW Quick (Restricted Overwrite), 0x10 for full.
    /// Per MMC-5, format type occupies bits 7:2 of descriptor byte 4 (pre-shifted in constants).
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatDvdMinusRwRestrictedOverwrite(bool quickFormat)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        // Per dvd+rw-tools: DVD-RW uses IMMED only (no FOV, no DCRT).
        formatData[1] = 0x02; // Immed=1 (bit 1) only
        formatData[3] = 0x08;

        // Quick: try quick RO (0x15) then full RO (0x10)
        // Full: try full RO (0x10) then quick RO (0x15)
        if (quickFormat)
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdRwQuick, MmcCommands.FormatDvdRwFull);
        else
            PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks,
                MmcCommands.FormatDvdRwFull, MmcCommands.FormatDvdRwQuick);

        // Per dvd+rw-tools: For DVD-RW Quick Format (type 0x15), NumberOfBlocks
        // should be zeroed to make it truly quick.
        // FormatDvdRwQuick (0x54) is format type 0x15 pre-shifted by 2 bits.
        if ((formatData[8] & 0xFC) == MmcCommands.FormatDvdRwQuick)
        {
            formatData[4] = 0;
            formatData[5] = 0;
            formatData[6] = 0;
            formatData[7] = 0;
        }

        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: false);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full DVD-RW format
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT with IMM=1 fails, retry with IMM=0 (synchronous).
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = 0x00; // IMM=0
            result = _transport.Execute(cmd);
        }

        // Fallback: try BLANK command. DVD-RW in Sequential mode supports BLANK,
        // and some drives accept BLANK even in Restricted Overwrite mode.
        if (!result.Success && result.SenseKey == 0x05)
        {
            var blankType = quickFormat ? MmcCommands.BlankTypeMinimal : MmcCommands.BlankTypeDisc;
            var blankCmd = new ScsiCommand(
                MmcCommands.BuildBlank(blankType, 0, true),
                ScsiDataDirection.None)
            {
                TimeoutMs = 3_600_000
            };
            var blankResult = _transport.Execute(blankCmd);
            if (blankResult.Success)
                result = blankResult;
        }

        return result;
    }

    /// <summary>
    /// Formats an HD DVD-RW disc using FORMAT UNIT command.
    /// HD DVD-RW uses format type 0x00 (full format) for both quick and full;
    /// quick format sets DCRT=1 to skip certification.
    /// Per MMC-5, format type occupies bits 7:2 of descriptor byte 4 (pre-shifted in constants).
    /// Number of Blocks and Type Dependent Parameter are populated from READ FORMAT CAPACITIES.
    /// </summary>
    public ScsiResult FormatHdDvdRw(bool quickFormat)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        var descriptors = ReadFormatCapacities();
        var fallbackBlocks = GetCapacityFallbackBlocks();

        var formatData = new byte[12];
        // HD DVD-RW uses FOV=1 (bit 7), Immed=1 (bit 1).
        // For quick format, DCRT is set at bit 5 (0x20) per Mt. Fuji convention.
        formatData[1] = (byte)(quickFormat ? 0xA2 : 0x82);
        formatData[3] = 0x08;

        // Format type 0x00 (full format) — same for quick and full, differentiated by DCRT
        PopulateFormatDescriptor(descriptors, formatData, fallbackBlocks, 0x00);

        // CmpLst=0: no defect list follows the format descriptor.
        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: false);

        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000 // Up to 1 hour for full HD DVD-RW format
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT with IMM=1 fails, retry with IMM=0 (synchronous).
        if (!result.Success && result.SenseKey == 0x05)
        {
            formatData[1] = (byte)(quickFormat ? 0xA0 : 0x80); // Same flags but IMM=0
            result = _transport.Execute(cmd);
        }

        return result;
    }

    /// <summary>
    /// Reads track information by address type.
    /// Address type: 0x00 = LBA, 0x01 = Track Number, 0x02 = Session Number.
    /// Uses an extended allocation length to capture the Last Recorded Address field.
    /// This variant is useful for BD-R/RE discs where LRA determines the last recorded sector.
    /// </summary>
    public TrackInfoData? ReadTrackInfoByType(byte addressType, uint addressOrNumber)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadTrackInfoByType(addressType, addressOrNumber, 48),
            ScsiDataDirection.In, 48);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 28)
            return null;

        var data = cmd.DataBuffer;
        var trackNumLsb = data[2];
        var sessionNumLsb = data[3];
        ushort trackNum = trackNumLsb;
        ushort sessionNum = sessionNumLsb;
        if (result.DataTransferred >= 34)
        {
            trackNum = (ushort)((data[32] << 8) | trackNumLsb);
            sessionNum = (ushort)((data[33] << 8) | sessionNumLsb);
        }
        else if (result.DataTransferred >= 33)
        {
            trackNum = (ushort)((data[32] << 8) | trackNumLsb);
        }

        bool lraValid = (data[7] & 0x02) != 0;
        uint lastRecordedAddress = 0;
        if (lraValid && result.DataTransferred >= 32)
            lastRecordedAddress = MmcCommands.ReadBE32(data, 28);

        return new TrackInfoData
        {
            TrackNumber = trackNum,
            SessionNumber = sessionNum,
            TrackMode = (byte)(data[5] & 0x0F),
            DataMode = (byte)(data[6] & 0x0F),
            TrackStartAddress = MmcCommands.ReadBE32(data, 8),
            NextWritableAddress = MmcCommands.ReadBE32(data, 12),
            FreeBlocks = MmcCommands.ReadBE32(data, 16),
            FixedPacketSize = MmcCommands.ReadBE32(data, 20),
            TrackSize = MmcCommands.ReadBE32(data, 24),
            NwaValid = (data[7] & 0x01) != 0,
            LraValid = lraValid,
            LastRecordedAddress = lastRecordedAddress,
            Damage = (data[5] & 0x20) != 0,
            Copy = (data[5] & 0x10) != 0,
            ReservationTrack = (data[6] & 0x20) != 0,
        };
    }

    /// <summary>
    /// Gets the Last Recorded Address (LRA) for the specified track on sequential BD-R/RE discs.
    /// Returns the LRA or null if the track has no recorded data or the drive doesn't report LRA.
    /// Useful for determining the exact end of recorded data on sequential media.
    /// </summary>
    public uint? GetLastRecordedAddress(uint trackNumber = 0xFF)
    {
        var trackInfo = ReadTrackInfo(trackNumber);
        if (trackInfo == null) return null;

        // If LRA is explicitly valid, return it directly
        if (trackInfo.LraValid)
            return trackInfo.LastRecordedAddress;

        // Fallback: if NWA is valid, the LRA is NWA - 1 (last sector before the next writable)
        if (trackInfo.NwaValid && trackInfo.NextWritableAddress > 0)
            return trackInfo.NextWritableAddress - 1;

        // Fallback: TrackStart + TrackSize - 1 if track has data
        if (trackInfo.TrackSize > 0)
            return trackInfo.TrackStartAddress + trackInfo.TrackSize - 1;

        return null;
    }

    /// <summary>
    /// Reserves a track with the ARSV (Address Reservation) bit for BD-R SRM.
    /// When arsv=false, size specifies reservation size in sectors (standard).
    /// When arsv=true, the parameter specifies a logical track number for BD-R
    /// address-based reservation used in Sequential Recording Mode.
    /// </summary>
    public bool ReserveTrackEx(uint sizeOrTrackNumber, bool arsv = false)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReserveTrackEx(sizeOrTrackNumber, arsv),
            ScsiDataDirection.None)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd).Success;
    }

    // -----------------------------------------------------------------------
    // SEND DISC STRUCTURE (BFh) — send structure information to the drive
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends disc structure information to the drive using SEND DISC STRUCTURE (BFh).
    /// Used for sending timestamps, disc control blocks, and other format-specific
    /// structure data to the drive (e.g., during formatting or authoring).
    /// </summary>
    /// <param name="mediaType">Media type (0x00 = DVD, 0x01 = BD).</param>
    /// <param name="format">Structure format code to send.</param>
    /// <param name="data">Parameter list data to send to the drive.</param>
    /// <returns>The SCSI result of the operation.</returns>
    public ScsiResult SendDiscStructure(byte mediaType, byte format, byte[] data)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        var cmd = new ScsiCommand(
            MmcCommands.BuildSendDiscStructure(mediaType, format, (ushort)data.Length),
            ScsiDataDirection.Out, data)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd);
    }

    // -----------------------------------------------------------------------
    // READ HEADER (44h) — CD-ROM data block address header
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the CD-ROM data block address header for the specified LBA.
    /// Returns the data mode (Mode 0/1/2) and absolute address of the block.
    /// This command is specific to CD-ROM media; it is not applicable to DVD/BD.
    /// </summary>
    /// <param name="lba">Logical Block Address to read the header for.</param>
    /// <param name="msf">If true, return the address in MSF format; otherwise LBA.</param>
    /// <returns>Header information or null if the command fails.</returns>
    public ReadHeaderResult? ReadHeader(uint lba, bool msf = false)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadHeader(lba, msf, 8),
            ScsiDataDirection.In, 8)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        var data = cmd.DataBuffer;
        // READ HEADER response (8 bytes):
        //   Byte 0: CD-ROM Data Mode (0=Mode 0, 1=Mode 1, 2=Mode 2)
        //   Bytes 1-3: Reserved
        //   Bytes 4-7: Absolute CD-ROM Address (LBA or MSF, big-endian 32-bit)
        return new ReadHeaderResult
        {
            DataMode = data[0],
            AbsoluteAddress = MmcCommands.ReadBE32(data, 4)
        };
    }

    // -----------------------------------------------------------------------
    // READ TOC/PMA/ATIP (43h) — Full TOC, PMA
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the Full TOC (format 2) from the disc.
    /// The Full TOC contains complete session and track information in Q sub-channel format,
    /// including lead-in descriptors (A0, A1, A2 POINTs) for each session.
    /// Per MMC-5 §6.26, Full TOC uses MSF addressing and returns 11-byte descriptors.
    /// </summary>
    /// <returns>List of Full TOC entries or null if the command fails.</returns>
    public System.Collections.Generic.List<FullTocEntry>? ReadFullToc()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadToc(2, 1, 2048),
            ScsiDataDirection.In, 2048)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        var tocLength = MmcCommands.ReadBE16(data, 0);
        int dataEnd = Math.Min(2 + tocLength, result.DataTransferred);
        dataEnd = Math.Min(dataEnd, data.Length);

        var entries = new System.Collections.Generic.List<FullTocEntry>();
        // Full TOC descriptors start at offset 4, each is 11 bytes
        for (int offset = 4; offset + 11 <= dataEnd; offset += 11)
        {
            entries.Add(new FullTocEntry
            {
                SessionNumber = data[offset],
                Adr = (byte)((data[offset + 1] >> 4) & 0x0F),
                Control = (byte)(data[offset + 1] & 0x0F),
                Tno = data[offset + 2],
                Point = data[offset + 3],
                Min = data[offset + 4],
                Sec = data[offset + 5],
                Frame = data[offset + 6],
                PMin = data[offset + 8],
                PSec = data[offset + 9],
                PFrame = data[offset + 10]
            });
        }

        return entries;
    }

    /// <summary>
    /// Reads the PMA (Program Memory Area) data from the disc using format 3.
    /// PMA is available on CD-R/CD-RW media and contains a record of track
    /// and session recording actions. PMA data is temporary and exists in
    /// the Power Memory Area until the disc is finalized.
    /// </summary>
    /// <returns>PMA data with parsed entries, or null if the command fails.</returns>
    public PmaData? ReadPma()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadToc(3, 0, 2048),
            ScsiDataDirection.In, 2048)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        var pmaLength = MmcCommands.ReadBE16(data, 0);
        int dataEnd = Math.Min(2 + pmaLength, result.DataTransferred);
        dataEnd = Math.Min(dataEnd, data.Length);

        var pma = new PmaData
        {
            RawData = data[..result.DataTransferred]
        };

        // PMA descriptors start at offset 4, each is 11 bytes (same layout as Full TOC)
        for (int offset = 4; offset + 11 <= dataEnd; offset += 11)
        {
            pma.Entries.Add(new PmaEntry
            {
                Adr = (byte)((data[offset + 1] >> 4) & 0x0F),
                Control = (byte)(data[offset + 1] & 0x0F),
                Tno = data[offset + 2],
                Point = data[offset + 3],
                Min = data[offset + 4],
                Sec = data[offset + 5],
                Frame = data[offset + 6],
                PMin = data[offset + 8],
                PSec = data[offset + 9],
                PFrame = data[offset + 10]
            });
        }

        return pma;
    }

    // -----------------------------------------------------------------------
    // READ DISC STRUCTURE (ADh) — higher-level wrappers for specific formats
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the DVD/BD copyright information (format 0x01).
    /// Returns copyright protection type and region management information.
    /// </summary>
    public DiscCopyrightInfo? GetDiscCopyrightInfo(byte layerNumber = 0)
    {
        var (result, data) = ReadDiscStructure(0, layerNumber,
            MmcCommands.DiscStructureFormatCopyright, 2052);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        // Response: 4-byte header + copyright data
        // Byte 4: Copyright Protection System Type
        // Byte 5: Region Management Information
        return new DiscCopyrightInfo
        {
            CopyrightProtectionType = data[4],
            RegionManagement = data[5]
        };
    }

    /// <summary>
    /// Gets the Burst Cutting Area (BCA) data from the disc (format 0x03).
    /// BCA is a unique barcode area near the hub of DVD/BD discs,
    /// used for copy protection and disc identification.
    /// </summary>
    /// <returns>BCA data bytes or null if not available.</returns>
    public byte[]? GetBurstCuttingArea(byte layerNumber = 0)
    {
        var (result, data) = ReadDiscStructure(0, layerNumber,
            MmcCommands.DiscStructureFormatBca, 2052);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var bcaLength = MmcCommands.ReadBE16(data, 0);
        if (bcaLength < 2) return null;

        int payloadLen = Math.Min(bcaLength - 2, result.DataTransferred - 4);
        payloadLen = Math.Min(payloadLen, data.Length - 4);
        if (payloadLen <= 0) return null;

        var bcaData = new byte[payloadLen];
        Array.Copy(data, 4, bcaData, 0, payloadLen);
        return bcaData;
    }

    /// <summary>
    /// Gets the list of recognized disc structure format codes (format 0xFF).
    /// This indicates which format codes are supported by the drive for
    /// READ DISC STRUCTURE and SEND DISC STRUCTURE on the current media.
    /// </summary>
    public System.Collections.Generic.List<DiscStructureFormatListEntry>? GetDiscStructureFormatList(
        byte mediaType = 0)
    {
        var (result, data) = ReadDiscStructure(mediaType, 0,
            MmcCommands.DiscStructureFormatStructureList, 2052);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var listLength = MmcCommands.ReadBE16(data, 0);
        int dataEnd = Math.Min(2 + listLength, result.DataTransferred);
        dataEnd = Math.Min(dataEnd, data.Length);

        var entries = new System.Collections.Generic.List<DiscStructureFormatListEntry>();
        // Format list entries start at offset 4, each is 4 bytes per MMC-5 §6.22.3.22
        for (int offset = 4; offset + 4 <= dataEnd; offset += 4)
        {
            entries.Add(new DiscStructureFormatListEntry
            {
                FormatCode = data[offset],
                SendDiscStructureAvailable = (data[offset + 1] & 0x80) != 0,
                ReadDiscStructureAvailable = (data[offset + 1] & 0x40) != 0
            });
        }

        return entries;
    }

    /// <summary>
    /// Gets the write protection status for the current disc (format 0xC0).
    /// Returns the raw write protection status byte, or null if not supported.
    /// </summary>
    public byte? GetWriteProtectionStatus()
    {
        var (result, data) = ReadDiscStructure(0, 0,
            MmcCommands.DiscStructureFormatWriteProtect, 2052);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        // Byte 4 contains the write protection status flags
        return data[4];
    }

    /// <summary>
    /// Reads the manufacturer ID string from the disc using READ DISC STRUCTURE.
    /// For DVD media, uses format 0x04 (Manufacturer Information).
    /// For BD media, uses format 0x06 (Media Identifier).
    /// Returns the manufacturer ID string, or null on failure.
    /// </summary>
    public string? ReadManufacturerId(byte formatCode)
    {
        try
        {
            var (result, data) = ReadDiscStructure(0, 0, formatCode, 2052);
            if (!result.Success || result.DataTransferred < 8)
                return null;

            var dataLen = (data[0] << 8) | data[1];
            if (dataLen < 4) return null;

            int stringLen = Math.Min(dataLen, result.DataTransferred - 4);
            stringLen = Math.Min(stringLen, data.Length - 4);
            if (stringLen <= 0) return null;

            return System.Text.Encoding.ASCII.GetString(data, 4, stringLen).TrimEnd('\0', ' ');
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads disc information with extended allocation (65534 bytes).
    /// Returns more detailed information including disc type identification
    /// and additional status fields compared to ReadDiscInfo().
    /// </summary>
    public DiscInfoData? ReadDiscInformationEx()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReadDiscInfo(65534),
            ScsiDataDirection.In, 65534);
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 34)
            return null;

        var data = cmd.DataBuffer;
        var freeSectors = 0u;
        var nextWritable = 0u;
        byte sessionsMsb = 0;
        byte tracksMsb = 0;

        if (result.DataTransferred >= 38)
        {
            freeSectors = MmcCommands.ReadBE32(data, 16);
            nextWritable = MmcCommands.ReadBE32(data, 12);
            sessionsMsb = data[33];
            tracksMsb = data[34];
        }

        return new DiscInfoData
        {
            DiscStatus = (byte)(data[2] & 0x03),
            LastSessionState = (byte)((data[2] >> 2) & 0x03),
            Erasable = (data[2] & 0x10) != 0,
            NumberOfFirstTrack = data[3],
            NumberOfSessionsLsb = data[4],
            FirstTrackInLastSessionLsb = data[5],
            LastTrackInLastSessionLsb = data[6],
            DiscType = data[8],
            FreeSectors = freeSectors,
            NextWritableAddress = nextWritable,
            NumberOfSessionsMsb = sessionsMsb,
            NumberOfTracksMsb = tracksMsb,
        };
    }

    /// <summary>
    /// Sends OPC table data to the drive for optimized laser power calibration.
    /// The OPC table contains speed/power pairs that the drive uses to optimize
    /// write quality for the specific media inserted. This is typically used for
    /// BD-R and BD-RE media where precise power calibration is critical.
    /// </summary>
    /// <param name="opcTableData">OPC table data to send to the drive. The format is
    /// drive-specific but typically contains:
    ///   Bytes 0-1: OPC Table Length (big-endian, number of bytes following)
    ///   Bytes 2+:  OPC Table Entries (7 bytes each: 2-byte speed + 5-byte OPC values)
    /// </param>
    /// <param name="doOpc">If true, perform OPC after sending table data.
    /// If false, only send the OPC table without performing calibration.</param>
    public bool SendOpcInformation(byte[] opcTableData, bool doOpc = false)
    {
        if (opcTableData == null || opcTableData.Length == 0)
            return PerformOpc(); // No data — just do OPC

        var cmd = new ScsiCommand(
            MmcCommands.BuildSendOpcInfoWithData(doOpc, (ushort)opcTableData.Length),
            ScsiDataDirection.Out, opcTableData)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Reads sub-channel data from the current disc position.
    /// Returns the audio status and current position information (Q sub-channel).
    /// Applicable primarily to Audio CDs but the header (audio status) works for all media.
    /// </summary>
    public SubChannelData? ReadSubChannel(byte format = MmcCommands.SubChannelCurrentPosition,
        byte trackNumber = 0, bool msf = false)
    {
        // Allocation length depends on format:
        // Format 01h (Current Position): 4-byte header + 12 bytes sub-Q = 16 bytes min
        // Format 02h (MCN): 4-byte header + 20 bytes = 24 bytes
        // Format 03h (ISRC): 4-byte header + 20 bytes = 24 bytes
        ushort allocLen = format switch
        {
            MmcCommands.SubChannelCurrentPosition => 48,
            MmcCommands.SubChannelMcn => 24,
            MmcCommands.SubChannelIsrc => 24,
            _ => 48
        };

        var cmd = new ScsiCommand(
            MmcCommands.BuildReadSubChannel(format, subQ: true, msf: msf,
                trackNumber: trackNumber, allocationLength: allocLen),
            ScsiDataDirection.In, allocLen)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        // Sub-Channel Data Header (4 bytes):
        //   Byte 0: Reserved
        //   Byte 1: Audio Status (00h=not supported, 11h=playing, 12h=paused,
        //           13h=play complete, 14h=play error, 15h=no current status)
        //   Bytes 2-3: Sub-channel Data Length (big-endian, bytes following header)
        var audioStatus = data[1];
        var subChannelDataLength = MmcCommands.ReadBE16(data, 2);

        var subData = new SubChannelData
        {
            AudioStatus = audioStatus,
            SubChannelDataLength = subChannelDataLength,
            Format = format
        };

        // Parse format-specific data (starts at byte 4)
        if (format == MmcCommands.SubChannelCurrentPosition && result.DataTransferred >= 16)
        {
            // Current Position (Sub-Q Channel Data format 01h):
            //   Byte 4: Sub-channel Data Format Code (01h)
            //   Byte 5: bits 7:4 = ADR, bits 3:0 = Control
            //   Byte 6: Track Number
            //   Byte 7: Index Number
            //   Bytes 8-11: Absolute CD Address (LBA or MSF)
            //   Bytes 12-15: Track Relative CD Address (LBA or MSF)
            subData.Adr = (byte)((data[5] >> 4) & 0x0F);
            subData.Control = (byte)(data[5] & 0x0F);
            subData.TrackNumber = data[6];
            subData.IndexNumber = data[7];
            subData.AbsoluteAddress = MmcCommands.ReadBE32(data, 8);
            subData.TrackRelativeAddress = MmcCommands.ReadBE32(data, 12);
        }
        else if (format == MmcCommands.SubChannelMcn && result.DataTransferred >= 24)
        {
            // Media Catalogue Number (format 02h):
            //   Byte 4: Sub-channel Data Format Code (02h)
            //   Byte 5: bits 7:4 = ADR, bits 3:0 = Control
            //   Byte 8: MCVal (bit 7 = MCN valid)
            //   Bytes 9-21: Media Catalogue Number (13 bytes ASCII)
            subData.McnValid = (data[8] & 0x80) != 0;
            if (subData.McnValid && result.DataTransferred >= 22)
            {
                subData.MediaCatalogueNumber = Encoding.ASCII
                    .GetString(data, 9, 13).TrimEnd('\0', ' ');
            }
        }
        else if (format == MmcCommands.SubChannelIsrc && result.DataTransferred >= 24)
        {
            // Track ISRC (format 03h):
            //   Byte 4: Sub-channel Data Format Code (03h)
            //   Byte 5: bits 7:4 = ADR, bits 3:0 = Control
            //   Byte 6: Track Number
            //   Byte 8: TCVal (bit 7 = ISRC valid)
            //   Bytes 9-20: ISRC (12 bytes ASCII)
            subData.TrackNumber = data[6];
            subData.IsrcValid = (data[8] & 0x80) != 0;
            if (subData.IsrcValid && result.DataTransferred >= 21)
            {
                subData.Isrc = Encoding.ASCII
                    .GetString(data, 9, 12).TrimEnd('\0', ' ');
            }
        }

        return subData;
    }

    /// <summary>
    /// Reads the Media Catalogue Number (MCN/UPC) from the disc via READ SUB-CHANNEL.
    /// Returns the MCN string or null if not available.
    /// </summary>
    public string? ReadMediaCatalogueNumber()
    {
        var subChannel = ReadSubChannel(MmcCommands.SubChannelMcn);
        if (subChannel?.McnValid == true && !string.IsNullOrEmpty(subChannel.MediaCatalogueNumber))
            return subChannel.MediaCatalogueNumber;
        return null;
    }

    /// <summary>
    /// Reads the ISRC (International Standard Recording Code) for a specific track
    /// via READ SUB-CHANNEL. Returns the ISRC string or null if not available.
    /// </summary>
    public string? ReadIsrc(byte trackNumber)
    {
        var subChannel = ReadSubChannel(MmcCommands.SubChannelIsrc, trackNumber: trackNumber);
        if (subChannel?.IsrcValid == true && !string.IsNullOrEmpty(subChannel.Isrc))
            return subChannel.Isrc;
        return null;
    }

    /// <summary>
    /// Sends a REPORT KEY command to the drive for authentication/key exchange.
    /// Used in the AKE (Authentication and Key Exchange) process for CSS/CPPM (DVD)
    /// and AACS (Blu-ray) content protection.
    /// </summary>
    /// <param name="keyClass">Key class: 0x00=CSS/CPPM, 0x02=AACS.</param>
    /// <param name="keyFormat">Key format code specifying which key/data to report.</param>
    /// <param name="agid">Authentication Grant ID (0-3).</param>
    /// <param name="allocationLength">Maximum response length.</param>
    /// <returns>The key data returned by the drive, or null on failure.</returns>
    public ReportKeyData? ReportKey(byte keyClass, byte keyFormat,
        byte agid = 0, ushort allocationLength = 8)
    {
        // AACS responses can be large (e.g. drive certificates are ~724 bytes)
        if (allocationLength < 8) allocationLength = 8;

        var cmd = new ScsiCommand(
            MmcCommands.BuildReportKey(keyClass, keyFormat, agid, allocationLength),
            ScsiDataDirection.In, allocationLength)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        // Response header:
        //   Bytes 0-1: Data Length (number of bytes following, big-endian)
        //   Bytes 2-3: Reserved
        //   Bytes 4+:  Key data (format-specific)
        var dataLength = MmcCommands.ReadBE16(data, 0);
        var keyDataLen = Math.Min(dataLength, result.DataTransferred - 2);
        keyDataLen = Math.Max(0, keyDataLen);

        byte[] keyData;
        if (keyDataLen > 2 && result.DataTransferred > 4)
        {
            var payloadLen = Math.Min(keyDataLen - 2, result.DataTransferred - 4);
            keyData = new byte[payloadLen];
            Array.Copy(data, 4, keyData, 0, payloadLen);
        }
        else
        {
            keyData = Array.Empty<byte>();
        }

        return new ReportKeyData
        {
            DataLength = dataLength,
            KeyData = keyData
        };
    }

    /// <summary>
    /// Sends a SEND KEY command to the drive for authentication/key exchange.
    /// Used in the AKE process for CSS/CPPM (DVD) and AACS (Blu-ray) content protection.
    /// </summary>
    /// <param name="keyClass">Key class: 0x00=CSS/CPPM, 0x02=AACS.</param>
    /// <param name="keyFormat">Key format code specifying which key/data to send.</param>
    /// <param name="keyData">The key data to send to the drive (including any headers expected by the format).</param>
    /// <param name="agid">Authentication Grant ID (0-3).</param>
    /// <returns>True if the command succeeded.</returns>
    public bool SendKey(byte keyClass, byte keyFormat, byte[] keyData, byte agid = 0)
    {
        if (keyData == null || keyData.Length == 0)
            return false;

        var cmd = new ScsiCommand(
            MmcCommands.BuildSendKey(keyClass, keyFormat, agid, (ushort)keyData.Length),
            ScsiDataDirection.Out, keyData)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    // -----------------------------------------------------------------------
    // GET CONFIGURATION (46h) — full feature list and single-feature queries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Queries a single feature descriptor via GET CONFIGURATION (RT=0x01).
    /// Returns the feature data (including header and descriptor) or null if not supported.
    /// </summary>
    /// <param name="featureNumber">Feature number to query (e.g. 0x0021 = Incremental Streaming Writable).</param>
    /// <returns>Feature descriptor data or null if the feature is not supported.</returns>
    public FeatureDescriptor? GetFeature(ushort featureNumber)
    {
        // RT=0x01: return only the requested feature
        var cmd = new ScsiCommand(
            MmcCommands.BuildGetConfiguration(featureNumber, 512, rt: 0x01),
            ScsiDataDirection.In, 512)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 12)
            return null;

        var data = cmd.DataBuffer;
        // GET CONFIGURATION response header (8 bytes):
        //   Bytes 0-3: Data Length (big-endian, bytes following these 4)
        //   Bytes 4-5: Reserved
        //   Bytes 6-7: Current Profile (big-endian)
        // Feature Descriptor starts at byte 8:
        //   Bytes 8-9: Feature Code (big-endian)
        //   Byte 10: bits 7:2 = Version, bit 1 = Persistent, bit 0 = Current
        //   Byte 11: Additional Length

        var code = MmcCommands.ReadBE16(data, 8);
        if (code != featureNumber) return null;  // Drive didn't return the requested feature

        var featureFlags = data[10];
        var additionalLength = data[11];

        byte[] additionalData;
        if (additionalLength > 0 && result.DataTransferred > 12)
        {
            int copyLen = Math.Min(additionalLength, result.DataTransferred - 12);
            additionalData = new byte[copyLen];
            Array.Copy(data, 12, additionalData, 0, copyLen);
        }
        else
        {
            additionalData = Array.Empty<byte>();
        }

        return new FeatureDescriptor
        {
            FeatureCode = code,
            Version = (byte)((featureFlags >> 2) & 0x3F),
            Persistent = (featureFlags & 0x02) != 0,
            Current = (featureFlags & 0x01) != 0,
            AdditionalData = additionalData
        };
    }

    /// <summary>
    /// Gets all supported features via GET CONFIGURATION (RT=0x00).
    /// Returns a list of feature descriptors supported by the drive.
    /// </summary>
    public System.Collections.Generic.List<FeatureDescriptor> GetAllFeatures()
    {
        var features = new System.Collections.Generic.List<FeatureDescriptor>();

        // First query with a small buffer to get the total data length
        var headerCmd = new ScsiCommand(
            MmcCommands.BuildGetConfiguration(0, 8, rt: 0x00),
            ScsiDataDirection.In, 8)
        {
            TimeoutMs = 10_000
        };
        var headerResult = _transport.Execute(headerCmd);
        if (!headerResult.Success || headerResult.DataTransferred < 8)
            return features;

        // Determine actual size needed (clamped to reasonable max)
        var totalLength = MmcCommands.ReadBE32(headerCmd.DataBuffer, 0) + 4;
        var allocLen = (ushort)Math.Min(totalLength, 65534);

        var cmd = new ScsiCommand(
            MmcCommands.BuildGetConfiguration(0, allocLen, rt: 0x00),
            ScsiDataDirection.In, allocLen)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 12)
            return features;

        var data = cmd.DataBuffer;
        int offset = 8; // Skip 8-byte header
        while (offset + 4 <= result.DataTransferred)
        {
            var code = MmcCommands.ReadBE16(data, offset);
            var featureFlags = data[offset + 2];
            var addLen = data[offset + 3];

            if (offset + 4 + addLen > result.DataTransferred)
                break;

            byte[] addData;
            if (addLen > 0)
            {
                addData = new byte[addLen];
                Array.Copy(data, offset + 4, addData, 0, addLen);
            }
            else
            {
                addData = Array.Empty<byte>();
            }

            features.Add(new FeatureDescriptor
            {
                FeatureCode = code,
                Version = (byte)((featureFlags >> 2) & 0x3F),
                Persistent = (featureFlags & 0x02) != 0,
                Current = (featureFlags & 0x01) != 0,
                AdditionalData = addData
            });

            offset += 4 + addLen;
        }

        return features;
    }

    /// <summary>
    /// Checks whether a specific feature is currently active via GET CONFIGURATION.
    /// Returns true if the feature is present and current, false if not supported or not active.
    /// </summary>
    public bool IsFeatureCurrent(ushort featureNumber)
    {
        var feature = GetFeature(featureNumber);
        return feature?.Current == true;
    }

    // -----------------------------------------------------------------------
    // MODE SELECT (15h) / MODE SELECT 10 (55h) — public wrappers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends mode page data to the drive using MODE SELECT(6).
    /// Used for configuring drive parameters on older drives that don't support MODE SELECT(10).
    /// </summary>
    /// <param name="parameterData">Complete parameter data (header + mode page).
    /// The Mode Data Length field (byte 0) should be 0; drive ignores it per SPC.</param>
    /// <returns>True if the command succeeded.</returns>
    public bool ModeSelect6(byte[] parameterData)
    {
        if (parameterData == null || parameterData.Length == 0) return false;
        // Zero out the mode data length byte per SPC spec
        parameterData[0] = 0;

        var cmd = new ScsiCommand(
            MmcCommands.BuildModeSelect6((byte)Math.Min(parameterData.Length, 255)),
            ScsiDataDirection.Out, parameterData)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    /// <summary>
    /// Sends mode page data to the drive using MODE SELECT(10).
    /// The preferred method for modern optical drives.
    /// </summary>
    /// <param name="parameterData">Complete parameter data (header + mode page).
    /// The Mode Data Length field (bytes 0-1) should be 0; drive ignores it per SPC.</param>
    /// <returns>True if the command succeeded.</returns>
    public bool ModeSelect10(byte[] parameterData)
    {
        if (parameterData == null || parameterData.Length == 0) return false;
        // Zero out the mode data length per SPC spec
        parameterData[0] = 0;
        if (parameterData.Length > 1) parameterData[1] = 0;

        var cmd = new ScsiCommand(
            MmcCommands.BuildModeSelect10((ushort)parameterData.Length),
            ScsiDataDirection.Out, parameterData)
        {
            TimeoutMs = 10_000
        };
        return _transport.Execute(cmd).Success;
    }

    // -----------------------------------------------------------------------
    // LOG SENSE (4Dh) — read log pages from the drive
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a log page from the drive using LOG SENSE.
    /// Returns the raw log page data or null if the command fails.
    /// </summary>
    /// <param name="pageCode">Log page code (e.g. 0x00=Supported Pages, 0x02=Write Errors, 0x03=Read Errors).</param>
    /// <param name="subPageCode">Sub-page code (0x00 for most pages).</param>
    /// <param name="pageControl">Page control: 0x00=current cumulative, 0x01=current threshold,
    /// 0x02=default threshold, 0x03=default cumulative.</param>
    /// <returns>Raw log page data including the 4-byte page header, or null on failure.</returns>
    public byte[]? LogSense(byte pageCode, byte subPageCode = 0, byte pageControl = 0x00)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildLogSense(pageCode, subPageCode, pageControl, 512),
            ScsiDataDirection.In, 512)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        // Log page header (4 bytes):
        //   Byte 0: bits 7:6 = DS/SPF, bits 5:0 = Page Code
        //   Byte 1: Sub-Page Code
        //   Bytes 2-3: Page Length (big-endian, bytes following header)
        var pageLength = MmcCommands.ReadBE16(data, 2);
        var totalLength = 4 + pageLength;

        // If the response was truncated, re-issue with the correct allocation length
        if (totalLength > result.DataTransferred && totalLength > 512)
        {
            var allocLen = (ushort)Math.Min(totalLength, 65534);
            cmd = new ScsiCommand(
                MmcCommands.BuildLogSense(pageCode, subPageCode, pageControl, allocLen),
                ScsiDataDirection.In, allocLen)
            {
                TimeoutMs = 10_000
            };
            result = _transport.Execute(cmd);
            if (!result.Success || result.DataTransferred < 4)
                return null;
            data = cmd.DataBuffer;
        }

        var copyLen = Math.Min(totalLength, result.DataTransferred);
        var pageData = new byte[copyLen];
        Array.Copy(data, 0, pageData, 0, copyLen);
        return pageData;
    }

    /// <summary>
    /// Gets the list of supported log pages via LOG SENSE page 0x00.
    /// Returns an array of supported page codes or null if not supported.
    /// </summary>
    public byte[]? GetSupportedLogPages()
    {
        var data = LogSense(MmcCommands.LogPageSupportedPages);
        if (data == null || data.Length < 4) return null;

        var pageLength = MmcCommands.ReadBE16(data, 2);
        if (pageLength == 0 || data.Length < 4 + pageLength) return null;

        var pages = new byte[pageLength];
        Array.Copy(data, 4, pages, 0, pageLength);
        return pages;
    }

    /// <summary>
    /// Parses error counter log page data (pages 02h, 03h) into individual parameter values.
    /// Returns a list of (ParameterCode, Value) tuples.
    /// </summary>
    public System.Collections.Generic.List<LogParameter>? ParseErrorCounterLogPage(byte[] logPageData)
    {
        if (logPageData == null || logPageData.Length < 4) return null;

        var parameters = new System.Collections.Generic.List<LogParameter>();
        int offset = 4; // Skip page header

        while (offset + 4 <= logPageData.Length)
        {
            var paramCode = MmcCommands.ReadBE16(logPageData, offset);
            var paramFlags = logPageData[offset + 2];
            var paramLen = logPageData[offset + 3];

            if (offset + 4 + paramLen > logPageData.Length)
                break;

            ulong value = 0;
            for (int i = 0; i < paramLen && i < 8; i++)
            {
                value = (value << 8) | logPageData[offset + 4 + i];
            }

            parameters.Add(new LogParameter
            {
                ParameterCode = paramCode,
                Value = value,
                Flags = paramFlags
            });

            offset += 4 + paramLen;
        }

        return parameters;
    }

    // -----------------------------------------------------------------------
    // GET EVENT STATUS NOTIFICATION (4Ah) — complete event class support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets event status notification for the specified event class(es).
    /// Returns the raw event data including the 4-byte header, or null on failure.
    /// </summary>
    /// <param name="notificationClassRequest">Bitmask of requested event classes.</param>
    /// <returns>Parsed event status data or null on failure.</returns>
    public EventStatusData? GetEventStatusNotification(byte notificationClassRequest)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildGetEventStatusNotification(notificationClassRequest, 8),
            ScsiDataDirection.In, 8)
        {
            TimeoutMs = 5000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 4)
            return null;

        var data = cmd.DataBuffer;
        // Event Header (4 bytes):
        //   Bytes 0-1: Event Descriptor Length (big-endian, bytes following header)
        //   Byte 2: bit 7 = NEA (No Event Available), bits 2:0 = Notification Class
        //   Byte 3: Supported Event Classes (bitmask)
        var eventDescLen = MmcCommands.ReadBE16(data, 0);
        var nea = (data[2] & 0x80) != 0;
        var notificationClass = (byte)(data[2] & 0x07);
        var supportedClasses = data[3];

        var eventData = new EventStatusData
        {
            EventDescriptorLength = eventDescLen,
            NoEventAvailable = nea,
            NotificationClass = notificationClass,
            SupportedEventClasses = supportedClasses
        };

        // Parse class-specific event data (bytes 4+)
        if (!nea && result.DataTransferred >= 8)
        {
            eventData.EventByte0 = data[4];
            eventData.EventByte1 = data[5];
            eventData.EventByte2 = data[6];
            eventData.EventByte3 = data[7];
        }

        return eventData;
    }

    /// <summary>
    /// Gets media event status via GET EVENT STATUS NOTIFICATION (media class).
    /// Returns detailed media event information including media present status.
    /// </summary>
    public MediaEventData? GetMediaEventStatus()
    {
        var eventData = GetEventStatusNotification(MmcCommands.EventClassMedia);
        if (eventData == null) return null;

        // Media event (Notification Class = 4):
        //   Byte 4: bits 3:0 = Event Code (0=NoChange, 1=Eject, 2=NewMedia, 3=MediaRemoval, 4=MediaChanged, 5=BGFormatComplete, 6=BGFormatRestarted)
        //   Byte 5: bit 1 = Media Present, bit 0 = Door/Tray Open
        return new MediaEventData
        {
            EventCode = (byte)(eventData.EventByte0 & 0x0F),
            MediaPresent = (eventData.EventByte1 & 0x02) != 0,
            DoorTrayOpen = (eventData.EventByte1 & 0x01) != 0,
            StartSlot = eventData.EventByte2,
            EndSlot = eventData.EventByte3
        };
    }

    // -----------------------------------------------------------------------
    // MECHANISM STATUS (BDh) — read mechanical status of the drive
    // -----------------------------------------------------------------------

    /// <summary>
    /// Gets the mechanism status of the drive.
    /// Reports the current mechanical state including slot information for changers,
    /// door open/closed status, and current pickup position (LBA).
    /// </summary>
    public MechanismStatusData? GetMechanismStatus()
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildMechanismStatus(8),
            ScsiDataDirection.In, 8)
        {
            TimeoutMs = 10_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success || result.DataTransferred < 8)
            return null;

        var data = cmd.DataBuffer;
        // Mechanism Status Header (8 bytes) per MMC-5 Table 492:
        //   Byte 0: bit 7 = Fault (mechanism fault),
        //           bits 6:5 = Changer State (00=Ready, 01=Loading, 10=Unloading, 11=Initializing),
        //           bit 4 = Changer State Machine Changed,
        //           bits 2:0 = Current Slot (for changer mechanisms)
        //   Byte 1: bit 5 = Door Open,
        //           bits 4:0 = Mechanism State:
        //           000b = Idle, 001b = Playing, 010b = Scanning, 011b = Host Selected Active
        //   Bytes 2-4: Current LBA/optical pickup position (24-bit big-endian)
        //   Byte 5: Number of Slots Available (for changer mechanisms)
        //   Bytes 6-7: Slot Table Length (big-endian)
        var currentSlot = (byte)(data[0] & 0x07);
        var fault = (data[0] & 0x80) != 0;
        var changerStateChanged = (data[0] & 0x10) != 0;
        var doorOpen = (data[1] & 0x20) != 0;
        var mechanismState = (byte)(data[1] & 0x1F);
        uint currentLba = ((uint)data[2] << 16) | ((uint)data[3] << 8) | data[4];
        var numberOfSlots = data[5];
        var slotTableLength = MmcCommands.ReadBE16(data, 6);

        return new MechanismStatusData
        {
            CurrentSlot = currentSlot,
            Fault = fault,
            ChangerStateChanged = changerStateChanged,
            DoorOpen = doorOpen,
            MechanismState = mechanismState,
            CurrentLba = currentLba,
            NumberOfSlots = numberOfSlots,
            SlotTableLength = slotTableLength
        };
    }

    // -----------------------------------------------------------------------
    // FLUSH CACHE (35h) — alias for SYNCHRONIZE CACHE
    // -----------------------------------------------------------------------

    /// <summary>
    /// Flushes the drive's write cache (alias for SynchronizeCache).
    /// Ensures all data in the drive buffer is written to the medium.
    /// The SCSI SYNCHRONIZE CACHE command (35h) is often referred to as "Flush Cache".
    /// </summary>
    public bool FlushCache() => SynchronizeCache();

    // -----------------------------------------------------------------------
    // BLANK (A1h) — additional blank type methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Blanks a specific track on a CD-RW/DVD-RW disc.
    /// </summary>
    /// <param name="trackNumber">Track number to blank (as start address).</param>
    /// <param name="immediate">Whether to return immediately (true) or wait for completion (false).</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult BlankTrack(uint trackNumber, bool immediate = true)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(MmcCommands.BlankTypeTrack, trackNumber, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 3_600_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>
    /// Blanks the tail of a track on a CD-RW disc (from the specified address to the end of the track).
    /// </summary>
    /// <param name="startAddress">Start address for the blank operation.</param>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult BlankTrackTail(uint startAddress, bool immediate = true)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(MmcCommands.BlankTypeTrackTail, startAddress, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 3_600_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>
    /// Unreserves a track on a CD-RW disc.
    /// </summary>
    /// <param name="trackNumber">Track number to unreserve (as start address).</param>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult BlankUnreserveTrack(uint trackNumber, bool immediate = true)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(MmcCommands.BlankTypeUnreserve, trackNumber, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>
    /// Uncloses the last session on a CD-RW disc.
    /// </summary>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult BlankUncloseSession(bool immediate = true)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(MmcCommands.BlankTypeUncloseSession, 0, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000
        };
        return _transport.Execute(cmd);
    }

    /// <summary>
    /// Blanks only the last session on a CD-RW disc.
    /// </summary>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult BlankLastSession(bool immediate = true)
    {
        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();

        var cmd = new ScsiCommand(
            MmcCommands.BuildBlank(MmcCommands.BlankTypeSession, 0, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 3_600_000
        };
        return _transport.Execute(cmd);
    }

    // -----------------------------------------------------------------------
    // CLOSE TRACK/SESSION (5Bh) — convenience methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Closes a specific track.
    /// </summary>
    /// <param name="trackNumber">Track number to close.</param>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>True if the command succeeded.</returns>
    public bool CloseTrack(ushort trackNumber, bool immediate = true)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildCloseTrackSession(MmcCommands.CloseTrack, trackNumber, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000
        };
        var result = _transport.Execute(cmd);
        // Retry with IMM=0 if immediate mode fails
        if (!result.Success && immediate && (result.SenseKey == 0x05 || result.Asc == 0x24))
        {
            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(MmcCommands.CloseTrack, trackNumber, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
        }
        return result.Success;
    }

    /// <summary>
    /// Closes the current session without finalizing the disc.
    /// </summary>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>True if the command succeeded.</returns>
    public bool CloseSession(bool immediate = true)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSession, 0, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success && immediate && (result.SenseKey == 0x05 || result.Asc == 0x24))
        {
            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSession, 0, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
        }
        return result.Success;
    }

    /// <summary>
    /// Finalizes the disc (closes the last session and writes lead-out).
    /// After finalization, the disc cannot be written to further.
    /// </summary>
    /// <param name="immediate">Whether to return immediately.</param>
    /// <returns>True if the command succeeded.</returns>
    public bool FinalizeDisc(bool immediate = true)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSessionFinal, 0, immediate),
            ScsiDataDirection.None)
        {
            TimeoutMs = 600_000
        };
        var result = _transport.Execute(cmd);
        if (!result.Success && immediate && (result.SenseKey == 0x05 || result.Asc == 0x24))
        {
            cmd = new ScsiCommand(
                MmcCommands.BuildCloseTrackSession(MmcCommands.CloseSessionFinal, 0, false),
                ScsiDataDirection.None)
            {
                TimeoutMs = 600_000
            };
            result = _transport.Execute(cmd);
        }
        return result.Success;
    }

    // -----------------------------------------------------------------------
    // RESERVE TRACK (53h) — additional methods
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reserves a track with a specific size and returns the full SCSI result
    /// for detailed error handling.
    /// </summary>
    /// <param name="sizeInSectors">Number of sectors to reserve.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult ReserveTrackWithResult(uint sizeInSectors)
    {
        var cmd = new ScsiCommand(
            MmcCommands.BuildReserveTrack(sizeInSectors),
            ScsiDataDirection.None)
        {
            TimeoutMs = 30_000
        };
        return _transport.Execute(cmd);
    }

    // -----------------------------------------------------------------------
    // FORMAT UNIT (04h) — generic format method
    // -----------------------------------------------------------------------

    /// <summary>
    /// Performs a FORMAT UNIT with the given format parameter data.
    /// This is a low-level method that sends the raw format descriptor.
    /// For media-specific formatting, use FormatDvdPlusRw, FormatBdRe, etc.
    /// </summary>
    /// <param name="formatData">12-byte format parameter data (header + descriptor).</param>
    /// <param name="cmpLst">Whether the defect list is complete.</param>
    /// <returns>SCSI result of the operation.</returns>
    public ScsiResult FormatUnit(byte[] formatData, bool cmpLst = false)
    {
        if (formatData == null || formatData.Length < 12)
            throw new ArgumentException("Format data must be at least 12 bytes.", nameof(formatData));

        PrepareForDestructiveOperation();
        TestUnitReady();
        TestUnitReady();
        TestUnitReady();
        StartStopUnit(true);

        // StartStopUnit may generate UNIT ATTENTION — clear it and wait for ready
        TestUnitReady();
        WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);

        var cdb = MmcCommands.BuildFormatUnit(fmtData: true, cmpLst: cmpLst);
        var cmd = new ScsiCommand(cdb, ScsiDataDirection.Out, formatData)
        {
            TimeoutMs = 3_600_000
        };
        var result = _transport.Execute(cmd);

        // If FORMAT UNIT fails with "Not Ready" (sense key 0x02) or transport error,
        // wait for the drive to stabilize and retry. Common on macOS after DA unmount.
        if (!result.Success && (result.SenseKey == 0x02 || result.Status == 0xFF))
        {
            WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 500);
            StartStopUnit(true);
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        // If FORMAT UNIT fails with UNIT ATTENTION (sense key 0x06), retry after clearing
        if (!result.Success && result.SenseKey == 0x06)
        {
            TestUnitReady();
            result = _transport.Execute(cmd);
        }

        return result;
    }
}
