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

```mermaid
graph TD
    subgraph Views["Avalonia UI Views"]
        V1["Discover / Copy / Build / Burn / Write / Verify"]
        V2["Advanced / Settings / Help"]
        V3["AudioWizard / VideoWizard / DataWizard / GameWizard"]
        V4["CopyWizard / BlankWizard / DiscInfoView"]
        V5["DiscVisualizationControl (SkiaSharp) / SplashWindow"]
    end

    subgraph Services["Services Layer"]
        S1["BurnService"]
        S2["ReadService"]
        S3["BuildService"]
        S4["VerifyService"]
        S5["DiscDiscoveryService"]
        S6["DiscInfoService"]
        S7["DiscEncryptionService"]
        S8["GamingDiscService"]
        S9["SettingsService"]
        S10["Ps3DiscService / Xbox360StealthService / XisoService"]
    end

    subgraph Engine["Native Engine Layer"]
        E1["Optical: BurnEngine / ReadEngine / OpticalDrive"]
        E2["Iso: IsoBuilder / VcdBuilder / HfsPlusBuilder"]
        E3["Audio: AudioConverter / AudioCdBuilder / CdTextEncoder"]
        E4["Video: BluRayAuthoringBuilder / DvdAuthoringBuilder"]
        E5["Toc: TocCueConverter / Format parsers"]
    end

    subgraph SCSI["SCSI / MMC Command Layer"]
        C1["MmcCommands / ScsiCommand / ScsiResult"]
    end

    subgraph Transport["Platform SCSI Transport Layer"]
        T1["WindowsScsiTransport (IOCTL_SCSI_PASS_THROUGH_DIRECT)"]
        T2["LinuxScsiTransport (SG_IO)"]
        T3["MacOsScsiTransport (IOKit SCSITaskLib)"]
    end

    subgraph Platform["Platform Utilities Layer"]
        U1["NativeDeviceDiscovery (.Windows / .Linux / .macOS)"]
        U2["NativeEject"]
        U3["NativePrivilege"]
    end

    V1 --> S1 & S2 & S3 & S4
    V2 --> S9
    V3 --> S1 & S3
    V4 --> S5 & S6 & S8
    V5 --> S5

    S1 --> E1
    S2 --> E1
    S3 --> E2
    S4 --> E1
    S6 --> E1
    S7 --> C1

    E1 --> C1
    E2 --> C1
    E3 --> C1
    E4 --> C1

    C1 --> T1 & T2 & T3
    T1 & T2 & T3 --> U1 & U2 & U3
```

---

## Layer Descriptions

### Avalonia UI Views

The top layer contains 16 views plus a splash window and a custom SkiaSharp-based disc visualization control. Views are primarily written in AXAML with C# code-behind, following a code-behind pattern (no formal MVVM framework — bindings are done manually via property change notification).

**View categories:**
- **Main operations:** Discover, Copy Disc, Build Image, Burn/Write, Verify
- **Configuration:** Advanced, Settings, Help
- **Quick Start Wizards:** Audio, Video, Data, Game, Copy, Blank/Erase
- **Information:** Disc Info panel, Disc Visualization
- **Overlay:** Splash Window (startup)

### Services Layer

Services encapsulate business logic for each major operation. They coordinate between views and the native engine layer:

| Service | Responsibility |
|:--------|:---------------|
| `BurnService` | Coordinates burn operations across media types, manages CHD conversion |
| `ReadService` | Handles disc reading to all output formats |
| `BuildService` | Creates ISO/UDF/HFS+ filesystem images |
| `VerifyService` | Performs checksum verification and disc-to-image comparison |
| `DiscDiscoveryService` | Detects and enumerates optical drives |
| `DiscInfoService` | Retrieves detailed disc/drive information (MID, ATIP, formats, speeds) |
| `DiscEncryptionService` | AES-256-CBC encryption/decryption of disc images (.obse) |
| `GamingDiscService` | Console-specific disc detection and preset logic |
| `SettingsService` | Persistent user settings (chdman path, preferences) |
| `Ps3DiscService` | PS3 IRD/dkey/hex decryption support |
| `Xbox360StealthService` | Xbox 360 stealth patching |

### Native Engine Layer

The engine layer implements the actual disc operations. It is split into domains:

**Optical Engine:**
- `BurnEngine` — SCSI WRITE commands, buffer management, burn strategy
- `ReadEngine` — SCSI READ commands, sector extraction, error recovery
- `OpticalDrive` — Drive capabilities, media type detection, speed negotiation

