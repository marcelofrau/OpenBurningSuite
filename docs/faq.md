---
layout: default
title: FAQ
permalink: /faq
---

# Frequently Asked Questions

---

## General

### What is Open Burning Suite?

Open Burning Suite is a free, open-source, cross-platform optical disc burning application. It supports burning, reading, building, and verifying CD, DVD, HD DVD, and Blu-ray discs on Windows, Linux, and macOS.

### How is Open Burning Suite different from other burning tools?

Unlike most disc burning applications that rely on external command-line tools (cdrecord, wodim, growisofs, mkisofs), Open Burning Suite communicates **directly** with your optical drive using native SCSI/MMC commands. This means:

- **No external tool dependencies** — everything is built-in
- **Consistent behavior** across all platforms
- **Better error reporting** from direct hardware communication
- **Finer control** over all disc operations

### What platforms are supported?

- **Windows** 10 or later (x64, ARM64)
- **Linux** (x64, ARM64) — any distribution with .NET 8 support
- **macOS** (x64, Apple Silicon)

### Is Open Burning Suite free?

Yes! Open Burning Suite is free and open-source, released under the [BSD 2-Clause License](https://github.com/SvenGDK/OpenBurningSuite/blob/main/LICENSE).

---

## Installation & Setup

### What do I need to install?

You need the [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build and run Open Burning Suite from source. Alternatively, you can use a self-contained published build that includes the .NET runtime.

### Do I need an optical drive?

For burning, reading, and verification operations — yes, you need a physical optical drive. For building disc images from files/folders, no drive is needed.

### Why do I need administrator / root access?

Disc burning requires direct hardware access via SCSI passthrough commands. Operating systems restrict this access for security:

- **Windows:** Requires running as Administrator to send SCSI commands via `IOCTL_SCSI_PASS_THROUGH_DIRECT`
- **Linux:** Requires root access or membership in the `cdrom`/`optical` groups for SG_IO ioctl access
- **macOS:** May prompt for hardware access permissions

### The application doesn't detect my drive. What should I do?

1. **Check the connection:** Ensure your drive is properly connected (internal SATA, external USB)
2. **Check permissions:** On Linux, try running with `sudo`
3. **Check device nodes (Linux):** Run `ls -la /dev/sr* /dev/sg*` to see if devices exist
4. **Load the sg module (Linux):** Run `sudo modprobe sg`
5. **Try a different USB port:** For external drives, some USB ports may not support SCSI passthrough

---

## Burning

### What write speed should I use?

- **Audio CDs:** 4x–8x for best quality
- **Data CDs:** Maximum speed is usually fine
- **DVDs:** 8x is a good balance of speed and quality
- **Blu-ray:** 4x–6x recommended
- **Uncertain?** Use "Auto" and let the drive choose the optimal speed

### What is simulation mode?

Simulation mode performs all steps of a burn — laser calibration, data transfer, timing — without actually writing to the disc. Use it to test your setup before committing to a real burn.

### Can I burn multiple copies?

Yes. Set the "Copies" count in the Write view. After each copy, the disc is ejected and the application waits for you to insert a new blank disc.

### What is buffer underrun protection?

Buffer underrun occurs when the drive's internal buffer runs empty during a burn (e.g., due to a slow computer or heavy system load). Buffer underrun protection (also known as BURN-Free, SafeBurn, or JustLink) pauses the burn and resumes seamlessly when the buffer refills. It is enabled by default and strongly recommended.

### What is overburn?

Overburn writes data beyond the rated capacity of a disc. For example, writing more than 80 minutes on a standard 80-minute CD-R. Not all drives and media support overburn, and it may reduce compatibility with some readers. Use with caution.

---

## Reading

### Which output format should I use?

| Format | Best For |
|:-------|:---------|
| **ISO** | Data discs, general use, maximum compatibility |
| **BIN/CUE** | Audio CDs, exact copies, multi-track discs |
| **CDI** | Dreamcast games |
| **NRG, MDF/MDS** | Compatibility with specific tools (Nero, Alcohol 120%) |
| **IMG** | Simple raw copies |

### What is audio paranoia mode?

Audio paranoia performs error-corrected reading of audio CDs by reading each sector multiple times, comparing reads, and interpolating damaged samples. It's slower but produces better results from scratched or damaged discs.

### What are subchannel modes?

Subchannels are additional data streams embedded alongside the main data on a CD. They can contain CD-TEXT, copy protection data, or other metadata. Capturing subchannel data is mainly useful for exact disc copies or copy-protected discs.

---

## Building Images

### What filesystem should I use?

| Target | Recommended |
|:-------|:------------|
| Windows PCs | Joliet |
| Linux systems | Rock Ridge |
| DVDs / Blu-rays | UDF |
| Maximum compatibility | ISO 9660 |
| Cross-platform | Joliet + Rock Ridge |

### Why is my volume label being changed?

ISO 9660 volume identifiers are limited to **32 uppercase characters** per the ECMA-119 standard. Open Burning Suite automatically truncates and uppercases volume labels to comply with this requirement.

### Can I create bootable discs?

Yes. Enable the "Bootable" option and provide a boot image file (e.g., an El Torito boot image). The boot image is embedded in the ISO according to the El Torito specification.

### Can I create audio CDs?

Yes. You can use the **Audio & Music Wizard** in the sidebar for a guided step-by-step experience, or add audio tracks manually in the Build view. Configure CD-TEXT metadata (album, artist), set per-track properties (title, ISRC, pre-gap), and Open Burning Suite converts audio files to the required PCM format automatically.

### Can I rip audio CDs?

Yes. The **Audio & Music Wizard** includes a "Rip Audio CD" option that extracts audio tracks to individual WAV files, or saves the full disc as a raw BIN/CUE audio image. Advanced options include audio paranoia mode (for scratched discs) and jitter correction for maximum accuracy.

### Can I copy music files to a disc?

Yes. The **Audio & Music Wizard** has a "Copy Music to Disc" mode that lets you add MP3, FLAC, WAV, OGG, AIFF, AAC, WMA, and M4A files and burn them to a data disc.

### What are the Quick Start Wizards?

The Quick Start Wizards are guided step-by-step workflows accessible from the sidebar:

- **🎵 Audio & Music Wizard** — Create audio CDs, copy music files to disc, or rip audio CDs to WAV/BIN/CUE
- **🎬 Video Disc Wizard** — Author DVD-Video, Blu-ray, BDAV, Blu-ray 3D, VCD, SVCD, or XSVCD discs
- **📁 Data Disc Wizard** — Create and burn data discs from files and folders
- **🎮 Game Disc Wizard** — Use console-specific presets for gaming disc creation and burning
- **💿 Copy Disc Wizard** — Copy a disc to an image file with gaming presets and PS3 decryption support
- **🧹 Blank Disc Wizard** — Erase rewritable discs or format blank media

Each wizard walks you through the process step by step: choose a task, configure options, review a summary, and execute.

---

## Verification

### How long does verification take?

Verification speed depends on disc read speed and size. A typical DVD at 8x takes about 8–10 minutes. Blu-ray discs take longer due to their larger capacity.

### Which checksum algorithm should I use?

- **Quick check:** CRC32 (fastest)
- **Standard:** MD5 (good balance)
- **Maximum assurance:** SHA-256 (most secure)

### Can I verify without the original image?

Yes. The "Verify Disc" mode reads every sector and checks for read errors without needing the original image. However, this only checks physical readability, not data accuracy. For data accuracy verification, use "Compare Disc to Image" with the source image file.

### How does verification work with mixed-mode CDs (e.g., PS1 games)?

Mixed-mode CDs contain both data and audio tracks. Open Burning Suite handles these by:
- Reading each track with the correct **Expected Sector Type** (data or CD-DA audio) via the SCSI READ CD command
- Properly handling the **150-frame pregap** between data and audio tracks, which contains audio-format sectors
- Using **sync-pattern-based classification** to ensure both the disc and image sides hash the same data
- Enable **Audio Track Verification** in the Verify view to include audio tracks in the comparison — without it, only data tracks are compared

### Does verification support both single-file and multi-file BIN/CUE?

Yes. Both layouts are fully supported. Single-file CUE images (one `.bin` file containing all tracks) and multi-file CUE images (one `.bin` file per track) produce identical verification results because the engine uses the same per-sector sync-pattern classification for both.

---

## Gaming Discs

### Which gaming consoles are supported?

PlayStation 1–5, PSP, Nintendo GameCube, Wii (SL/DL), Wii U, Nintendo DS/3DS (verification only), Xbox, Xbox 360, Xbox One/Series, Sega Dreamcast, Saturn, Mega CD, PC Engine/TurboGrafx-CD, Neo Geo CD, 3DO, Philips CD-i, Amiga CD32, Amiga CDTV, and Atari Jaguar CD.

### Can Open Burning Suite decrypt game discs?

Open Burning Suite supports **PS3 disc decryption**. The **Game Wizard** and **Write** view support three key sources: IRD files (.ird), disc key files (.dkey), or direct hex key input. The **Copy Disc Wizard** supports decryption using IRD files (.ird). Select the "Decrypt PS3 disc" option and provide the matching key. Other encrypted gaming disc formats are not supported.

### Do I need a special drive for gaming discs?

Standard optical drives can read most gaming discs. However, some formats (like GD-ROM for Dreamcast) use proprietary high-density areas that cannot be read by standard drives.

### Can I encrypt disc images?

Yes. Open Burning Suite includes a built-in disc image encryption feature using **AES-256-CBC** with PBKDF2 key derivation. Encrypted images are saved with the `.obse` extension. You can encrypt any disc image with a password and decrypt it later for burning or verification.

---

## Troubleshooting

### Build fails with "SDK not found"

Ensure you have the .NET 8.0 SDK installed:

```bash
dotnet --version
# Should output 8.0.x or later
```

If not installed, download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8.0).

### "Permission denied" when accessing drive

- **Linux:** Run with `sudo` or add your user to the `cdrom` and `optical` groups: `sudo usermod -aG cdrom,optical $USER`
- **Windows:** Run the application as Administrator
- **macOS:** Allow hardware access when prompted

### Burn fails with "Medium not present"

Ensure a blank disc is inserted and the drive has recognized it. Try:

1. Ejecting and reinserting the disc
2. Waiting a few seconds for the drive to spin up
3. Checking if the disc type is compatible with your drive

### Burn fails with "Write error"

Common causes:

- **Disc is not blank** — use a new blank disc or erase a rewritable disc
- **Speed too high** — try lowering the write speed
- **Incompatible media** — try a different disc brand
- **Drive issue** — clean the drive lens or try a different drive

---

**Have a question not covered here?** [Open an issue](https://github.com/SvenGDK/OpenBurningSuite/issues/new) on GitHub.
