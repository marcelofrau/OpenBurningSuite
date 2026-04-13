---
layout: default
title: Architecture
permalink: /architecture
---

# Architecture & Internals

This page describes the technical architecture of Open Burning Suite, its layered design, and how the major components interact.

---

## High-Level Architecture

Open Burning Suite follows a clean **layered architecture** where each layer depends only on the layer below it:

```
┌──────────────────────────────────────────────────────────────┐
│                       Avalonia UI Views                       │
│  Discover · Copy Disc · Build Image · Burn/Write · Verify    │
│  Advanced · Settings · Help                                   │
│  AudioWizard · VideoWizard · DataWizard · GameWizard          │
│  CopyWizard · BlankWizard                                     │
│  DiscVisualizationControl (SkiaSharp)                        │
├──────────────────────────────────────────────────────────────┤
│                       Services Layer                          │
│  BurnService · ReadService · BuildService · VerifyService    │
│  DiscDiscoveryService · DiscEncryptionService                │
│  GamingDiscService · SettingsService                         │
│  Ps3DiscService · Xbox360StealthService · XisoService        │
├──────────────────────────────────────────────────────────────┤
│                    Native Engine Layer                        │
│  Optical: BurnEngine · ReadEngine · OpticalDrive             │
│  Iso: IsoBuilder · VcdBuilder · ElToritoWriter               │
│       HfsPlusBuilder · RockRidgeExtensions/Reader             │
│       CcdParser/Writer · CdiParser/Writer · MdfParser/Writer  │
│       NrgParser/Writer · ImgParser/Writer                     │
│  Audio: AudioConverter · AudioCdBuilder · CdTextEncoder      │
│         PlaylistParser · AudioReaderFactory                   │
│  Video: BluRayAuthoringBuilder · DvdAuthoringBuilder         │
│         VideoTranscoder (FFmpeg)                              │
│  Toc: TocCueConverter                                        │
├──────────────────────────────────────────────────────────────┤
│                 SCSI / MMC Command Layer                      │
│           MmcCommands · ScsiCommand · ScsiResult              │
├──────────────────────────────────────────────────────────────┤
│              Platform SCSI Transport Layer                    │
│  WindowsScsiTransport (IOCTL_SCSI_PASS_THROUGH_DIRECT)      │
│  LinuxScsiTransport (SG_IO)                                  │
│  MacOsScsiTransport (IOKit SCSITaskLib)                      │
├──────────────────────────────────────────────────────────────┤
│                Platform Utilities Layer                       │
│  NativeDeviceDiscovery · NativeEject · NativePrivilege       │
└──────────────────────────────────────────────────────────────┘
```

---

## Layer Descriptions

### 1. UI Layer (`Views/`)

The presentation layer built with **Avalonia UI** using the Fluent theme. The main window provides sidebar navigation across multiple views:

| View | File | Purpose |
|:-----|:-----|:--------|
| **MainWindow** | `MainWindow.axaml` | App shell, sidebar navigation, status display |
| **DiscoverView** | `DiscoverView.axaml` | Drive detection and disc info |
| **WriteView** | `WriteView.axaml` | Burn configuration and execution |
| **ReadView** | `ReadView.axaml` | Read configuration and execution |
| **BuildView** | `BuildView.axaml` | Image building from files/folders |
| **VerifyView** | `VerifyView.axaml` | Verification operations |
| **AdvancedView** | `AdvancedView.axaml` | Erase, finalize, and advanced disc operations |
| **AudioWizardView** | `AudioWizardView.axaml` | Audio CD creation, music copy, and CD ripping |
| **VideoWizardView** | `VideoWizardView.axaml` | DVD-Video, Blu-ray, BDAV, 3D, VCD/SVCD/XSVCD authoring |
| **DataWizardView** | `DataWizardView.axaml` | Guided data disc creation |
| **GameWizardView** | `GameWizardView.axaml` | Gaming disc preset-driven workflows |
| **CopyWizardView** | `CopyWizardView.axaml` | Guided disc copy with gaming presets and PS3 decryption |
| **BlankWizardView** | `BlankWizardView.axaml` | Guided disc erase and format operations |
| **SettingsView** | `SettingsView.axaml` | Application settings and preferences |
| **HelpView** | `HelpView.axaml` | In-app help documentation with TOC navigation |
| **DiscVisualizationControl** | `DiscVisualizationControl.cs` | Real-time disc visualization (SkiaSharp) |

### 2. Services Layer (`Services/`)

The orchestration layer that coordinates between the UI and native engines. Services handle high-level business logic, progress reporting, and error management.