**Iso/Disc Image Engine:**
- `IsoBuilder`, `VcdBuilder`, `HfsPlusBuilder` — filesystem image creation
- `RockRidgeExtensions` / `RockRidgeReader` — POSIX extensions
- `CcdParser`/`Writer`, `CdiParser`/`Writer`, `MdfParser`/`Writer` — format support
- `NrgParser`/`Writer`, `ImgParser`/`Writer` — additional format support

**Audio Engine:**
- `AudioConverter` — Audio file transcoding
- `AudioCdBuilder` — Red Book audio CD creation with CD-TEXT
- `CdTextEncoder` — CD-TEXT pack encoding
- `PlaylistParser` — M3U/PLS/WPL/ASX import

**Video Engine:**
- `BluRayAuthoringBuilder` — BDMV/BDAV structure authoring
- `DvdAuthoringBuilder` — VIDEO_TS structure authoring
- `VideoTranscoder` — FFmpeg video transcoding adapter

### SCSI / MMC Command Layer

This layer defines the SCSI command set used for optical drive communication:

```mermaid
sequenceDiagram
    participant App as Application
    participant Scsi as ScsiCommand
    participant Transport as SCSI Transport
    participant Drive as Optical Drive

    App->>Scsi: Create command (opcode, data, direction)
    Scsi->>Scsi: Build CDB (Command Descriptor Block)
    Scsi->>Transport: Execute(cdb, buffer, timeout)
    Transport->>Drive: Send SCSI command via platform API
    Drive-->>Transport: SCSI status + data
    Transport-->>Scsi: ScsiResult (status, sense data)
    Scsi-->>App: Process result
```

Key components:
- `MmcCommands` — Static definitions of MMC opcodes and parameter pages
- `ScsiCommand` — Encapsulates CDB construction, buffer allocation, timeout handling
- `ScsiResult` — Parses sense data, extracts error information

### Platform SCSI Transport Layer

Each platform has its own SCSI transport implementation behind the `IScsiTransport` interface:

| Platform | Implementation | API |
|:---------|:---------------|:----|
| Windows | `WindowsScsiTransport` | `IOCTL_SCSI_PASS_THROUGH_DIRECT` via `DeviceIoControl` |
| Linux | `LinuxScsiTransport` | `SG_IO` ioctl on `/dev/sg*` |
| macOS | `MacOsScsiTransport` | IOKit `SCSITaskDeviceInterface` |

The factory pattern (`ScsiTransportFactory`) selects the correct transport at runtime based on `OSPlatform`.

### Platform Utilities Layer

Cross-platform helpers for system integration:

```mermaid
graph LR
    subgraph Windows
        W1["SetupAPI device enumeration"]
        W2["IOCTL_STORAGE_EJECT_MEDIA"]
        W3["WindowsPrincipal.IsInRole"]
    end
    subgraph Linux
        L1["/sys/class/scsi_generic/ enumeration"]
        L2["SG_IO eject via MMC"]
        L3["geteuid() == 0 check"]
    end
    subgraph macOS
        M1["IOKit registry iteration"]
        M2["IOKit eject via SCSITask"]
        M3["EUID check"]
    end

    NativeDeviceDiscovery --> W1 & L1 & M1
    NativeEject --> W2 & L2 & M2
    NativePrivilege --> W3 & L3 & M3
```

---

## Application Startup Flow

```mermaid
sequenceDiagram
    participant Main as Program.cs
    participant App as App.axaml.cs
    participant Splash as SplashWindow
    participant MainWnd as MainWindow
    participant Disc as DiscDiscoveryService

    Main->>App: BuildAvaloniaApp().StartWithClassicDesktopLifetime()
    App->>Splash: Show splash window
    Splash-->>Splash: Display logo + version + loading
    App->>Disc: Detect optical drives
    Disc-->>App: Drive list
    App->>MainWnd: Create MainWindow with drive data
    MainWnd-->>MainWnd: Populate sidebar, set up views
    App->>Splash: Close splash
    App->>MainWnd: Show main window
```

---

## Burn Operation Flow

