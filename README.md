<div align="center">

<img src="OpenBurningSuite/icon.png" width="128px">

# Open Burning Suite

[![Build Windows](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-Windows.yml/badge.svg)](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-Windows.yml)
[![Build Linux](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-linux.yml/badge.svg)](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-linux.yml)
[![Build macOS](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-macOS.yml/badge.svg)](https://github.com/marcelofrau/OpenBurningSuite/actions/workflows/build-macOS.yml)

> **Active fork** by [marcelofrau](https://github.com/marcelofrau) — continuing development with new features, bug fixes, and UI improvements. Pull requests sent back upstream.
>
> [Full documentation →](https://marcelofrau.github.io/OpenBurningSuite/)

[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: BSD-2-Clause](https://img.shields.io/badge/License-BSD_2--Clause-orange.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)]()

<img src="docs/social-preview.jpg" width="75%">

</div>

---

## Quick Start

```bash
git clone https://github.com/marcelofrau/OpenBurningSuite.git
cd OpenBurningSuite
dotnet build
dotnet run --project OpenBurningSuite
```

> **Windows:** Run as Administrator for SCSI passthrough.

---

## What Can You Do?

| | Feature | |
|:--:|:--------|:--:|
| 🔥 | **Burn** — CD, DVD, HD DVD, Blu-ray, M-DISC. TAO/SAO/DAO/RAW modes. | |
| 📀 | **Read** — ISO, BIN/CUE, CHD, CCD, NRG, MDF, IMG, CDI and more. | |
| 🏗️ | **Build** — ISO 9660, Joliet, UDF, HFS+ images from files or folders. | |
| ✅ | **Verify** — CRC32, MD5, SHA-1/256/512 sector-by-sector integrity checks. | |
| 🎮 | **Gaming** — Presets for 20+ consoles. LibCrypt, region-free, boot-sector patching. | |
| 🎬 | **Video** — DVD-Video, Blu-ray BDMV/BDAV, Blu-ray 3D authoring via FFmpeg. | |
| 🎵 | **Audio** — Audio CDs with CD-TEXT, ripping, playlist import (M3U/PLS/WPL/ASX). | |
| 🔒 | **Encryption** — AES-256-CBC image encryption (.obse) and PS3 decryption. | |
| 💿 | **Disc Info** — Media ID, ATIP, physical format, write speeds, firmware info. | |
| 🧙 | **Wizards** — Step-by-step for Audio, Video, Data, Gaming, Copy and Blank discs. | |

See the **[full documentation](https://marcelofrau.github.io/OpenBurningSuite/)** for detailed guides, architecture docs, and FAQ.

---

## Supported Media

CD-R/RW (up to 99 min), DVD±R/RW/RAM/DL, HD DVD-R/RW/RAM, BD-R/RE/XL (up to 128 GB), UHD BD, M-DISC.

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md) for release history.

## Icons Attribution

| Source | License |
|:-------|:--------|
| [Icons8](https://icons8.com) | Free with attribution |
| [FluentUI System Icons](https://github.com/microsoft/fluentui-system-icons) — Microsoft | MIT |

## License

[BSD 2-Clause License](LICENSE)