| Service | Responsibility |
|:--------|:--------------|
| **BurnService** | Orchestrates burn operations, multi-copy management, disc eject polling |
| **ReadService** | Dispatches read operations to the appropriate format handler |
| **BuildService** | Manages ISO, audio, VCD/SVCD/XSVCD, DVD-Video, Blu-ray, and BDAV image construction workflows |
| **VerifyService** | Coordinates verification operations, checksum computation (MD5, SHA-1, SHA-256, SHA-512, CRC32) |
| **VcdSvcdVerifier** | VCD/SVCD-specific disc image structural validation |
| **MdfMdsVerifier** | MDF/MDS-specific disc image structural validation |
| **DiscDiscoveryService** | Wraps platform-specific drive enumeration |
| **DiscContentDetectionService** | Detects disc content type (data, audio, video, gaming) from drive or image; recommends output format, sector size, and subchannel mode |
| **DiscEncryptionService** | Encrypts and decrypts disc images using AES-256-CBC with PBKDF2 key derivation (.obse format) |
| **GamingDiscService** | Detects gaming systems, applies presets, manages gaming-specific parameters |
| **Ps3DiscService** | PlayStation 3 disc handling: PARAM.SFO parsing, IRD/.dkey/hex-key decryption, region map processing |
| **Xbox360StealthService** | Xbox 360 disc handling: DMI/PFI/SS extraction, XGD2/XGD3 game detection |
| **XisoService** | Xbox ISO (XDVDFS) handling: reads and extracts files from Xbox DVD filesystem images |
| **SettingsService** | Manages loading and saving of application settings to a JSON file |

### 3. Native Engine Layer (`Native/`)

The core implementation layer containing the actual disc operation logic, organized into subdirectories:

#### Optical (`Native/Optical/`)

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **BurnEngine** | `BurnEngine.cs` | Writes image data to disc in 16-sector chunks, parses CUE sheets, handles SAO and DAO write modes |
| **ReadEngine** | `ReadEngine.cs` | Reads sectors with configurable error handling, paranoia modes |
| **OpticalDrive** | `OpticalDrive.cs` + `OpticalDriveModels.cs` | High-level drive abstraction (inquiry, disc info, media detection, format, erase, eject) with associated data models |

#### ISO / Image Formats (`Native/Iso/`)

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **IsoBuilder** | `IsoBuilder.cs` | Builds ISO 9660/Joliet/UDF images using DiscUtils |
| **VcdBuilder** | `VcdBuilder.cs` | Builds VCD, SVCD, and XSVCD disc images (CD-ROM XA Mode 2) |
| **ElToritoWriter** | `ElToritoWriter.cs` | Writes El Torito boot records for bootable disc images |
| **HfsPlusBuilder** | `HfsPlusBuilder.cs` | HFS+ hybrid disc image support |
| **RockRidgeExtensions** | `RockRidgeExtensions.cs` | Rock Ridge POSIX extension writing |
| **RockRidgeReader** | `RockRidgeReader.cs` | Rock Ridge POSIX extension reading |
| **CcdParser / CcdWriter** | `CcdParser.cs`, `CcdWriter.cs` | CloneCD image format (CCD/IMG/SUB) reading and writing |
| **CdiParser / CdiWriter** | `CdiParser.cs`, `CdiWriter.cs` | DiscJuggler image format (CDI v2.0–4.0) reading and writing |
| **MdfParser / MdfWriter** | `MdfParser.cs`, `MdfWriter.cs` | Alcohol 120% image format (MDF/MDS) reading and writing |
| **NrgParser / NrgWriter** | `NrgParser.cs`, `NrgWriter.cs` | Nero image format (NRG v1/v2) reading and writing |
| **ImgParser / ImgWriter** | `ImgParser.cs`, `ImgWriter.cs` | Raw disc image format (IMG) reading and writing |
| **SubParser / SubWriter** | `SubParser.cs`, `SubWriter.cs` | Subchannel data (SUB) parsing, format detection, CRC-16 validation, and writing |

#### Audio (`Native/Audio/`)

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **AudioConverter** | `AudioConverter.cs` | Converts audio formats for CD burning using NAudio |
| **AudioCdBuilder** | `AudioCdBuilder.cs` | Builds Red Book audio CD images |
| **CdTextEncoder** | `CdTextEncoder.cs` | Encodes CD-TEXT metadata for audio CDs |
| **PlaylistParser** | `PlaylistParser.cs` | Loads and saves M3U, M3U8, PLS, WPL, ASX playlists |
| **AudioReaderFactory** | `AudioReaderFactory.cs` | Creates audio file readers for various formats |

#### Video (`Native/Video/`)

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **DvdAuthoringBuilder** | `DvdAuthoringBuilder.cs` | Builds DVD-Video directory structures (VIDEO_TS) |
| **BluRayAuthoringBuilder** | `BluRayAuthoringBuilder.cs` | Builds Blu-ray BDMV, BDAV, and 3D disc structures |
| **VideoTranscoder** | `VideoTranscoder.cs` | FFmpeg-based video transcoding for DVD, Blu-ray, BDAV, 3D, VCD/SVCD/XSVCD |

