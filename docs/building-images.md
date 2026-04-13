---
layout: default
title: Building Images
permalink: /building-images
---

# Building Images

Open Burning Suite can create disc images from files and folders on your computer. This includes data disc images (ISO 9660, Joliet, UDF) and audio CD images.

---

## Data Disc Images

### Supported Filesystems

| Filesystem | Description | Compatibility |
|:-----------|:------------|:-------------|
| **ISO 9660** | Base standard for CD-ROM filesystems (ECMA-119) | Universal — works everywhere |
| **Joliet** | Microsoft extension to ISO 9660 with Unicode support | Windows, most modern OS |
| **UDF** | Universal Disk Format — modern replacement for ISO 9660 | DVD, Blu-ray, modern OS |
| **Rock Ridge** | POSIX extension to ISO 9660 (long filenames, permissions, symlinks) | Unix/Linux systems |
| **HFS+** | Apple filesystem for hybrid disc images | macOS systems |

> **Note:** HFS+ hybrid images are not fully built natively. Open Burning Suite creates the ISO 9660 portion of the image. For a native HFS+ wrapper, use Apple's `hdiutil` on macOS.

### Filesystem Selection Guide

| Use Case | Recommended Filesystem |
|:---------|:----------------------|
| Maximum compatibility | ISO 9660 |
| Windows with long filenames | Joliet |
| DVD or Blu-ray data disc | UDF |
| Linux/Unix systems | Rock Ridge |
| Cross-platform with long names | Joliet + Rock Ridge |

---

## Build Options

### Basic Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Source Folder** | Directory containing files to include in the image | — (required) |
| **Output Image Path** | Where to save the generated image file | — (required) |
| **Disc Type** | Target disc type (CD, DVD-5, DVD-9, BD-25, BD-50, etc.) | CD |
| **Filesystem** | Filesystem format (ISO 9660, Joliet, UDF, Rock Ridge, Mixed) | ISO 9660 |

### Volume Metadata

| Option | Description | Max Length |
|:-------|:------------|:----------|
| **Volume Label** | Name of the disc as shown in file managers | 32 characters |
| **Publisher** | Publisher identifier | — |
| **Preparer** | Data preparer identifier | — |
| **Application** | Application identifier | — |

> **Note:** The ISO 9660 volume identifier is limited to 32 uppercase characters per ECMA-119 §8.4.1. Open Burning Suite automatically truncates and uppercases the volume label for ISO 9660 compliance.

### Filesystem Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Joliet Long Filenames** | Allow filenames longer than the 64-character Joliet limit | On |
| **Rock Ridge Extensions** | Include POSIX metadata (permissions, symlinks, long names) | Off |
| **Deep Directory Nesting** | Allow directory nesting deeper than 8 levels (ISO 9660 limit) | Off |
| **Allow Lowercase** | Allow lowercase characters in ISO 9660 filenames | Off |
| **Allow Special Characters** | Allow special characters in filenames | Off |
| **UDF Version** | UDF filesystem version (1.02, 1.5, 2.01, 2.50, 2.60) | 1.02 |

### Advanced Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Pad to Capacity** | Pad the image to fill the target disc capacity | Off |
| **Sort Files** | Sort files in a specific order within the image | Off |
| **Input Charset** | Character encoding of source file names | UTF-8 |
| **Output Charset** | Character encoding for the image filesystem | UTF-8 |

---

## Bootable Disc Images

Open Burning Suite supports creating bootable CD and DVD images using the **El Torito** specification.

### Options

| Option | Description |
|:-------|:------------|
| **Bootable** | Enable boot support in the image |
| **Boot Image Path** | Path to the boot image file (e.g., isolinux.bin, GRUB image, UEFI loader) |
| **Emulation Mode** | No Emulation (modern, most common), Floppy (1.2/1.44/2.88 MB legacy), or Hard Disk |
| **Platform** | x86 (BIOS), PowerPC, Mac, or EFI (UEFI) |
| **Load Segment** | Memory address to load the boot image (hex, 0 = default 0x07C0) |
| **Sector Count** | Number of 512-byte virtual sectors to load (0 = auto-detect) |
| **Patch Boot Info Table** | Required by ISOLINUX and GRUB — patches the boot image with LBA and size |
| **EFI Dual-Boot Image** | Optional second boot image for UEFI, enabling dual BIOS+UEFI boot |

### Common Boot Scenarios

- **BIOS boot CD:** Use a floppy image (1.44 MB) or no-emulation boot image
- **UEFI boot DVD:** Use a FAT-formatted UEFI boot image
- **Linux live CD/DVD:** Typically uses isolinux/syslinux boot loader

---

## Audio CD Images

Open Burning Suite can build audio CD images from audio files. The built-in audio converter (powered by NAudio) handles format conversion automatically.

### Audio Track Properties