```mermaid
sequenceDiagram
    participant View as WriteView
    participant Service as BurnService
    participant Chd as ChdHelper
    participant Engine as BurnEngine
    participant Scsi as ScsiCommand
    participant Drive as OpticalDrive

    View->>Service: StartBurn(job)
    Service->>Service: Determine media type & write mode

    alt Source is CHD
        Service->>Chd: ConvertChdToBinCue(path)
        Chd-->>Service: BIN/CUE temp path
    end

    Service->>Engine: Prepare burn image
    Engine->>Drive: Get write parameters
    Drive-->>Engine: Write speed, buffer size

    loop Each write unit
        Engine->>Scsi: WRITE (10/12/16) command
        Scsi->>Drive: Execute SCSI write
        Drive-->>Scsi: Write status
        Scsi-->>Engine: Result
        Engine-->>Service: Progress callback
    end

    Service->>Engine: Close session / finalize
    Engine->>Scsi: CLOSE TRACK / SESSION
    alt Verify enabled
        Service->>Service: Read back & compare checksums
    end
    Service-->>View: BurnResult (success)
```

---

## Disc Information Retrieval

```mermaid
sequenceDiagram
    participant View as DiscInfoView
    participant Service as DiscInfoService
    participant Manuf as MediaManufacturerLookup
    participant Drive as OpticalDrive
    participant Scsi as MmcCommands

    View->>Service: GetDiscInfo(drive)
    Service->>Drive: Select media
    Service->>Scsi: GET CONFIGURATION
    Scsi-->>Service: Feature descriptors
    Service->>Scsi: READ DISC INFORMATION
    Scsi-->>Service: Disc structure
    Service->>Scsi: READ TRACK INFORMATION
    Scsi-->>Service: Track info

    alt Media is CD
        Service->>Scsi: READ TOC/PMA/ATIP
        Scsi-->>Service: ATIP data
        Service->>Manuf: Lookup manufacturer by ATIP code
    else Media is DVD or BD
        Service->>Scsi: READ DISC INFORMATION (Physical Format)
        Scsi-->>Service: Media ID (MID)
        Service->>Manuf: Lookup manufacturer by MID
    end

    Service->>Drive: Query capabilities
    Drive-->>Service: Write speeds, buffer size, feature bits
    Service-->>View: DiscInfoResult (full report)
```

---

## Key Design Decisions

### Why Native SCSI Instead of CLI Tools?

Unlike most disc burning applications that wrap command-line tools (`cdrecord`, `wodim`, `growisofs`, `mkisofs`), Open Burning Suite communicates directly with the optical drive:

- **No external dependencies** — the application is self-contained
- **Consistent behavior** — same SCSI commands on every platform
- **Better error reporting** — direct access to sense data and drive status
- **Finer control** — raw sector access, subchannel data, custom write modes

### Code Example: Executing a SCSI Command

```csharp
// From ScsiCommand.cs — simplified
public ScsiResult Execute(IScsiTransport transport, byte[] cdb,
                          byte[] buffer, int timeoutMs)
{
    var result = transport.Execute(cdb, buffer, timeoutMs);

    if (result.Status == ScsiStatus.CheckCondition)
    {
        var sense = SenseData.Parse(result.SenseBuffer);
        if (sense.IsRecoverableError)
            return new ScsiResult(result.Data, sense, isWarning: true);

        throw new ScsiException(sense);
    }

    return new ScsiResult(result.Data, senseData: null);
}
```

### Why Code-Behind Instead of MVVM?

The original upstream project used code-behind for view logic. This fork maintains that pattern for consistency and incremental migration. New features (Disc Info, Icon system) introduce helper classes and services that can be extracted into a formal ViewModel layer in a future refactor.

---

## SCSI Command Categories

| Category | Commands | Example Opcodes |
|:---------|:---------|:----------------|
| **Inquiry** | GET CONFIGURATION, FEATURE, EVENT STATUS | 46h, 4Ah, 4Dh |
| **Read** | READ (10/12/16), READ CD, READ TOC/PMA/ATIP | 28h, A8h, BEh |
| **Write** | WRITE (10/12/16), WRITE AND VERIFY | 2Ah, AAh, 2Eh |
| **Session** | CLOSE TRACK/SESSION, RESERVE TRACK, SYNCHRONIZE CACHE | 5Bh, 53h, 35h |
| **Media** | READ DISC INFORMATION, READ TRACK INFORMATION | 51h, 52h |
| **Mode** | MODE SENSE (6/10), MODE SELECT (6/10) | 1Ah, 5Ah, 15h, 55h |
| **Misc** | START STOP UNIT, PREVENT ALLOW MEDIUM REMOVAL, LOAD/UNLOAD MEDIUM | 1Bh, 1Eh, A6h |

---