#### TOC / CUE (`Native/Toc/`)

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **TocCueConverter** | `TocCueConverter.cs` | Parses and converts CUE sheets and TOC files |

#### Platform Utilities (`Native/Platform/`)

| Component | File(s) | Responsibility |
|:----------|:--------|:--------------|
| **NativeDeviceDiscovery** | `NativeDeviceDiscovery.cs` + `.Linux.cs` / `.Windows.cs` / `.macOS.cs` | Platform-specific optical drive enumeration (partial class per OS) |
| **NativeEject** | `NativeEject.cs` + `.Linux.cs` / `.Windows.cs` / `.macOS.cs` | Platform-specific disc tray eject/load (partial class per OS) |
| **NativePrivilege** | `NativePrivilege.cs` | Platform-specific privilege elevation checks |

### 4. SCSI / MMC Command Layer (`Native/Scsi/`)

Builds and interprets SCSI Command Descriptor Blocks (CDBs) per the **MMC-5** (Multimedia Commands) standard:

| Component | File | Responsibility |
|:----------|:-----|:--------------|
| **MmcCommands** | `MmcCommands.cs` | Static builders for MMC commands: INQUIRY, READ(10), WRITE(10), WRITE(12), BLANK, READ DISC INFO, READ TRACK INFO, TEST UNIT READY, etc. |
| **ScsiCommand** | `ScsiCommand.cs` | Encapsulates a SCSI CDB with direction and transfer length |
| **ScsiResult** | `ScsiResult.cs` | Parses SCSI response data and sense data (fixed/descriptor format) |

### 5. Platform Transport Layer (`Native/Scsi/`)

Platform-specific implementations of the `IScsiTransport` interface:

| Transport | File | Platform | Mechanism |
|:----------|:-----|:---------|:----------|
| **IScsiTransport** | `IScsiTransport.cs` | All | Interface defining the SCSI command execution contract |
| **WindowsScsiTransport** | `WindowsScsiTransport.cs` | Windows | `IOCTL_SCSI_PASS_THROUGH_DIRECT` via P/Invoke to `kernel32.dll` |
| **LinuxScsiTransport** | `LinuxScsiTransport.cs` | Linux | `SG_IO` ioctl on `/dev/sg*` or `/dev/sr*` devices |
| **MacOsScsiTransport** | `MacOsScsiTransport.cs` | macOS | IOKit SCSITaskLib SCSI passthrough |
| **ScsiTransportFactory** | `ScsiTransportFactory.cs` | All | Factory that creates the correct transport based on `RuntimeInformation.IsOSPlatform()` |

---

## Key Design Decisions

### Native SCSI/MMC Implementation

**Why:** Traditional disc burning applications rely on external command-line tools (cdrecord, wodim, growisofs, mkisofs). This creates dependency management issues, inconsistent error handling, and platform-specific tool availability problems.

**Solution:** Open Burning Suite implements all disc operations directly using SCSI/MMC-5 commands sent through platform-specific SCSI passthrough mechanisms. This provides:

- Zero external runtime dependencies
- Consistent behavior across platforms
- Direct hardware error reporting via SCSI sense data
- Fine-grained control over all disc operations

### Platform Abstraction via Interface

The `IScsiTransport` interface defines a single method for executing SCSI commands:

```
IScsiTransport
├── WindowsScsiTransport  →  IOCTL_SCSI_PASS_THROUGH_DIRECT
├── LinuxScsiTransport    →  SG_IO ioctl
└── MacOsScsiTransport    →  macOS passthrough
```

`ScsiTransportFactory` selects the correct implementation at runtime based on the detected platform.

### Write Strategy: 16-Sector Chunks

BurnEngine writes data in **16-sector chunks** (16 × 2048 = 32,768 bytes per write command). WRITE(10) is used for CD media (16-bit transfer length) and WRITE(12) for DVD/BD media (32-bit transfer length for reduced command overhead). This balances:

- Drive buffer efficiency (large enough to minimize command overhead)
- Progress granularity (small enough for responsive progress updates)
- Compatibility (universally supported transfer size)

### Tray Lock Safety

BurnEngine wraps all write operations in a `try/finally` block to ensure the disc tray is **always unlocked** (`PREVENT/ALLOW MEDIUM REMOVAL`) even if an exception occurs. This prevents discs from being permanently locked in the drive after a failed burn.

### Sense Data Handling

