---
layout: default
title: Burning Discs
permalink: /burning
---

# Burning Discs

Open Burning Suite supports burning a wide variety of optical disc formats using native SCSI/MMC commands. This guide covers all burning features and options.

---

## Supported Image Formats for Burning

| Format | Extension | Description |
|:-------|:----------|:------------|
| ISO 9660 | `.iso` | Standard disc image format |
| BIN/CUE | `.bin` + `.cue` | Raw sector image with CUE sheet |
| NRG | `.nrg` | Nero disc image (auto-converted to BIN/CUE) |
| MDF/MDS | `.mdf` + `.mds` | Alcohol 120% disc image (auto-converted to BIN/CUE) |
| IMG | `.img` | Raw disc image (auto-converted to BIN/CUE) |
| CDI | `.cdi` | DiscJuggler disc image (auto-converted to BIN/CUE) |
| CCD/IMG/SUB | `.ccd` + `.img` + `.sub` | CloneCD disc image (auto-converted to BIN/CUE) |

> **Note:** Other image formats (NRG, MDF/MDS, IMG, CDI, CCD) are automatically converted to BIN/CUE before burning. Conversion happens transparently — just select the image and burn.

---

## Build on the Fly

**Build on the fly** lets you burn files and folders directly to disc without manually creating an image file first. Open Burning Suite builds a temporary ISO image from your selected content and burns it in a single operation.

### How It Works

1. In the **Write / Burn Disc** view, select the **Build on the fly** source mode.
2. Choose your source content:
   - **Source Folder:** Select a folder — all its files and subdirectories are included on the disc.
   - **Individual Files/Folders:** Add specific files and/or folders from different locations. Drag and drop is supported.
3. Set the **Volume Label** (disc name) and **File System** (ISO 9660, Joliet, UDF, etc.).
4. Select your target drive, write speed, and burn options as usual.
5. Click **Burn** — the image is built and burned automatically.

### Supported File Systems for On-the-Fly Burning

| File System | Description | Best For |
|:------------|:------------|:---------|
| **ISO 9660 + Joliet** | Standard with Windows long filename support | General use (default) |
| **ISO 9660** | Basic ECMA-119 (8.3 filenames) | Maximum compatibility |
| **Joliet** | Microsoft extension for long filenames and Unicode | Windows systems |
| **UDF 1.02** | Universal Disc Format 1.02 | DVD-Video, large files |
| **UDF 2.01** | Universal Disc Format 2.01 | DVD, large files (>4 GB) |
| **UDF 2.50** | Universal Disc Format 2.50 | Blu-ray discs |
| **ISO 9660 + UDF** | Hybrid ISO/UDF bridge disc | Cross-platform compatibility |
| **Rock Ridge** | POSIX extensions (permissions, symlinks) | Linux/Unix systems |

### Notes

- The temporary ISO image is created in the system temp directory and automatically cleaned up after burning (or on failure/cancellation).
- All burn options (verify, simulate, multi-copy, overburn, etc.) work with on-the-fly mode.
- For very large data sets, ensure sufficient free disk space for the temporary image.
- On-the-fly mode uses the same native ISO builder as the **Build** view — the same file system features and compliance guarantees apply.

---

## Write Modes

Open Burning Suite supports multiple standard write modes:

### TAO — Track At Once

- Writes one track at a time with gaps between tracks.
- **Best for:** Data discs, single-track audio CDs, incremental multi-session discs.
- Allows adding more sessions later (if disc is not closed).

### SAO — Session At Once

- Writes an entire session in one pass without link blocks between tracks.
- Uses a CUE sheet to define the track layout for the session.
- **Best for:** Audio CDs (ensures gapless playback), multi-track discs, exact disc duplication.
- Multi-session IS possible: the session can be closed while leaving the disc appendable.
- **CD media only.** Both SAO and DAO use SCSI Write Type 0x02 (Mode Page 05h).

### DAO — Disc At Once

