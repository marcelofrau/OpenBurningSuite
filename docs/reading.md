---
layout: default
title: Reading Discs
permalink: /reading
---

# Reading Discs

Open Burning Suite can read optical discs and save them as image files in various formats. This guide covers all reading features, output formats, and advanced options.

---

## Output Formats

| Format | Extension(s) | Description | Best For |
|:-------|:-------------|:------------|:---------|
| **ISO** | `.iso` | Standard ISO 9660 disc image | Data discs, general use |
| **BIN/CUE** | `.bin` + `.cue` | Raw sector image with CUE sheet | Audio CDs, exact copies |
| **CCD/IMG/SUB** | `.ccd` + `.img` + `.sub` | CloneCD disc image | Exact copies with subchannel |
| **TOC/BIN** | `.toc` + `.bin` | cdrdao-style TOC with raw image | Audio CDs, multi-track |
| **NRG** | `.nrg` | Nero disc image format | Nero compatibility |
| **MDF/MDS** | `.mdf` + `.mds` | Alcohol 120% disc image | Alcohol compatibility |
| **IMG** | `.img` | Raw disc image | Simple raw copies |
| **CDI** | `.cdi` | DiscJuggler disc image | Dreamcast games |
| **VCD** | `.bin` + `.cue` | Video CD raw Mode 2 XA sector image | VCD disc copies |
| **SVCD** | `.bin` + `.cue` | Super Video CD raw Mode 2 XA sector image | SVCD disc copies |
| **XSVCD** | `.bin` + `.cue` | eXtended Super Video CD raw Mode 2 XA sector image | XSVCD disc copies |
| **XISO (Xbox)** | `.iso` | Xbox DVD filesystem (XDVDFS) image | Original Xbox games |

---

## Read Options

### Basic Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Device** | Source optical drive to read from | — (required) |
| **Output Path** | Path for the output image file | — (required) |
| **Output Format** | Image format to save as | ISO |
| **Read Speed** | Read speed (Max, or a specific speed) | Max |

### Error Recovery

| Option | Description | Default |
|:-------|:------------|:--------|
| **Error Recovery** | How to handle read errors: Yes (retry), No (skip), Abort | Yes |
| **Retry Count** | Number of times to retry reading a failed sector | 3 |
| **Read Bad Sectors** | Attempt to read sectors that are known to be damaged | Off |

### Sector Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Sector Size** | Bytes per sector: 2048 (data only) or 2352 (raw with headers/ECC) | 2048 |
| **Subchannel Mode** | Subchannel data to capture alongside main sector data | None |

### Subchannel Modes

| Mode | Description | Extra Bytes |
|:-----|:------------|:------------|
| **None** | No subchannel data | 0 |
| **RW** | Raw P-W subchannel data | 96 bytes/sector |
| **RW_RAW** | Raw P-W subchannel data (alternative) | 96 bytes/sector |
| **P-W** | De-interleaved R-W subchannel data | 96 bytes/sector |

> **Note:** Subchannel data is primarily useful for exact audio CD copies, copy-protected discs, and certain gaming disc formats.

---

## Audio Paranoia Mode

For audio CDs, Open Burning Suite provides a **paranoia mode** that performs error-corrected reading with:

- Multiple reads of each sector to detect inconsistencies
- Jitter correction to align overlapping reads
- Interpolation of unrecoverable samples

### When to Use Audio Paranoia

- Reading scratched or damaged audio CDs
- Creating archival-quality audio rips
- When standard reading produces clicks, pops, or skips

### Jitter Correction

**Jitter correction** compensates for the fact that CD drives don't always position the laser at the exact same starting point for each read. When enabled, it aligns overlapping sector reads to ensure sample-accurate extraction.

---

## Gaming Presets for Reading

Applying a gaming preset configures sector size, subchannel mode, speed, and error recovery for a specific console.

| Console | Sector Size | Subchannel | Speed |
|:--------|:-----------|:-----------|:------|
| PlayStation 1 | 2352 | RW_RAW | 4x |
| PlayStation 2 | 2048 | None | 4x |
| PlayStation 3 | 2048 | None | 2x |
| PlayStation 4/5 | 2048 | None | 2x |
| Sega Saturn | 2352 | RW | 4x |
| Sega Dreamcast | 2352 | RW | 2x |
| Sega Mega CD | 2352 | RW | 4x |
| Xbox | 2048 | None | 2x |
| Xbox 360 | 2048 | None | 2x |
| GameCube | 2048 | None | 4x |
| Wii | 2048 | None | 4x |
| Neo Geo CD | 2352 | RW | 4x |
| 3DO | 2352 | RW | 4x |
| PC Engine | 2352 | RW | 4x |
| Atari Jaguar | 2352 | RW | 4x |
| Amiga CD32 | 2352 | RW | 4x |
| Amiga CDTV | 2352 | RW | 4x |

The **Copy Disc Wizard** provides a guided step-by-step workflow with gaming preset support.

---

## PS3 Disc Decryption

PlayStation 3 game ISOs use AES-128-CBC sector-level encryption. Open Burning Suite detects encrypted PS3 ISOs and offers decryption during the read process:

| Key Source | Description |
|:-----------|:------------|
| **IRD file (.ird)** | Official IRD file (versions 6–9) containing the encrypted disc key |
| **Disc key file (.dkey)** | Plaintext disc key file |
| **Hex disc key** | 32-character hex string entered directly |

After decryption, the image can be used with emulators or burned to BD-R media.

---

## CHD Output

CHD (Compressed Hunks of Data) is available as an output format for compressed disc image storage. This requires [chdman]({{ '/getting-started' | relative_url }}#chdman-mame-chd-tools) to be installed.

---

---

## Gaming Disc Presets

Open Burning Suite includes pre-configured reading presets for gaming discs. When a gaming preset is selected, the following parameters are automatically configured:

- Optimal sector size (2048 or 2352)
- Appropriate subchannel mode
- Error recovery settings
- Output format best suited for the gaming system

See [Gaming Discs]({{ '/gaming-discs' | relative_url }}) for detailed information about each supported gaming platform.

---

## Progress Tracking

During a read operation, Open Burning Suite reports:

- **Percent complete** — Overall read progress
- **Sectors read** — Number of sectors successfully read
- **Total sectors** — Total number of sectors on the disc
- **Error sectors** — Number of sectors that encountered read errors
- **Current speed** — Actual read speed (e.g., 12.0x)
- **Elapsed time** — Time since read started
- **Remaining time** — Estimated time to completion

---

## Tips & Best Practices

1. **Use ISO format for data discs.** It's the most universally supported image format.

2. **Use BIN/CUE for audio CDs.** Raw sector images preserve the full audio data and track layout.

3. **Enable audio paranoia for damaged discs.** It significantly improves read quality at the cost of speed.

4. **Lower the read speed for scratched discs.** Slower speeds give the drive more time to recover data from damaged areas.

5. **Use the appropriate sector size:**
   - **2048** bytes for standard data disc copies
   - **2352** bytes for exact raw copies (includes sector headers and error correction data)

6. **Include subchannel data for exact copies.** Some disc formats and copy protections store data in subchannel areas.

---

**Next:** [Building Images →]({{ '/building-images' | relative_url }})