| Property | Description | Default |
|:---------|:------------|:--------|
| **File Path** | Path to the audio file | — (required) |
| **Title** | Track title (for CD-TEXT) | — |
| **Artist** | Track artist (for CD-TEXT) | — |
| **Pre-Gap** | Silence before the track (seconds) | 2 |
| **ISRC** | International Standard Recording Code | — |
| **Copy Permitted** | Allow digital copying of this track | Off |
| **Four Channel** | Mark as 4-channel (quadraphonic) audio | Off |
| **Pre-Emphasis** | Apply pre-emphasis flag | Off |

### CD-TEXT Metadata

| Property | Description |
|:---------|:------------|
| **Album** | Album title for the entire disc |
| **Artist** | Album artist for the entire disc |

> **Note:** CD-TEXT is embedded in the disc's subchannel data and displayed by compatible CD players and car stereos.

---

## VCD / SVCD / XSVCD Images

Open Burning Suite can build **Video CD (VCD)**, **Super Video CD (SVCD)**, and **eXtended Super Video CD (XSVCD)** disc images in BIN/CUE format using CD-ROM XA Mode 2 sectors.

| Feature | VCD | SVCD | XSVCD |
|:--------|:----|:-----|:------|
| **Video codec** | MPEG-1 | MPEG-2 | MPEG-2 |
| **Max resolution** | 352×240 (NTSC), 352×288 (PAL) | 480×480 (NTSC), 480×576 (PAL) | 720×480 (NTSC), 720×576 (PAL) |
| **Mux rate** | 1,394 kbps (fixed) | Up to 2,778 kbps (VBR) | Up to 3,500 kbps (VBR) |
| **Capacity** | ~74–80 min per CD | ~35–40 min per CD | ~25–35 min per CD |
| **Sector type** | Mode 2 Form 2 (2,328 bytes user data) | Mode 2 Form 2 (2,328 bytes user data) | Mode 2 Form 2 (2,328 bytes user data) |

VCD/SVCD/XSVCD images include the required filesystem structure (VCD directory, INFO.VCD/INFO.SVD, ENTRIES, etc.) and optional **CD-i application stub** for playback on Philips CD-i players.

### CD-XA Marker

VCD, SVCD, and XSVCD images include the `CD-XA001` marker in the Primary Volume Descriptor as required by the CD-ROM XA specification. For data ISO images, this marker can also be injected via the **CD-XA Mode** build option.

---

## DVD-Video / Blu-ray Disc Authoring

Open Burning Suite includes DVD-Video and Blu-ray disc authoring capabilities. Video transcoding is handled via FFmpeg (must be installed separately).

### DVD-Video

Builds a compliant **VIDEO_TS** directory structure containing IFO, BUP, and VOB files from input video files. The `DvdAuthoringBuilder` handles muxing and structure generation.

### Blu-ray BDMV

Builds a standard **BDMV** directory structure containing index.bdmv, MPLS playlists, CLPI clip info, M2TS streams, and AUXDATA/META/BACKUP directories.

### BDAV (Blu-ray Recording)

**BDAV** is the Blu-ray recording format used by BD recorders. Unlike BDMV, BDAV uses a simplified **BDAV/** directory structure, application type `0x06`, and omits AUXDATA, META, and BACKUP directories.

### Blu-ray 3D

Builds stereoscopic 3D Blu-ray discs using **MVC (Multiview Video Coding)**. Supported 3D modes:

- **MVC (Frame-packed)** — Full resolution per eye via dependent MVC stream
- **Side-by-Side (SBS)** — Half horizontal resolution per eye
- **Top-and-Bottom (TAB)** — Half vertical resolution per eye

3D authoring generates the additional **SSIF** (Stereoscopic Interleaved File) directory and extended CLPI/MPLS metadata with depth offset information.

> **Note:** FFmpeg is required for all video authoring features. It must be installed and available on the system PATH or in a common installation location.

---

## Progress Tracking

During an image build, Open Burning Suite reports:

- **Percent complete** — Overall build progress
- **Bytes processed** — Amount of data processed
- **Total bytes** — Total size of source data
- **Current file** — File currently being added to the image
- **Elapsed time** — Time since build started
- **Status messages** — Detailed build log

---

## Tips & Best Practices

1. **Choose the right filesystem for your target.**
   - Burning for Windows PCs? Use **Joliet**.
   - Burning for Linux servers? Use **Rock Ridge**.
   - Burning a DVD or Blu-ray? Use **UDF**.

2. **Keep volume labels short and uppercase.** ISO 9660 limits labels to 32 uppercase characters. Longer or mixed-case labels will be automatically adjusted.

3. **Use "Pad to Capacity" for gaming discs.** Some gaming consoles expect the disc to be filled to a specific capacity.

4. **Set the correct disc type.** This ensures the image size is validated against the target disc capacity before building.

5. **Pre-emphasis is rarely needed.** Only enable it if your audio source was recorded with pre-emphasis encoding.

---

**Next:** [Verification →]({{ '/verification' | relative_url }})