- Writes the entire disc in one pass and always finalizes it.
- Uses a CUE sheet to define the track layout for the disc.
- **Best for:** Final disc copies, game disc preservation, discs that must be playable on all players.
- Multi-session is NOT possible — the disc is always closed and finalized.
- **Supported on CD and DVD-R media.** For DVD-R, uses Write Type 0x02 (DAO) in Mode Page 05h.

### RAW Modes

Raw write modes send complete sector data including sync patterns, headers, and ECC/EDC to the drive. These are used for exact disc duplication where the application controls all sector formatting.

| Mode | Sector Size | Description |
|:-----|:------------|:------------|
| **RAW96R** | 2448 bytes | Raw data with R-W subchannel (de-interleaved, 96 bytes) |
| **RAW16** | 2368 bytes | Raw data with P-Q subchannel (16 bytes) |
| **RAW96P** | 2448 bytes | Raw data with P-W subchannel (interleaved, 96 bytes) |

- **Best for:** Exact 1:1 disc copies including subchannel data, copy-protected gaming discs.
- Requires drive support for raw writing.

### Incremental (Packet Writing)

- Writes data in fixed-size packets with incremental track recording.
- **Best for:** Packet writing, UDF formatted discs, drag-and-drop disc usage.
- Data block type: Mode 1 (2048 bytes).

---

## Burn Options

### Basic Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Image Path** | Path to the image file to burn | — (required) |
| **CUE Path** | Path to CUE sheet (for BIN/CUE images in SAO or DAO mode) | — (optional) |
| **Device** | Target optical drive | — (required) |
| **Write Speed** | Burn speed (Auto, or specific speed like 4x, 8x, 16x) | Auto |
| **Copies** | Number of copies to burn | 1 |

### Advanced Options

| Option | Description | Default |
|:-------|:------------|:--------|
| **Simulate Only** | Perform a test burn without actually writing to disc | Off |
| **Buffer Underrun Protection** | Prevent buffer underrun errors during writing | On |
| **Verify After Burn** | Read back and verify the disc after burning | Off |
| **Eject After Burn** | Eject disc after burning completes | On |
| **Close Disc** | Finalize the disc (prevent further writing) | On |
| **Overburn** | Allow writing beyond the rated disc capacity | Off |
| **Overburn Size (MB)** | Maximum overburn size in megabytes | — |

### Multi-Session Options

| Mode | Description |
|:-----|:------------|
| **Close (Single Session)** | Write a single session and close the disc |
| **Start (Multi-session)** | Write a session but leave the disc open for more sessions |
| **Continue (Append)** | Add a new session to an existing multi-session disc |

### Blu-ray Specific Options

| Option | Description |
|:-------|:------------|
| **BD-R Mode** | SRM (Sequential Recording), SRM+POW (Pseudo Overwrite), or RRM (Random Recording) |
| **Layer Break Position** | Sector position for the layer break on dual-layer media |

#### BD-R Recording Modes

| Mode | Description |
|:-----|:------------|
| **SRM (Sequential Recording)** | Standard sequential recording mode. Data is written sequentially from the beginning of the disc. |
| **SRM+POW (Pseudo Overwrite)** | Sequential recording with pseudo-overwrite capability. Allows limited random writes within the recorded area. |
| **RRM (Random Recording)** | Random recording mode. Requires pre-formatting the disc with FORMAT UNIT. Allows random-access writes to any sector. |

### M-DISC (Millennial Disc) Options

M-DISC uses an inorganic stone-like recording layer for archival longevity (estimated 1,000+ years). Open Burning Suite automatically detects M-DISC media via the disc manufacturer identifier and applies the following optimizations:

| Behavior | Description |
|:---------|:------------|
| **Auto-detection** | M-DISC is detected automatically when inserted; the media type is displayed as "M-DISC DVD" or "M-DISC BD-R" |
| **Speed clamping** | Write speed is automatically clamped to the M-DISC maximum (4x for DVD, 6x for BD-R) to ensure reliable engraving |
| **Overburn disabled** | Overburning is automatically disabled for M-DISC — exceeding capacity risks damaging the archival layer |
| **Verify recommendation** | If "Verify After Burn" is not enabled, a recommendation is logged to enable it for archival integrity |

