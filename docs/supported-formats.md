---
layout: default
title: Supported Formats
permalink: /supported-formats
---

# Supported Formats & Capacities

A complete reference of all disc formats, image formats, and filesystem types supported by Open Burning Suite.

---

## Optical Disc Media

### CD Formats

| Format | Capacity | Sectors | Minutes | Burn | Read | Notes |
|:-------|:---------|:--------|:--------|:----:|:----:|:------|
| CD-R (74 min) | 682 MB | 333,000 | 74 | ✅ | ✅ | Standard recordable CD |
| CD-R (80 min) | 737 MB | 360,000 | 80 | ✅ | ✅ | Extended capacity |
| CD-R (90 min) | 829 MB | 405,000 | 90 | ✅ | ✅ | Overburn required |
| CD-R (99 min) | 912 MB | 445,500 | 99 | ✅ | ✅ | Maximum overburn |
| CD-RW | Same as above | — | — | ✅ | ✅ | Rewritable, erasable |

### DVD Formats

| Format | Capacity | Layers | Burn | Read | Notes |
|:-------|:---------|:-------|:----:|:----:|:------|
| DVD-R | 4.37 GB | 1 | ✅ | ✅ | Single-layer recordable |
| DVD+R | 4.37 GB | 1 | ✅ | ✅ | Single-layer recordable |
| DVD-RW | 4.37 GB | 1 | ✅ | ✅ | Rewritable |
| DVD+RW | 4.37 GB | 1 | ✅ | ✅ | Rewritable |
| DVD-R DL | 7.95 GB | 2 | ✅ | ✅ | Dual-layer recordable |
| DVD+R DL | 7.96 GB | 2 | ✅ | ✅ | Dual-layer recordable |
| DVD+RW DL | 7.96 GB | 2 | ✅ | ✅ | Dual-layer rewritable |
| DVD-RAM | 4.37 GB | 1 | ✅ | ✅ | Random-access rewritable |

### HD DVD Formats

| Format | Capacity | Layers | Burn | Read | Notes |
|:-------|:---------|:-------|:----:|:----:|:------|
| HD DVD-R | 14.96 GB | 1 | ✅ | ✅ | Single-layer |
| HD DVD-R DL | 29.92 GB | 2 | ✅ | ✅ | Dual-layer |
| HD DVD-RW | 14.96 GB | 1 | ✅ | ✅ | Rewritable |
| HD DVD-RAM | 4.37 GB | 1 | ✅ | ✅ | Random-access rewritable |

### Blu-ray Formats

| Format | Capacity | Layers | Burn | Read | Notes |
|:-------|:---------|:-------|:----:|:----:|:------|
| BD-R (SL) | 25.03 GB | 1 | ✅ | ✅ | Single-layer recordable |
| BD-R (DL) | 50.05 GB | 2 | ✅ | ✅ | Dual-layer recordable |
| BD-R XL (TL) | 100.10 GB | 3 | ✅ | ✅ | Triple-layer BDXL |
| BD-R XL (QL) | 128.00 GB | 4 | ✅ | ✅ | Quad-layer BDXL |
| BD-RE (SL) | 25.03 GB | 1 | ✅ | ✅ | Rewritable single-layer |
| BD-RE (DL) | 50.05 GB | 2 | ✅ | ✅ | Rewritable dual-layer |
| BD-RE XL (TL) | 100.10 GB | 3 | ✅ | ✅ | Rewritable triple-layer BDXL |
| BD-RE XL (QL) | 128.00 GB | 4 | ✅ | ✅ | Rewritable quad-layer BDXL |

### UHD Blu-ray Formats

| Format | Capacity | Layers | Burn | Read | Notes |
|:-------|:---------|:-------|:----:|:----:|:------|
| UHD BD-66 | 66.00 GB | 2 | ✅ | ✅ | Dual-layer UHD Blu-ray |
| UHD BD-100 | 100.00 GB | 3 | ✅ | ✅ | Triple-layer UHD Blu-ray |

UHD Blu-ray uses higher density pits and tighter tracks than standard Blu-ray. Video content requires HEVC (H.265) Main10 Profile with HDR10, BT.2020 color space, and up to 100 Mbps video bitrate.

### M-DISC (Millennial Disc) Formats

M-DISC uses an inorganic recording layer (stone-like) instead of organic dye, providing estimated 1,000+ year archival longevity. M-DISC media uses the same physical format and MMC profiles as standard DVD+R and BD-R, but requires an M-DISC-certified drive for writing. Any standard drive can read M-DISC media.

