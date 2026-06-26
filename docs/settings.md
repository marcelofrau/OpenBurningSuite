---
layout: default
title: Settings
permalink: /settings
---

# Settings

Open Burning Suite has **67 configurable settings** across 12 categories. This page documents every option.

---

## General

| Setting | Default | Description |
|:--------|:--------|:------------|
| Confirm Before Burn | On | Show confirmation prompt before writing to disc |
| Confirm Before Erase | On | Display confirmation before erasing rewritable media |
| Minimize to System Tray | Off | Place app icon in system tray when minimized |
| Prevent Sleep During Burn | On | Keep system awake while writing to prevent failed burns |
| Prevent Screensaver During Burn | On | Disable screensaver while burning a disc |
| Check for Updates at Startup | On | Check for new versions when the application starts |
| Remember Window Position | On | Save window size and position for next launch |
| Show Tooltips | On | Display helpful tooltips when hovering over controls |

---

## Burn Defaults

| Setting | Default | Choices |
|:--------|:--------|:--------|
| Write Speed | Auto | Auto, 1x, 2x, 4x, 8x, 12x, 16x, 24x, 32x, 48x, 52x, Max |
| Write Mode | TAO | TAO (Track At Once), SAO (Session At Once), DAO (Disc At Once) |
| Multi-Session Mode | Close | Close (Single Session), Start (Multi-session), Continue (Append) |
| Buffer Underrun Protection | On | Prevents buffer underruns that can ruin a disc |
| Verify After Burn | Off | Automatically verify written data matches the source |
| Eject After Burn | On | Automatically eject when burning finishes |
| Close Disc | On | Finalize disc so it can be read in standard drives |
| Overburn | Off | Allow writing more data than official disc capacity |
| Simulate Burn | Off | Test burn simulation without actually writing |
| Copies | 1 | Number of copies to burn (1–99) |

---

## Read / Copy Defaults

| Setting | Default | Choices |
|:--------|:--------|:--------|
| Read Speed | Max | Max, 1x, 2x, 4x, 8x, 12x, 16x, 24x, 32x, 48x, 52x |
| Output Format | ISO | ISO, BIN/CUE, CHD, BIN/CUE/SBI, MDF/MDS, NRG, CDI, CCD/IMG/SUB, TOC/BIN, IMG |
| Error Recovery | Yes | Yes (continue), No (stop), Abort (exit) |
| Subchannel Mode | None | None, RW, RW_RAW, P-W |
| Sector Size | 2048 | 2048 (data), 2352 (raw/audio) |
| Read Retry Count | 3 | Retries per failed sector (0–100) |
| Audio Paranoia | Off | Paranoia-level error correction for audio CD ripping |
| Jitter Correction | On | Correct jitter errors during audio extraction |

---

## Verification

| Setting | Default | Choices |
|:--------|:--------|:--------|
| Checksum Algorithm | SHA256 | SHA256, SHA1, MD5, SHA512, CRC32 |
| Auto-Verify After Read | Off | Run verification after copying a disc to an image |

---

## File System Defaults

| Setting | Default | Choices |
|:--------|:--------|:--------|
| File System | ISO 9660 + Joliet | ISO 9660, ISO 9660 + Joliet, ISO 9660 + UDF, UDF 1.02/1.5/2.01/2.50/2.60, Rock Ridge, HFS+ |
| UDF Revision | 2.01 | 1.02, 1.50, 2.01, 2.50, 2.60 |
| Volume Label | _(empty)_ | Default label for new disc projects |
| Allow Long File Names | On | Allow names longer than ISO 9660 8.3 limit |
| Allow Deep Directories | On | Allow directory structures deeper than ISO 9660 limit |

---

## Audio CD Defaults

| Setting | Default | Range |
|:--------|:--------|:------|
| Audio Track Gap (seconds) | 2 | 0–10 |
| Audio Normalization | Off | Equalize volume across all tracks |
| CD-Text | Off | Write artist and title information |
| Gapless Playback | Off | Remove gaps between tracks for seamless playback |

---

## I/O & Performance

| Setting | Default | Range | Description |
|:--------|:--------|:------|:------------|
| I/O Buffer Size | 64 KB | 16–4096 KB | Buffer size for read/write operations |
| Transfer Length | 64 KB | 16–256 KB | KB per SCSI I/O command |
| SCSI Write Retries | 20 | 0–100 | Retries for transient SCSI write errors |
| SCSI Read Retries | 20 | 0–100 | Retries for transient SCSI read errors |
| Direct I/O | Off | — | Bypass OS cache for potentially faster I/O |
| Exclusive Drive Access | On | — | Lock drive exclusively during operations |

---

## Post-Operation Actions

| Setting | Default | Choices |
|:--------|:--------|:--------|
| After Burn | Eject | None, Eject, Shutdown, Sleep, Hibernate, Close App |
| After Read | None | None, Eject, Shutdown, Sleep, Hibernate, Close App |
| After Erase | None | None, Eject |
| Play Sound on Complete | On | Audible sound when operation finishes |
| Play Sound on Error | On | Audible sound when an error occurs |
| Show Notification on Complete | On | System notification when operation finishes |

---

## Logging & Diagnostics

| Setting | Default | Choices |
|:--------|:--------|:--------|
| Enable Logging | On | Write detailed operation logs |
| Log Level | Normal | Normal, Verbose, Debug |
| Max Log File Size | 10 MB | 1–500 MB per file |
| Log File Retention | 5 | 1–50 old files to retain |
| Log SCSI Commands | Off | Log raw SCSI commands and responses for debugging |

Logs are written to `bin/Debug/net8.0/logs/obs_*.log`.

---

## Paths & Locations

| Setting | Default | Description |
|:--------|:--------|:------------|
| Default Image Output Directory | _(empty)_ | Where saved disc images go |
| Temp Directory | _(empty)_ | Temporary build files |
| Log Directory | _(empty)_ | Custom log file location |
| chdman Path | _(empty)_ | Full path to chdman executable (overrides PATH detection) |

Use the **Browse** button to pick a directory. For chdman, use **Browse** to select the executable, then **Test chdman** to verify it works.

---

## Advanced & Safety

| Setting | Default | Description |
|:--------|:--------|:------------|
| Expert Mode | Off | Show advanced options in burn, read, and build views |
| Auto-Detect Optimal Speed | On | Analyze media quality and suggest best write speed |
| Media Compatibility Check | On | Verify media is compatible with the selected operation |
| Auto-Detect Drive Capabilities | On | Query drive firmware for supported features |
| Allow Same Source/Target Drive | Off | WARNING: using same drive as source and target can cause data loss |
| Default Layer Break Position | 0 | LBA for dual-layer media (0 = automatic, up to 4,000,000) |

---

## Gaming Disc Defaults

| Setting | Default | Choices |
|:--------|:--------|:--------|
| Default Gaming Preset | None | PS1, PS2, PS3, PS4/5, Sega Saturn, Sega Dreamcast, Sega Mega CD, Xbox, Xbox 360, GameCube, Wii, Neo Geo CD, 3DO, PC Engine, Atari Jaguar, Amiga CD32, Amiga CDTV |
| Auto-ESR Patch | Off | Auto-apply ESR patching for PS2 DVD compatibility |
| Auto-Region-Free Patch | Off | Remove region restrictions from game discs automatically |

---

**Next:** [Supported Formats →]({{ '/supported-formats' | relative_url }})