#### M-DISC Write Speed Limits

| Media Type | Maximum Speed | Notes |
|:-----------|:-------------|:------|
| M-DISC DVD | 4x | Higher speeds risk incomplete engraving |
| M-DISC BD-R | 6x | Lower speeds recommended for best results |

#### M-DISC Best Practices

1. **Always verify after burn.** M-DISC is for permanent archival storage — verify every burn to ensure data integrity.
2. **Use the lowest practical speed.** While M-DISC supports up to 4x (DVD) or 6x (BD-R), using 2x produces the most reliable engravings.
3. **Use an M-DISC-certified drive.** While any drive can read M-DISC, only certified drives have the higher laser power needed for writing.
4. **Finalize the disc.** Always close/finalize M-DISC after burning to maximize long-term readability across different drives.

### Media Type Override

You can force a specific media type if automatic detection doesn't work correctly. This is useful for unusual disc types or drives with limited detection capabilities.

### Image Encryption

You can encrypt disc images before burning using AES-256-CBC with a password:

1. Select a source image file in the **Burn / Write** view
2. Enable the encryption option
3. Enter and confirm a strong password
4. The application creates a `.obse` (Open Burning Suite Encrypted) file
5. Burn the encrypted image to disc

Encrypted images use PBKDF2 key derivation and include HMAC-SHA256 integrity verification. The original image file is not modified.

### Image Decryption

Open Burning Suite can decrypt encrypted disc images before burning:

- **OBS Encrypted Images (.obse)** — Images encrypted with the built-in disc encryption feature (AES-256-CBC). You will be prompted to enter the decryption password when an encrypted image is selected.
- **PS3 Encrypted ISOs** — PlayStation 3 game ISOs can be decrypted before burning using one of three key sources: an IRD file (.ird), a disc key file (.dkey), or a 32-character hex disc key. The decryption panel appears automatically when a PS3 ISO is detected.

---

## Multi-Copy Burning

When burning multiple copies, Open Burning Suite will:

1. Burn the first copy
2. Eject the disc
3. Wait for you to insert a new blank disc
4. Automatically detect the new disc and begin the next copy
5. Repeat until all copies are complete

---

## Simulation Mode

Simulation mode performs all the steps of a real burn — including laser calibration and data transfer — without actually writing to the disc. This is useful for:

- **Testing your system** before committing to a real burn
- **Verifying data transfer speed** can sustain the selected write speed
- **Checking for potential errors** in the image file or CUE sheet

> **Note:** Not all drives and media combinations support simulation mode. The drive will report an error if simulation is not supported.

---

## CUE Sheet Support

For multi-track audio CDs or mixed-mode discs, Open Burning Suite parses standard CUE sheets to determine:

- Track layout and ordering
- Audio vs. data track types
- Pre-gap and post-gap durations
- Index positions within tracks

When using SAO or DAO write mode with a BIN/CUE image, the CUE sheet is converted to an MMC-compliant CUE sheet and sent directly to the drive for precise track control.

---

## Progress Tracking

During a burn operation, Open Burning Suite reports:

- **Percent complete** — Overall progress of the burn
- **Current speed** — Actual write speed (e.g., 8.0x)
- **Bytes written** — Amount of data written so far
- **Elapsed time** — Time since burn started
- **Remaining time** — Estimated time to completion
- **Status messages** — Detailed log of the burn process

---

## Tips & Best Practices

1. **Use lower speeds for audio CDs.** A speed of 4x or 8x typically produces better audio quality than maximum speed.

2. **Always verify critical burns.** Enable "Verify After Burn" for important data to ensure the disc is readable.

3. **Use DAO for exact copies.** Disc At Once mode produces the most accurate copy of the original disc.

4. **Test with simulation first.** If you're unsure about your configuration, use simulation mode to test without wasting a disc.

5. **Close the disc when finished.** Unless you specifically need multi-session support, close the disc to ensure maximum compatibility.

6. **Enable buffer underrun protection.** This prevents failed burns caused by data transfer interruptions.

---

**Next:** [Reading Discs →]({{ '/reading' | relative_url }})