| Format | Capacity | Layers | Burn | Read | Notes |
|:-------|:---------|:-------|:----:|:----:|:------|
| M-DISC DVD | 4.37 GB | 1 | ✅ | ✅ | DVD+R compatible, max 4x write speed |
| M-DISC BD-R (SL) | 25.03 GB | 1 | ✅ | ✅ | BD-R compatible, max 6x write speed |
| M-DISC BD-R (DL) | 50.05 GB | 2 | ✅ | ✅ | BD-R DL compatible, max 6x write speed |
| M-DISC BD-R XL (TL) | 100.10 GB | 3 | ✅ | ✅ | BD-R XL compatible, max 6x write speed |

### Gaming Disc Formats

| Format | Capacity | Console | Read | Notes |
|:-------|:---------|:--------|:----:|:------|
| GameCube miniDVD | 1.36 GB | Nintendo GameCube | ✅ | 8 cm proprietary disc |
| Wii Disc (SL) | 4.37 GB | Nintendo Wii | ✅ | Single-layer |
| Wii Disc (DL) | 7.93 GB | Nintendo Wii | ✅ | Dual-layer |
| GD-ROM | 1.12 GB | Sega Dreamcast | ✅ | High-density area |
| UMD (SL) | 900 MB | Sony PSP | ✅ | Single-layer |
| UMD (DL) | 1.68 GB | Sony PSP | ✅ | Dual-layer |

---

## Image File Formats

### Read / Write Formats

| Format | Extension(s) | Read | Write | Build | Description |
|:-------|:-------------|:----:|:-----:|:-----:|:------------|
| ISO 9660 | `.iso` | ✅ | ✅ | ✅ | Standard disc image |
| BIN/CUE | `.bin` + `.cue` | ✅ | ✅ | ✅ | Raw sector image + CUE sheet |
| CCD/IMG/SUB | `.ccd` + `.img` + `.sub` | ✅ | ✅ | — | CloneCD disc image (CCD v2/v3) |
| TOC/BIN | `.toc` + `.bin` | ✅ | ✅ | — | cdrdao-style TOC + raw image |
| NRG | `.nrg` | ✅ | ✅ | — | Nero disc image |
| MDF/MDS | `.mdf` + `.mds` | ✅ | ✅ | — | Alcohol 120% disc image |
| IMG | `.img` | ✅ | ✅ | — | Raw disc image |
| CDI | `.cdi` | ✅ | ✅ | — | DiscJuggler disc image (v2.0, 3.0, 3.5, 4.0) |
| XISO | `.iso` | ✅ | ✅ | — | Xbox DVD filesystem (XDVDFS) image |
| VCD | `.bin` + `.cue` | ✅ | — | ✅ | Video CD raw Mode 2 XA sector image |
| SVCD | `.bin` + `.cue` | ✅ | — | ✅ | Super Video CD raw Mode 2 XA sector image |
| XSVCD | `.bin` + `.cue` | ✅ | — | ✅ | eXtended Super Video CD raw Mode 2 XA sector image |

### Sector Sizes

| Size | Mode | Description |
|:-----|:-----|:------------|
| 2048 bytes | Mode 1 | Standard data sectors (user data only) |
| 2056 bytes | Mode 1 | User data + 8-byte L-EC header |
| 2332 bytes | Mode 2 XA | XA Form 2 user data |
| 2336 bytes | Mode 2 | Mode 2 formless (no error correction) |
| 2340 bytes | Mode 2 | Mode 2 with sub-header |
| 2352 bytes | Raw | Full raw sectors including sync, headers, ECC/EDC |
| 2368 bytes | Raw + PQ | Raw sectors with P-Q subchannel data (16 bytes) |
| 2448 bytes | Raw + Sub | Raw sectors with P-W subchannel data (96 bytes) |

---

## Audio Formats

### Audio CD (Red Book)

Audio CDs follow the **Red Book** standard: 16-bit PCM audio at 44,100 Hz stereo. Open Burning Suite automatically converts supported input formats to Red Book PCM during audio CD creation.

### Supported Audio Input Formats