## Image Format Support Pipeline

```mermaid
graph LR
    subgraph Input["Input Image Formats"]
        I1["ISO (.iso)"]
        I2["BIN/CUE (.bin+.cue)"]
        I3["NRG (.nrg)"]
        I4["MDF/MDS (.mdf+.mds)"]
        I5["CCD/IMG/SUB"]
        I6["CDI (.cdi)"]
        I7["TOC/BIN"]
        I8["CHD (.chd)"]
    end

    subgraph Convert["Conversion Pipeline"]
        C1["Parser → Internal Sector Representation"]
        C2["chdman → BIN/CUE" ]
    end

    subgraph Output["Output / Burn Format"]
        O1["BIN/CUE (intermediate)"]
        O2["RAW sectors → SCSI WRITE"]
    end

    I1 --> C1
    I2 --> C1
    I3 --> C1
    I4 --> C1
    I5 --> C1
    I6 --> C1
    I7 --> C1
    I8 --> C2 --> C1
    C1 --> O1 --> O2
```

---

## Service Dependencies

```mermaid
graph TD
    BurnSvc["BurnService"] --> OpticalDrive
    BurnSvc --> ChdHelper
    BurnSvc --> BurnEngine
    ReadSvc["ReadService"] --> OpticalDrive
    ReadSvc --> ReadEngine
    BuildSvc["BuildService"] --> IsoBuilder
    BuildSvc --> AudioCdBuilder
    VerifySvc["VerifyService"] --> OpticalDrive
    DiscInfoSvc["DiscInfoService"] --> OpticalDrive
    DiscInfoSvc --> MediaManufacturerLookup
    DiscDiscoverySvc["DiscDiscoveryService"] --> NativeDeviceDiscovery
    DriveWatcherSvc["DriveWatcherService"] --> WindowsDeviceNotifier

    style BurnSvc fill:#1A7CE0,color:#fff
    style ReadSvc fill:#1A7CE0,color:#fff
    style BuildSvc fill:#1A7CE0,color:#fff
    style VerifySvc fill:#1A7CE0,color:#fff
    style DiscInfoSvc fill:#1A7CE0,color:#fff
```

---

## Project Structure

```
OpenBurningSuite/
├── OpenBurningSuite.slnx        # Solution file (.slnx format)
├── OpenBurningSuite.csproj      # .NET 8 project
├── Program.cs                   # Entry point
├── App.axaml / App.axaml.cs     # Application + theme
│
├── Models/                      # Data models
│   ├── AppSettings.cs           # User configuration
│   ├── BurnJob.cs               # Burn operation parameters
│   ├── BurnProgress.cs          # Burn progress tracking
│   ├── DiscDrive.cs             # Drive representation
│   ├── DiscInfoResult.cs        # Disc information result
│   └── ...                      # (20+ model classes)
│
├── Services/                    # Business logic
│   ├── BurnService.cs
│   ├── ReadService.cs
│   ├── BuildService.cs
│   ├── VerifyService.cs
│   ├── DiscInfoService.cs
│   ├── DiscDiscoveryService.cs
│   ├── DiscEncryptionService.cs
│   ├── GamingDiscService.cs
│   └── ...
│
├── Native/                      # Native engine
│   ├── Optical/                 # BurnEngine, ReadEngine, OpticalDrive
│   ├── Audio/                   # AudioCdBuilder, AudioConverter
│   ├── Video/                   # VideoTranscoder, BluRay/DVD authoring
│   ├── Scsi/                    # SCSI commands + platform transports
│   ├── Platform/                # NativeDeviceDiscovery, Eject, Privilege
│   └── Toc/                     # TOC/CUE conversion
│
├── Controls/                    # Custom Avalonia controls
│   ├── IconButton.cs
│   ├── IconTextBlock.cs
│   ├── IconConverter.cs
│   └── IconSourceExtension.cs
│
├── Helpers/                     # Utilities
│   ├── ChdHelper.cs             # CHD extraction via chdman
│   ├── IconHelper.cs            # PNG icon loading
│   ├── Logger.cs                # Logging with ms precision
│   └── MediaManufacturerLookup.cs  # 150+ vendor IDs
│
└── Views/                       # AXAML views (16 views + splash)
    ├── MainWindow.axaml/.cs
    ├── SplashWindow.axaml/.cs
    ├── WriteView.axaml/.cs
    ├── ReadView.axaml/.cs
    ├── DiscInfoView.axaml/.cs
    └── ...
```
