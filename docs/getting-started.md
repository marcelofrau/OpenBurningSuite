---
layout: default
title: Getting Started
permalink: /getting-started
---

# Getting Started

This guide walks you through installing, building, and running Open Burning Suite on your platform.

---

## Prerequisites

### .NET 8.0 SDK

Open Burning Suite requires the **.NET 8.0 SDK** (or later). Download it from the official site:

[Download .NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

Verify your installation:

```bash
dotnet --version
# Should output 8.0.x or later
```

### Optical Drive

You'll need a physical optical disc drive connected to your system for burning, reading, and verification operations. Image building works without a drive.

### Optional External Tools

Open Burning Suite uses two optional external tools for advanced features. Neither is required for basic burning, reading, or disc operations.

#### FFmpeg

[FFmpeg](https://ffmpeg.org/) is required for video authoring (DVD-Video, Blu-ray, BDAV, Blu-ray 3D, VCD, SVCD, XSVCD).

Install FFmpeg and ensure `ffmpeg` is available on your system PATH.

#### chdman (MAME CHD Tools)

[chdman](https://www.mamedev.org/release.html) is required for reading and extracting CHD (Compressed Hunks of Data) disc images, commonly used for game preservation. You need at least **chdman v0.287** (included with MAME).

| Detection Method | Description |
|:-----------------|:------------|
| **PATH** | If `chdman` (or `chdman.exe` on Windows) is on your system PATH, it is detected automatically. |
| **Settings** | You can set a custom path in **Settings → Paths & Locations → chdman Path**. Use the **Browse** button to select the executable, then click **Test** to verify it works. |
| **Common locations** | The application also searches standard install paths: `C:\mame\chdman.exe` (Windows), `/opt/homebrew/bin/chdman` (macOS), `/usr/bin/chdman` (Linux). |

If chdman is not installed, the application will show a clear error message when you attempt to work with CHD files.

### Platform-Specific Requirements

#### Windows

- **Administrator privileges** are required for direct SCSI passthrough access to optical drives.
- Windows 10 or later is recommended.
- The application uses `IOCTL_SCSI_PASS_THROUGH_DIRECT` for hardware communication.

#### Linux

- **Root or sudo access** is required for SCSI passthrough via SG_IO.
- Your user needs read/write permissions on `/dev/sg*` and `/dev/sr*` device nodes.
- Alternatively, add your user to the `cdrom` and `optical` groups:

  ```bash
  sudo usermod -aG cdrom,optical $USER
  # Log out and back in for changes to take effect
  ```

- Ensure the `sg` (SCSI generic) kernel module is loaded:

  ```bash
  sudo modprobe sg
  ```

#### macOS

- Appropriate permissions for SCSI passthrough are required.
- macOS may prompt you to allow direct hardware access.
- The application uses the **IOKit SCSI Architecture Model** framework (`SCSITaskDeviceInterface`) for hardware communication.
- On newer macOS versions (Sequoia and later), granting **Full Disk Access** may be required. See [macOS.md](../macOS.md) for details.

---

## Building from Source

### Clone the Repository

```bash
git clone https://github.com/SvenGDK/OpenBurningSuite.git
cd OpenBurningSuite
```

### Restore Dependencies & Build

```bash
# Debug build (default)
dotnet build

# Release build (optimized)
dotnet build -c Release
```

The build process automatically restores the following NuGet packages:

| Package | Version | Purpose |
|:--------|:--------|:--------|
| Avalonia | 11.3.12 | Cross-platform UI framework |
| Avalonia.Desktop | 11.3.12 | Desktop platform integration |
| Avalonia.Themes.Fluent | 11.3.12 | Fluent design theme |
| Avalonia.Fonts.Inter | 11.3.12 | Inter font family |
| Avalonia.Controls.DataGrid | 11.3.12 | DataGrid control |
| DiscUtils.Iso9660 | 0.16.13 | ISO 9660 image creation |
| NAudio | 2.2.1 | Audio file processing |

### Publish as Self-Contained (Optional)

To create a self-contained executable that doesn't require the .NET SDK on the target machine:

```bash
# Windows (x64)
dotnet publish -c Release -r win-x64 --self-contained

# Windows (ARM64)
dotnet publish -c Release -r win-arm64 --self-contained

# Linux (x64)
dotnet publish -c Release -r linux-x64 --self-contained

# Linux (ARM64)
dotnet publish -c Release -r linux-arm64 --self-contained

# macOS (Intel)
dotnet publish -c Release -r osx-x64 --self-contained

# macOS (Apple Silicon)
dotnet publish -c Release -r osx-arm64 --self-contained
```

---

## Running the Application

### From Source

```bash
dotnet run --project OpenBurningSuite
```

### From Build Output

```bash
# Debug
./OpenBurningSuite/bin/Debug/net8.0/OpenBurningSuite

# Release
./OpenBurningSuite/bin/Release/net8.0/OpenBurningSuite
```

### On Linux with Elevated Permissions

```bash
sudo dotnet run --project OpenBurningSuite
# Or
sudo ./OpenBurningSuite/bin/Release/net8.0/OpenBurningSuite
```

---

## First Run

When you launch Open Burning Suite for the first time:

1. **Discover drives** — The application will automatically detect any connected optical drives and display their information (vendor, model, firmware revision, capabilities).

2. **Insert a disc** — Insert a disc into your drive. The application will detect the media type and display disc information (capacity, used space, sessions, tracks).

3. **Choose an operation** — Use the sidebar to navigate:
   - 🔍 **Discover** — list and inspect optical drives
   - 💿 **Copy Disc** — read/duplicate discs to image formats
   - 🏗 **Build Image** — create disc images from files/folders
   - 🔥 **Burn / Write** — burn a pre-built image or files directly to disc
   - ✅ **Verify** — verify disc integrity with checksums
   - ⚙️ **Advanced** — erase, format, finalize, and other disc operations
   - **Quick Start Wizards** — guided step-by-step workflows:
     - 🎵 **Audio & Music** — create audio CDs, copy music to disc, or rip audio CDs
     - 🎬 **Video Disc** — author DVD-Video, Blu-ray, VCD, SVCD, or XSVCD discs
     - 📁 **Data Disc** — create and burn data discs from files and folders
     - 🎮 **Game Disc** — use console-specific presets for gaming discs
     - 💿 **Copy Disc** — copy a disc to an image file with gaming presets and PS3 decryption
     - 🧹 **Blank Disc** — erase rewritable discs or format blank media

---

## Troubleshooting

### "No drives detected"

- Ensure your optical drive is properly connected and recognized by the OS.
- On Linux, check that device nodes exist: `ls -la /dev/sr* /dev/sg*`
- On Linux, verify the `sg` module is loaded: `lsmod | grep sg`

### "Permission denied" errors

- **Linux:** Run with `sudo` or ensure your user is in the `cdrom`/`optical` groups.
- **Windows:** Run the application as Administrator.
- **macOS:** Grant hardware access when prompted. On macOS Sequoia and later, you may need to grant **Full Disk Access** (System Settings → Privacy & Security → Full Disk Access). See [macOS.md](../macOS.md) for details.

### Build errors

- Ensure you have .NET 8.0 SDK installed: `dotnet --version`
- Try cleaning and rebuilding: `dotnet clean && dotnet build`
- Check your internet connection (NuGet packages need to be downloaded on first build).

---

**Next:** [Burning Discs →]({{ '/burning' | relative_url }})
