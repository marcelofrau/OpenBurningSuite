---
layout: default
title: Verification
permalink: /verification
---

# Verification

Open Burning Suite provides comprehensive verification tools to ensure your burned discs and image files are error-free. This guide covers all verification modes and options.

---

## Verification Modes

### 1. Verify Disc

Reads every sector of a disc and checks for read errors. This mode verifies the physical integrity of the disc without comparing it to a source image.

**Use when:** You want to check if a disc is still readable, or to test a newly burned disc.

### 2. Compare Disc to Image

Reads the disc sector by sector and compares it against a source image file. This performs both a read integrity check and a data accuracy check.

**Use when:** You want to confirm that a burn was successful and the disc matches the original image exactly.

### 3. Check Image Integrity

Computes checksums for a disc image file on disk. This does not require an optical drive.

**Use when:** You want to verify a downloaded image file, or generate checksums for archival purposes.

---

## Checksum Algorithms

| Algorithm | Hash Size | Speed | Security | Use Case |
|:----------|:----------|:------|:---------|:---------|
| **CRC32** | 32-bit | ⚡ Fastest | Low | Quick error detection |
| **MD5** | 128-bit | 🔵 Fast | Medium | General integrity checking |
| **SHA-1** | 160-bit | 🟡 Medium | Medium | Legacy compatibility |
| **SHA-256** | 256-bit | 🔴 Slower | High | Archival, security-sensitive verification |
| **SHA-512** | 512-bit | 🔴 Slower | Very High | Maximum security, archival verification |

### Which Algorithm Should I Use?

- **Quick check:** Use **CRC32** for fast verification when you just need to confirm basic integrity.
- **Standard verification:** Use **MD5** for a good balance of speed and reliability.
- **Maximum confidence:** Use **SHA-256** when you need the highest level of assurance.
- **Compatibility:** Use **MD5** or **SHA-1** if you need to compare against checksums provided by others (many download sites provide MD5/SHA-1 hashes).

---

## Sector-by-Sector Verification

When verifying a disc (either standalone or compared to an image), Open Burning Suite reads individual sectors and handles different sector modes:

### CD Sector Modes

| Mode | Sector Size | User Data Offset | User Data Size | Description |
|:-----|:------------|:-----------------|:---------------|:------------|
| **Mode 1** | 2352 bytes | 16 | 2048 bytes | Standard data CD |
| **Mode 2** | 2352 bytes | 24 | 2048+ bytes | CD-ROM XA |
| **Audio** | 2352 bytes | 0 | 2352 bytes | Audio CD (no sync pattern) |

The verification engine detects the sector mode by looking for the **CD sync pattern** (`00 FF FF FF FF FF FF FF FF FF FF 00`) at the start of each sector and reading the mode byte at offset 15.

### DVD / Blu-ray Sectors

DVD and Blu-ray discs use a standard 2048-byte sector size without the additional CD-specific headers.

---

## Verification Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Device** | Optical drive to read from (for disc verification) | — |
| **Image Path** | Path to image file (for comparison or image integrity check) | — |
| **Checksum Algorithm** | Hash algorithm to use | SHA-256 |
| **Verify Subchannel** | Include subchannel data in verification | Off |
| **Audio Track Verification** | Also read and verify audio track data from disc (for mixed-mode CDs) | Off |

---

## Safety Features

### Maximum LBA Safety Cap

To prevent infinite loops when disc capacity cannot be determined (e.g., due to a damaged disc or drive firmware issue), the verification engine enforces a **safety cap** based on the maximum possible disc size (BD-50 dual-layer Blu-ray). If the disc capacity cannot be read via READ CAPACITY or READ TRACK INFO commands, verification will stop at this safety limit.

---

## Progress Tracking

During verification, Open Burning Suite reports:

- **Percent complete** — Overall verification progress
- **Sectors verified** — Number of sectors successfully checked
- **Error sectors** — Number of sectors with read or comparison errors
- **Current checksum** — Running checksum calculation
- **Elapsed time** — Time since verification started
- **Status messages** — Detailed verification log

---

## Raw Image Checksum

When computing checksums for raw (2352-byte sector) images, Open Burning Suite intelligently extracts the **user data** from each sector:

| Sector Type | Data Hashed |
|:------------|:------------|
| Mode 1 | 2048 bytes starting at offset 16 |
| Mode 2 | 2048 bytes starting at offset 24 |
| Audio (no sync) | Skipped by default; full 2352 bytes when Audio Track Verification is enabled |

This ensures checksums are comparable between raw images and standard 2048-byte ISO images containing the same data.

### Audio Track Verification

When **Audio Track Verification** is enabled in "Compare Disc to Image" mode, audio tracks are included in the checksum computation. Both the disc side and image side use the same **unified per-sector classification** based on the CD sync pattern (`00 FF FF FF FF FF FF FF FF FF FF 00`):

- **Sectors with sync pattern** (data) — User data (2048 bytes) is extracted from the appropriate offset (Mode 1: offset 16, Mode 2: offset 24) and hashed.
- **Sectors without sync pattern** (audio) — The full 2352 bytes are hashed.

This sync-pattern-based approach ensures consistent checksums regardless of:
- Whether the image uses single-file or multi-file CUE layout
- How the ripping tool organized tracks into files
- Pregap sector placement between data and audio tracks

This provides complete verification of mixed-mode CDs (e.g., PlayStation 1 games with audio tracks). Without this option, only data sectors (those with a sync pattern) are compared — audio sectors are skipped on both sides.

---

## BIN/CUE Verification Details

Open Burning Suite handles both **single-file** and **multi-file** BIN/CUE layouts when verifying against a disc.

### Single-File BIN/CUE

A single `.bin` file contains all tracks (data and audio) in one contiguous file. The verification engine:

1. Reads the disc's **Table of Contents (TOC)** to determine track boundaries
2. Builds per-track **LBA ranges** for data and audio tracks
3. Reads each range from the disc using `READ CD` with the correct **Expected Sector Type** (0 for data tracks, 1 for CD-DA audio tracks)
4. Computes a checksum using sync-pattern-based per-sector classification on both the disc and image sides

### Multi-File BIN/CUE

Multiple `.bin` files (one per track) are mapped to disc LBA positions using the CUE sheet's INDEX information and the disc's TOC. Each file is read from the disc at its corresponding LBA range with the correct sector type.

### Mixed-Mode CD Pregap Handling

On mixed-mode CDs (e.g., PlayStation 1 games with a data track followed by audio tracks), the **150-frame pregap** (2 seconds at 75 frames/sec) between data and audio tracks contains **audio-format sectors**. Per the Red Book standard, these pregap sectors have no CD sync pattern and cannot be read with `expectedSectorType=0` (data) — many drives return "Illegal mode for this track" (SCSI ASC=0x64, ASCQ=0x00) in this case.

To handle this correctly:
- **Data track ranges** are trimmed by 150 frames at the end when followed by an audio track
- **Audio track ranges** are extended backward by 150 frames to include the pregap (when Audio Track Verification is enabled)
- The sync-pattern-based classification correctly skips pregap audio sectors on the image side when Audio Track Verification is disabled

This approach matches how tools like **DiscImageCreator**, **Redumper**, and **CUETools** handle pregap sectors during disc verification and dumping.

### Error Recovery

If a sector read fails (e.g., due to disc damage or an unexpected sector type), the verification engine:

1. Falls back to **single-sector reads** instead of batch reads
2. Retries with `expectedSectorType=1` (CD-DA) if the initial type fails — this recovers pregap audio sectors near data/audio track boundaries
3. Continues reading past errors up to a threshold (512 consecutive errors) before aborting

---

## Tips & Best Practices

1. **Always verify important burns.** A verification pass catches errors that might not be apparent until the disc is needed.

2. **Use disc-to-image comparison for critical data.** This is the most thorough verification method, as it confirms every byte matches the source.

3. **Save checksums for archival.** When creating archival disc copies, record the SHA-256 checksum for future verification.

4. **Verify before distributing.** If you're making copies for distribution, verify at least one copy to ensure the burn process is producing accurate results.

5. **Re-verify periodically.** Optical media degrades over time. Periodically verify important discs to catch degradation early.

---

**Next:** [Gaming Discs →]({{ '/gaming-discs' | relative_url }})
