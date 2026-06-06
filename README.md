<div align="center">

<img src="OpenBurningSuite/icon.png" width="128px">

# Open Burning Suite

> **Active fork** by [marcelofrau](https://github.com/marcelofrau) — the [upstream project](https://github.com/SvenGDK/OpenBurningSuite) appears to be stalled, so I'm continuing development here with new features, bug fixes, and UI improvements. Pull requests are sent back upstream to keep the original project alive.
>
> [Upstream →](https://github.com/SvenGDK/OpenBurningSuite) | [Issue #1 — CHD Support](https://github.com/SvenGDK/OpenBurningSuite/issues/1)

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: BSD-2-Clause](https://img.shields.io/badge/License-BSD_2--Clause-orange.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)]()

<img src="/Screenshots/Home.png" width="75%">

</div>

---

## About

**Open Burning Suite** is a free, open-source disc burning application for **Windows**, **Linux**, and **macOS**. It lets you burn, copy, create, and verify CDs, DVDs, HD DVDs, and Blu-ray discs — all from one app. Step-by-step wizards for audio, video, data, and gaming discs make it easy to get started, while advanced options give experienced users full control.

Under the hood, Open Burning Suite talks directly to your optical drive using native **SCSI/MMC commands**, so there are no external tools to install. It's built with **.NET 8** and **Avalonia UI** for a modern look and feel on every platform.

---

## Features

| | Feature | Highlights |
|:--:|:--------|:-----------|
| 🔥 | **Burn** | CD-R/RW, DVD±R/RW, DVD-RAM, HD DVD, BD-R/RE, BDXL, M-DISC · TAO/SAO/DAO/RAW write modes · Build on the fly · Multi-copy · Simulation · Overburn · CUE sheets |
| 💿 | **Read** | ISO, BIN/CUE, CHD, CCD/IMG/SUB, TOC/BIN, NRG, MDF/MDS, IMG, CDI output · Raw 2352-byte sectors · Subchannel extraction · Audio paranoia · Gaming presets |
| 🏗 | **Build** | ISO 9660, Joliet, UDF, Rock Ridge, HFS+ filesystems · El Torito bootable discs · VCD/SVCD/XSVCD |
| 🎵 | **Audio** | Create audio CDs with CD-TEXT · Rip audio CDs to WAV or BIN/CUE · Copy music files to disc · Import M3U, PLS, WPL, ASX playlists |
| 🎬 | **Video** | DVD-Video authoring (VIDEO_TS) · Blu-ray BDMV · BDAV recording format · Blu-ray 3D (MVC/SBS/TAB) · FFmpeg-based transcoding |
| ✅ | **Verify** | Sector-by-sector integrity · Disc-to-image comparison · CRC32, MD5, SHA-1, SHA-256, SHA-512 checksums |
| 🎮 | **Gaming** | PlayStation 1–5, PSP · GameCube, Wii, Wii U · Dreamcast, Saturn, Mega CD · Xbox, Xbox 360, Xbox One/Series · Neo Geo CD, 3DO, CD-i, PC Engine, Amiga CD32/CDTV, Atari Jaguar CD · LibCrypt, region-free & boot-sector patching |
| ⚙️ | **Advanced** | Disc erase & format (quick/full) · Eject/load tray · Finalization · M-DISC archival · Real-time disc visualization |
| 🔒 | **Encryption** | AES-256-CBC disc image encryption with password protection (.obse format) · PS3 disc decryption (IRD/dkey/hex) |
| 🧙 | **Wizards** | Step-by-step Quick Start wizards for Audio, Video, Data, Gaming, Copy, and Blank/Erase discs |
| 💿 | **Disc Info** | Drive and media information panel — MID, ATIP, physical format, supported write speeds, buffer info, firmware, serial number |
| 🛡️ | **Admin Required** | RAW/DAO/SCSI commands require administrator/elevated privileges on all platforms |

See the [full documentation](https://svengdk.github.io/OpenBurningSuite/) for detailed guides on each feature.

---

## Supported Media

- **CD:** CD-R/RW (74, 80, 90, 99 min)
- **DVD:** DVD±R/RW, DVD-R DL, DVD+R DL, DVD-RAM
- **HD DVD:** HD DVD-R/RW/RAM (SL/DL)
- **Blu-ray:** BD-R/RE (25–128 GB, BDXL)
- **UHD Blu-ray:** UHD BD-66, UHD BD-100
- **M-DISC:** DVD, BD-R (SL/DL/XL)

See [Supported Formats](https://svengdk.github.io/OpenBurningSuite/supported-formats) for the complete reference.

---

## Platform Guides

- **Windows** — Run the application as Administrator for SCSI passthrough access.
- **Linux** — See [LINUX.md](LINUX.md) for DEB, RPM, APK, Pacman, AppImage, Snap, Flatpak instructions, and SCSI permissions setup.
- **macOS** — See [macOS.md](macOS.md) for PKG, DMG, Gatekeeper instructions, and IOKit SCSI access.

---

## License

Distributed under the **BSD 2-Clause License**. See [`LICENSE`](LICENSE) for more information.
