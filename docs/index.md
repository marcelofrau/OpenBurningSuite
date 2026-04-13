---
layout: default
title: Home
permalink: /
---

# Open Burning Suite

**A cross-platform CD / DVD / HD DVD / Blu-ray burning desktop utility built with native SCSI/MMC commands.**

---

## What is Open Burning Suite?

Open Burning Suite is a free, open-source disc burning application for **Windows**, **Linux**, and **macOS**. It lets you burn, copy, create, and verify CDs, DVDs, HD DVDs, and Blu-ray discs — all from one app. Step-by-step wizards for audio, video, data, and gaming discs make it easy to get started, while advanced options give experienced users full control.

Under the hood, Open Burning Suite talks directly to your optical drive using native **SCSI/MMC commands**, so there are no external tools to install (such as `cdrecord`, `wodim`, or `growisofs`). Built with [.NET 8](https://dotnet.microsoft.com/) and [Avalonia UI](https://avaloniaui.net/), it provides a modern, consistent experience across all platforms with the Fluent design theme.

---

## Key Highlights

| | Feature | Description |
|:--:|:--------|:------------|
| 🔥 | **Burn** | Burn CD, DVD, HD DVD, and Blu-ray discs with TAO, SAO, DAO, and RAW write modes |
| 📀 | **Read** | Read discs to ISO, BIN/CUE, CCD/IMG/SUB, NRG, MDF/MDS, IMG, TOC/BIN, CDI, VCD, SVCD, and XSVCD formats |
| 💿 | **Build** | Create ISO 9660, Joliet, UDF, Rock Ridge, and HFS+ disc images from files and folders |
| 🎵 | **Audio** | Create audio CDs with CD-TEXT, rip audio CDs to WAV/BIN/CUE, copy music to disc, import M3U/PLS/WPL/ASX playlists |
| 🎬 | **Video** | DVD-Video, Blu-ray BDMV, BDAV, Blu-ray 3D, VCD/SVCD/XSVCD authoring via FFmpeg |
| ✅ | **Verify** | Verify disc integrity with MD5, SHA-1, SHA-256, SHA-512, and CRC32 checksums |
| 🎮 | **Gaming** | Support for 20+ consoles: PlayStation, Nintendo, Sega, Xbox, and retro platforms |
| 🔒 | **Encryption** | AES-256-CBC disc image encryption with password protection and PS3 disc decryption |
| 🧙 | **Wizards** | Step-by-step Quick Start wizards for Audio, Video, Data, Gaming, Copy, and Blank/Erase discs |
| 🖥️ | **Cross-platform** | Runs natively on Windows, Linux, and macOS |
| 🔧 | **Native** | Direct SCSI/MMC hardware control — zero external tool dependencies for disc operations |

---

## Supported Media

Open Burning Suite supports a wide range of optical media:

- **CD:** CD-R, CD-RW (74, 80, 90, 99 min)
- **DVD:** DVD-R, DVD+R, DVD-RW, DVD+RW, DVD-RAM, DVD-R DL, DVD+R DL
- **HD DVD:** HD DVD-R, HD DVD-R DL, HD DVD-RW, HD DVD-RAM
- **Blu-ray:** BD-R, BD-RE, BD-R XL (25 / 50 / 100 / 128 GB)
- **UHD Blu-ray:** UHD BD-66, UHD BD-100
- **M-DISC:** M-DISC DVD, M-DISC BD-R (SL / DL / XL)
- **Gaming:** GameCube miniDVD, Wii, GD-ROM, UMD, and more

---

## Quick Start

```bash
# Clone and build
git clone https://github.com/SvenGDK/OpenBurningSuite.git
cd OpenBurningSuite
dotnet build

# Run
dotnet run --project OpenBurningSuite
```

See the [Getting Started]({{ '/getting-started' | relative_url }}) guide for detailed installation instructions.

---

## Documentation Overview

| Page | Description |
|:-----|:------------|
| [Getting Started]({{ '/getting-started' | relative_url }}) | Installation, prerequisites, and first run |
| [Burning Discs]({{ '/burning' | relative_url }}) | How to burn images to optical media |
| [Reading Discs]({{ '/reading' | relative_url }}) | How to read discs to image files |
| [Building Images]({{ '/building-images' | relative_url }}) | How to create ISO and audio disc images |
| [Verification]({{ '/verification' | relative_url }}) | How to verify discs and images |
| [Gaming Discs]({{ '/gaming-discs' | relative_url }}) | Support for PlayStation, Xbox, Nintendo, Sega, and retro platforms |
| [Supported Formats]({{ '/supported-formats' | relative_url }}) | Complete list of supported media and formats |
| [Architecture]({{ '/architecture' | relative_url }}) | Technical architecture and design |
| [FAQ]({{ '/faq' | relative_url }}) | Frequently asked questions |

---

## License

Open Burning Suite is released under the [BSD 2-Clause License](https://github.com/SvenGDK/OpenBurningSuite/blob/main/LICENSE).
