<div align="center">

# Running Open Burning Suite on macOS

Step-by-step guide for installing and running Open Burning Suite on macOS.

</div>

## Prerequisites

- macOS 10.15 (Catalina) or later
- An optical disc drive (for burning, reading, and verification operations)

## Optional: FFmpeg

[FFmpeg](https://ffmpeg.org/) is required for video authoring features (DVD-Video, Blu-ray, BDAV, Blu-ray 3D, VCD, SVCD). It is **not** needed for disc burning, reading, building data/audio images, or verification.

Install FFmpeg via [Homebrew](https://brew.sh/):

```bash
brew install ffmpeg
```

## Downloading a Release

Download the release that matches your Mac from the [Releases](https://github.com/SvenGDK/OpenBurningSuite/releases) page.

### Portable ZIPs

| File | Description |
|---|---|
| `osx-x64.zip` | macOS Intel |
| `osx-arm64.zip` | macOS Apple Silicon |

### PKG Installers

| File | Description |
|---|---|
| `osx-x64-Installer.pkg` | macOS Intel |
| `osx-arm64-Installer.pkg` | macOS Apple Silicon |

### DMG Disk Images

| File | Description |
|---|---|
| `osx-x64.dmg` | macOS Intel |
| `osx-arm64.dmg` | macOS Apple Silicon |

## Installing from a Portable ZIP

1. Extract the downloaded ZIP — double-click the file in Finder or use the terminal:

```bash
unzip osx-arm64.zip -d OpenBurningSuite
```

2. Navigate to the extracted directory:

```bash
cd OpenBurningSuite
```

3. Make the binary executable:

```bash
chmod +x OpenBurningSuite
```

4. Run the application:

```bash
./OpenBurningSuite
```

## Installing from a PKG Installer

1. Double-click the `.pkg` file to open the installer.
2. Follow the on-screen instructions to install **Open Burning Suite** to `/Applications`.
3. Launch the application from **Applications** or Spotlight.

## Installing from a DMG Disk Image

1. Double-click the `.dmg` file to mount the disk image.
2. Drag **OpenBurningSuite.app** to the **Applications** folder.
3. Eject the disk image.
4. Launch the application from **Applications** or Spotlight.

### Gatekeeper Warning

macOS may block the application because it was not downloaded from the App Store. If you see a **"cannot be opened because the developer cannot be verified"** dialog:

1. Open **System Settings** → **Privacy & Security**
2. Scroll down and click **Open Anyway** next to the Open Burning Suite message
3. Alternatively, right-click the application and select **Open**

Or remove the quarantine attribute from the terminal:

```bash
xattr -rd com.apple.quarantine OpenBurningSuite.app
# Or for the portable binary:
xattr -rd com.apple.quarantine OpenBurningSuite
```

## SCSI Access & Permissions

Open Burning Suite communicates directly with optical drives using native **SCSI/MMC commands** via the macOS **IOKit SCSI Architecture Model** framework. On macOS, optical drives appear as `/dev/diskN` devices and are accessed through IOKit's `SCSITaskDeviceInterface`.

### Hardware Access Prompts

macOS may prompt you to allow direct hardware access when the application first attempts to communicate with an optical drive. **Grant this permission** — it is required for all disc operations (burning, reading, verifying, erasing).

### Running with Elevated Privileges

If the application cannot access your optical drive, try running with `sudo`:

```bash
sudo /Applications/OpenBurningSuite.app/Contents/MacOS/OpenBurningSuite
# Or for the portable binary:
sudo ./OpenBurningSuite
```

### Full Disk Access (macOS Sequoia and Later)

Newer versions of macOS may require granting **Full Disk Access** to the application:

1. Open **System Settings** → **Privacy & Security** → **Full Disk Access**
2. Click the **+** button and add **Open Burning Suite**
3. Restart the application

## Troubleshooting

### "No drives detected"

- Ensure your optical drive is properly connected (USB or Thunderbolt).
- Check that macOS recognizes the drive in **System Information** → **USB** or **Thunderbolt**.
- Click **Refresh Drives** in the Discover view — optical drives may take a few seconds to be recognized after connection, especially USB drives.
- Try running with `sudo` (see [Running with Elevated Privileges](#running-with-elevated-privileges)).
- Try a different USB port — some USB controllers may not support SCSI passthrough.
- Check **System Information** → **Disc Burning** to verify macOS recognizes the drive.

### "Permission denied" or "ObtainExclusiveAccess failed" when accessing drive

- Grant hardware access when prompted by macOS.
- Run the application with `sudo`.
- Check **Full Disk Access** settings (see above).
- Close any other applications that may be using the optical drive (e.g., Finder, Disk Utility, iTunes/Music).
- If you see error code `0xE00002C5`, another process has exclusive access to the drive.

### Burning fails or media information is incorrect

- Eject and re-insert the disc, then click **Probe Media** to refresh media information.
- Ensure the disc is compatible with your drive (e.g., BD-R media requires a Blu-ray writer).
- Close Finder windows showing the disc before burning — macOS may prevent exclusive access while the disc is mounted.

### Fonts look incorrect

Install a common font package via Homebrew:

```bash
brew install font-liberation
```
