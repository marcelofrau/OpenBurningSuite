---
layout: default
title: Discover Drives & Media
permalink: /discover
---

# Discover Drives & Media

Scan for connected optical drives, probe inserted media, inspect drive capabilities, and analyze disc content.

---

## Optical Drive Detection

Open Burning Suite detects all SCSI/ATAPI optical drives connected to your system. The drive list shows:

| Column | Description |
|:-------|:------------|
| **Device Path** | OS device path (e.g., `\\.\CdRom0`, `/dev/sr0`) |
| **Drive Letter** | Mount point (Windows) |
| **Model** | Drive vendor and model string |
| **Type** | Drive type (CD-ROM, DVD-ROM, BD-ROM, etc.) |
| **Status** | Drive status (Ready, No Disc, Opening, etc.) |

Click **Refresh Drives** to rescan for newly connected drives.

---

## Media Information

After selecting a drive and clicking **Probe Media**, the app displays:

| Field | Description |
|:------|:------------|
| **Media Type** | CD-R, CD-RW, DVD+R, DVD-R, DVD+R DL, DVD-RAM, BD-R, BD-RE, HD DVD-R, etc. |
| **Disc State** | Empty, Appendable (open for multi-session), or Closed (finalized) |
| **Volume Label** | Volume name stored on the disc |
| **Capacity** | Total disc capacity (e.g., 700 MB, 4.37 GB, 25.03 GB) |
| **Used / Free** | Space used and available on the disc |
| **File System** | Detected file system (ISO 9660, Joliet, UDF, HFS+) |
| **Sessions** | Number of disc sessions |
| **Tracks** | Number of audio and data tracks |

---

## Drive Capabilities

The app queries drive firmware to display read/write support for 30+ media types:

### Read Capabilities

Media types queried: CD-R, CD-RW, DVD-ROM, DVD-R, DVD-RW, DVD+R, DVD+RW, DVD-R DL, DVD-RW DL, DVD+R DL, DVD+RW DL, DVD-RAM, HD DVD-ROM, HD DVD-R, HD DVD-RW, HD DVD-RAM, HD DVD-R DL, HD DVD-RW DL, BD-ROM, BD-R, BD-RE, BD-R XL, BD-RE XL

### Write Capabilities

Same media types checked for write support. Read-only formats (DVD-ROM, HD DVD-ROM, BD-ROM) are disabled.

### Advanced Capabilities

| Feature | Description |
|:--------|:------------|
| **C2 Errors** | Drive supports C2 error pointers for accurate error reporting |
| **CD Text** | Drive supports reading/writing CD-Text metadata |
| **Layer Jump Rec.** | Layer Jump Recording for DVD-R DL |
| **Labelflash** | Disc label printing (Yamaha/Sony) |
| **LightScribe** | Disc label printing (HP/LightScribe) |
| **Binding Nonce** | AACS Binding Nonce Generation (Blu-ray content protection) |
| **Bus Encryption** | AACS Bus Encryption for protected Blu-ray content |

### Speed Information

- **Supported Read Speeds:** List of all read speeds the drive supports
- **Supported Write Speeds:** List of all write speeds the drive supports

---

## Drive Firmware & Hardware

| Field | Description |
|:------|:------------|
| **Vendor** | Drive manufacturer / vendor ID |
| **Firmware** | Firmware revision string |
| **Serial** | Drive serial number |
| **Buffer Size** | Drive internal buffer size in KiB |

---

## Disc Content Analysis

Click **Analyze Content** to detect what type of disc is inserted:

| Detection | Examples |
|:----------|:---------|
| **Gaming Console** | PlayStation 1/2/3/4/5, Xbox, Xbox 360, Sega Saturn, Dreamcast, Nintendo Wii, GameCube |
| **Video Disc** | DVD-Video, Blu-ray Movie, VCD, SVCD |
| **Audio CD** | Standard Red Book Audio CD |
| **Data Disc** | Generic data disc with ISO 9660 / UDF |

When a gaming disc is detected, the app shows the specific console model and format details. Content details are shown in an expandable panel below the track listing.

### Track Listing

A track-by-track list is shown after analysis, displaying:
- Track number
- Track type (Audio / Data)
- Track start address (LBA)
- Track length

---

**Next:** [Disc Information →]({{ '/disc-info' | relative_url }})