| Format | Extension(s) | Notes |
|:-------|:-------------|:------|
| WAV | `.wav` | Lossless, natively supported |
| MP3 | `.mp3` | Lossy, auto-converted to PCM |
| WMA | `.wma` | Windows Media Audio, auto-converted |
| AIFF | `.aiff`, `.aif` | Apple lossless format, auto-converted |
| FLAC | `.flac` | Free lossless codec (for music copy to disc) |
| OGG | `.ogg` | Ogg Vorbis (for music copy to disc) |
| AAC | `.aac`, `.m4a` | Advanced Audio Coding (for music copy to disc) |

### Audio CD Ripping Output Formats

| Format | Extension | Notes |
|:-------|:----------|:------|
| WAV | `.wav` | Lossless, CD quality (individual tracks) |
| BIN/CUE | `.bin` + `.cue` | Raw audio image (full disc) |

### Playlist Formats

| Format | Extension | Description |
|:-------|:----------|:------------|
| M3U / M3U8 | `.m3u`, `.m3u8` | Standard playlist (UTF-8 variant) |
| PLS | `.pls` | SHOUTcast / Winamp playlist |
| WPL | `.wpl` | Windows Media Player playlist |
| ASX | `.asx` | Advanced Stream Redirector playlist |

---

## Filesystem Formats (Image Building)

| Filesystem | Standard | Description |
|:-----------|:---------|:------------|
| ISO 9660 | ECMA-119 | Base CD-ROM filesystem — 8.3 filenames, 8-level directory depth |
| Joliet | Microsoft ext. | Unicode filenames up to 64 characters, deep directories |
| UDF | ECMA-167 | Universal Disk Format — modern, supports large files (>4 GB) |
| Rock Ridge | IEEE P1282 | POSIX extensions — long filenames, permissions, symlinks, device nodes |
| HFS+ | Apple | macOS-native filesystem (requires Apple's `hdiutil` for full support) |
| ISO 9660 + UDF | Hybrid | Dual filesystem for maximum compatibility |
| ISO 9660 + Joliet | Hybrid | ISO 9660 base with Joliet extensions for Windows |

### UDF Versions

| Version | Description | Typical Use |
|:--------|:------------|:------------|
| 1.02 | Basic UDF | DVD-ROM, DVD-Video |
| 1.5 | Enhanced | DVD-RAM, packet writing |
| 2.01 | Common default | DVD, large files |
| 2.50 | Blu-ray | BD-ROM, BD-RE |
| 2.60 | Blu-ray / HD DVD | BD-R, BD-RE, HD DVD, UHD Blu-ray |

---

## Write Modes

| Mode | Full Name | Description |
|:-----|:----------|:------------|
| TAO | Track At Once | Writes one track at a time; allows multi-session |
| SAO | Session At Once | Writes entire session at once; gapless audio; multi-session possible (CD only) |
| DAO | Disc At Once | Writes entire disc at once; gapless audio; always finalizes disc (CD + DVD-R) |
| RAW96R | Raw + R-W Subchannel | Raw 2448-byte sectors with de-interleaved R-W subchannel |
| RAW16 | Raw + P-Q Subchannel | Raw 2368-byte sectors with P-Q subchannel |
| RAW96P | Raw + P-W Subchannel | Raw 2448-byte sectors with interleaved P-W subchannel |
| Incremental | Packet Writing | Fixed-size packet writing for incremental recording |

---

## Multi-Session Modes

| Mode | Description |
|:-----|:------------|
| Close (Single Session) | Write one session and finalize the disc |
| Start (Multi-session) | Write a session, leave disc open for more |
| Continue (Append) | Add a new session to existing multi-session disc |

---

## BD-R Recording Modes

| Mode | Full Name | Description |
|:-----|:----------|:------------|
| SRM | Sequential Recording Mode | Writes data sequentially (default, most compatible) |
| SRM+POW | SRM + Pseudo Overwrite | Sequential with limited random-write capability |
| RRM | Random Recording Mode | Allows random-access writes (BD-RE style, requires pre-format) |

---

## Checksum / Hash Algorithms

| Algorithm | Output Size | Standard |
|:----------|:------------|:---------|
| CRC32 | 32-bit (8 hex chars) | ISO 3309 |
| MD5 | 128-bit (32 hex chars) | RFC 1321 |
| SHA-1 | 160-bit (40 hex chars) | FIPS 180-4 |
| SHA-256 | 256-bit (64 hex chars) | FIPS 180-4 |
| SHA-512 | 512-bit (128 hex chars) | FIPS 180-4 |

---

**Next:** [Architecture →]({{ '/architecture' | relative_url }})