SCSI sense data is handled in both **fixed format** (response codes `0x70`/`0x71`) and **descriptor format** (response codes `0x72`/`0x73`). The sense key, ASC (Additional Sense Code), and ASCQ (Additional Sense Code Qualifier) are extracted at the correct offsets for each format.

---

## Data Models (`Models/`)

### Job Models

Each operation type has a corresponding "job" model that carries all parameters:

| Model | Properties |
|:------|:-----------|
| **BurnJob** | Image path, CUE path, device, write mode/speed, copies, simulation, buffer underrun protection, overburn, multi-session, BD-R mode, layer break |
| **ReadJob** | Device, output path/format, speed, error recovery, sector size, subchannel mode, paranoia, gaming preset |
| **BuildJob** | Source folder, output path, disc type, filesystem, volume metadata, boot options, audio tracks, CD-TEXT, gaming system |
| **VerifyJob** | Device, image path, verification mode, checksum algorithm, subchannel/audio verification |
| **BluRayAuthoringJob** | Video files, playlists, BDMV/BDAV mode, 3D stereoscopic settings, depth offset |
| **DvdAuthoringJob** | Video files, DVD-Video authoring parameters |

### Progress Models

Each job type has a progress model for real-time status reporting:

| Model | Key Fields |
|:------|:-----------|
| **BurnProgress** | Percent, speed, bytes written/total, elapsed/remaining, status message |
| **ReadProgress** | Percent, sectors read/total/error, speed, elapsed/remaining |
| **BuildProgress** | Percent, bytes processed/total, current file, elapsed |
| **VerifyProgress** | Percent, sectors verified/total, good/bad sectors, elapsed/remaining |
| **VerifyResult** | Checksum, sector counts, error details |

### Disc Models

| Model | Purpose |
|:------|:--------|
| **DiscDrive** | Drive capabilities, supported speeds, media type detection |
| **DriveCapabilities** | Per-format read/write profile capabilities and feature flags (DriveProfileCapabilities, DriveFeatureCapabilities) |
| **DiscMedia** | Media capacity, used space, sessions, tracks, volume label |
| **DiscContentInfo** | Detected disc content classification (data, audio, video, gaming) with format recommendations |
| **BluRayContentInfo** | Blu-ray content type classification (Data, Movie, PS3/PS4/PS5 Game) |

### Image Format Models

| Model | Purpose |
|:------|:--------|
| **CcdImage** | CloneCD image structure and metadata |
| **CdiImage** | DiscJuggler image structure and metadata |
| **MdfImage** | Alcohol 120% image structure and metadata |
| **NrgImage** | Nero image structure and metadata |
| **ImgImage** | Raw disc image structure and metadata |

### Application Models

| Model | Purpose |
|:------|:--------|
| **AppSettings** | Application-wide settings and preferences, persisted as JSON |
| **GamingPreset** | Read/write settings for a specific gaming console's disc format (sector size, subchannel, speed, write mode) |

---

## Helpers (`Helpers/`)

| Helper | Purpose |
|:-------|:--------|
| **PlatformHelper** | Runtime platform detection: `IsLinux`, `IsWindows`, `IsMacOS`; default device paths |
| **FormatHelper** | Disc capacity constants for all media types; supported format lists; write mode strings |

---

## External Dependencies

| Library | Purpose | Used By |
|:--------|:--------|:--------|
| [DiscUtils.Iso9660](https://github.com/DiscUtils/DiscUtils) | ISO 9660, Joliet, and UDF filesystem building | `IsoBuilder` |
| [NAudio](https://github.com/naudio/NAudio) | Audio file reading, format conversion, CD audio encoding | `AudioConverter`, `AudioCdBuilder`, `AudioReaderFactory` |
| [Avalonia](https://avaloniaui.net/) | Cross-platform UI framework with XAML | All `Views/` |
| [FFmpeg](https://ffmpeg.org/) *(optional, runtime)* | Video transcoding for DVD-Video, Blu-ray, BDAV, 3D, VCD/SVCD/XSVCD | `VideoTranscoder` |

> **Note:** FFmpeg is an optional runtime dependency — it is required for video authoring features but not for disc burning, reading, building, or verification. It is detected at runtime on the system PATH or common installation locations.

---

## Code Conventions

- **Nullable reference types** are enabled project-wide (`<Nullable>enable</Nullable>`)
- **Static compiled Regex** patterns with `RegexOptions.IgnoreCase | RegexOptions.Compiled`
- **XML doc comments** on all public APIs
- **Byte-to-uint casting** before bit-shifting to prevent signed integer promotion issues
- **Path traversal protection** — IsoBuilder validates all paths resolve within the source folder
- **Process deadlock prevention** — stdout/stderr are drained before `WaitForExit()` on all `Process.Start` calls

---

**Next:** [FAQ →]({{ '/faq' | relative_url }})
