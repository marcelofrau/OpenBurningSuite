---
layout: default
title: Gaming Discs
permalink: /gaming-discs
---

# Gaming Disc Support

Open Burning Suite includes specialized support for reading and identifying gaming disc formats. This guide covers each supported gaming platform, its disc specifications, and how to use the built-in presets.

> **Note:** Gaming disc formats are proprietary and are supported for **reading and identification** only. Open Burning Suite can read gaming discs and detect the console type, but cannot create or burn proprietary gaming disc formats. Standard ISO or BIN/CUE images of games can be burned to compatible media using the regular burn feature.

---

## Supported Gaming Platforms

| Console | Disc Format | Capacity | Media Type |
|:--------|:------------|:---------|:-----------|
| PlayStation 1 | CD-ROM | Up to 737 MB | CD-R |
| PlayStation 2 | DVD-ROM | Up to 7.95 GB | DVD±R |
| PlayStation 3 | BD-ROM | Up to 50.05 GB | BD-R |
| PlayStation 4 | BD-ROM | Up to 50.05 GB | BD-R |
| PlayStation 5 | BD-ROM | Up to 100.10 GB | BD-R XL |
| PSP | UMD | Up to 1.68 GB | — (read only) |
| Xbox | DVD-ROM | Up to 7.95 GB | DVD±R |
| Xbox 360 | DVD-ROM | Up to 7.95 GB | DVD±R DL |
| Xbox One/Series | BD-ROM | Up to 100.10 GB | BD-R XL |
| Nintendo GameCube | miniDVD | 1.36 GB | DVD±R (special) |
| Nintendo Wii (SL) | DVD | 4.37 GB | DVD±R |
| Nintendo Wii (DL) | DVD | 7.93 GB | DVD±R DL |
| Nintendo Wii U | BD-like | Up to 25 GB | BD-R |
| Sega Dreamcast | GD-ROM | 1.12 GB | CD-R (special) |
| Sega Saturn | CD-ROM | Up to 737 MB | CD-R |
| Sega Mega CD | CD-ROM | Up to 737 MB | CD-R |
| PC Engine / TurboGrafx-CD | CD-ROM | Up to 737 MB | CD-R |
| Neo Geo CD | CD-ROM | Up to 737 MB | CD-R |
| 3DO | CD-ROM | Up to 737 MB | CD-R |
| Philips CD-i | CD-ROM | Up to 737 MB | CD-R |
| Amiga CD32 | CD-ROM | Up to 737 MB | CD-R |
| Amiga CDTV | CD-ROM | Up to 737 MB | CD-R |
| Atari Jaguar CD | CD-ROM | Up to 737 MB | CD-R |
| Nintendo DS/3DS | Cartridge | — | — |

---

## PlayStation

### PlayStation 1

- **Format:** Standard CD-ROM
- **Sector size:** 2352 bytes (raw) recommended for exact copies
- **Output format:** BIN/CUE recommended
- **Notes:** PS1 games often use mixed-mode discs with audio tracks. Use BIN/CUE format to preserve the full disc layout including audio tracks and subchannel data.

### PlayStation 2

- **Format:** DVD-ROM (single or dual-layer)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Dual-layer PS2 games require specific layer break positioning when burning. Open Burning Suite's gaming preset handles this automatically.

### PlayStation 3

- **Format:** BD-ROM (single or dual-layer)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** PS3 game discs use Blu-ray. Encrypted disc content can be decrypted during the copy or burn process. The **Game Wizard** and **Write** view support three key sources: an IRD file (.ird), a disc key file (.dkey), or a 32-character hex disc key entered directly. The **Copy Disc Wizard** supports decryption using an IRD file (.ird). Select the "Decrypt PS3 disc" option and provide the matching key.

### PlayStation 4 & 5

- **Format:** BD-ROM / BD-XL
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Similar to PS3, these use Blu-ray media. PS5 titles may use triple-layer BDXL discs (100 GB).

### PSP (UMD)

- **Format:** UMD (Universal Media Disc)
- **Capacity:** 900 MB (single-layer) or 1.8 GB (dual-layer)
- **Notes:** UMD is a read-only format. Open Burning Suite supports reading UMD image files for verification and checksum purposes.

---

## Nintendo

### GameCube

- **Format:** miniDVD (8 cm optical disc)
- **Capacity:** 1.36 GB (1,459,978,240 bytes)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** GameCube games use a proprietary miniDVD format. When reading, the gaming preset configures the optimal parameters. When building images, use the "Pad to Capacity" option to fill the image to the expected GameCube disc size.

### Wii — Single Layer

- **Format:** Standard DVD
- **Capacity:** 4.37 GB (4,699,979,776 bytes)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Wii games use a proprietary DVD format. The gaming preset ensures correct read parameters.

### Wii — Dual Layer

- **Format:** Dual-layer DVD
- **Capacity:** 7.93 GB (8,511,160,320 bytes)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Dual-layer Wii games are less common. The preset handles layer transition correctly.

---

## Xbox

### Xbox (Original)

- **Format:** DVD-ROM (single or dual-layer)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Xbox games use a modified DVD format. The gaming preset configures the optimal read/burn parameters.

### Xbox 360

- **Format:** DVD-ROM (dual-layer)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Xbox 360 games use dual-layer DVDs with specific security sectors. Only unencrypted images can be burned.

### Xbox One / Xbox Series

- **Format:** BD-ROM (Blu-ray)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** These consoles use Blu-ray media (up to BDXL 100 GB). Encrypted disc content cannot be decrypted by Open Burning Suite.

---

## Sega

### Sega Mega CD

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** Sega Mega CD (Genesis CD) games use standard CD-ROM format. Use raw sector mode for accurate copies that preserve the disc layout.

### Sega Saturn

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** Sega Saturn games use standard CD-ROM format. The IP.BIN header contains "SEGA SEGASATURN" and is used for automatic system detection. Use raw sector mode for accurate copies.

### Dreamcast (GD-ROM)

- **Format:** GD-ROM (Gigabyte Disc)
- **Capacity:** ~1.12 GB (1,200,000,000 bytes)
- **Output format:** CDI or BIN/CUE recommended
- **Notes:** GD-ROM is a proprietary Sega format with a high-density data area. Standard optical drives can only read the low-density portion. Specialized drives or ripping methods may be needed for the high-density area.

---

## Other Platforms

### Nintendo Wii U

- **Format:** BD-like optical disc (25 GB)
- **Sector size:** 2048 bytes
- **Output format:** ISO recommended
- **Notes:** Wii U uses a proprietary Blu-ray-like format. Image detection looks for the "WUP0" magic header.

### PC Engine / TurboGrafx-CD

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** PC Engine CD games use standard CD-ROM format. The system area contains "PC Engine" or "PCECD" identifiers.

### Neo Geo CD

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** Neo Geo CD games use standard CD-ROM format. Detection checks for "NEO-GEO" at offset 0x100 in the system area.

### 3DO Interactive Multiplayer

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** 3DO uses a proprietary CD format. Detection checks for magic bytes (0x01, 0x5A) at the start of the disc.

### Philips CD-i

- **Format:** CD-ROM (CD-RTOS)
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** CD-i uses the CD-RTOS operating system. Detection checks for "CD-RTOS" or "CD-I" in the ISO PVD system identifier.

### Amiga CD32

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Detection methods:**
  - PVD system identifier containing "AMIGA"
  - Boot block containing "AMIGABOOT" or "AMIGA" at offset 0 (cooked) or offset 16 (raw 2352-byte images)
- **Notes:** Amiga CD32 uses standard CD-ROM Mode 1 format with an ISO 9660 filesystem and custom Amiga boot block. The CD32 was released in 1993 as a 32-bit game console. Use DAO write mode with RW subchannel for full preservation. Images typically come as BIN/CUE pairs.

### Amiga CDTV

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Detection methods:**
  - PVD system identifier containing "CDTV"
  - Boot block containing "CDTV" at offset 0 (cooked) or offset 16 (raw 2352-byte images)
- **Notes:** The Commodore CDTV (Commodore Dynamic Total Vision) was released in 1991 as a multimedia home entertainment system based on the Amiga 500. It uses standard CD-ROM format with an Amiga filesystem. Detected separately from the Amiga CD32 but uses the same raw sector and subchannel configuration for full preservation.

### Atari Jaguar CD

- **Format:** CD-ROM
- **Sector size:** 2352 bytes (raw) recommended
- **Output format:** BIN/CUE recommended
- **Notes:** Atari Jaguar CD uses standard CD-ROM format. Detection checks for "ATRI" or "TARA" magic bytes.

### Nintendo DS/3DS

- **Format:** Cartridge-based (not optical)
- **Sector size:** 2048 bytes
- **Output format:** ISO
- **Notes:** The Nintendo DS and 3DS are cartridge-based systems, not disc-based. This preset is provided for ISO verification and checksum purposes only — it does not support reading or burning cartridge data.

---

## Using Gaming Presets

### Reading Gaming Discs

1. Navigate to the **Read** tab.
2. Select your optical drive from the device list.
3. Choose a **Gaming Preset** from the dropdown (e.g., "PlayStation 2", "Nintendo Wii").
4. The preset automatically configures:
   - Sector size (2048 or 2352 as appropriate)
   - Output format (ISO, BIN/CUE, CDI)
   - Error recovery settings
   - Subchannel mode
5. Select your output path and click **Read**.

### Building Gaming Disc Images

1. Navigate to the **Build** tab.
2. Set the **Gaming System** (e.g., "GameCube", "Wii").
3. The disc type and capacity are automatically configured.
4. Add your source files/folders.
5. Enable **Pad to Capacity** if required by the target console.
6. Click **Build** to create the image.

### Automatic Detection

Open Burning Suite can automatically detect the gaming system of an image file by analyzing:

- **Image file size** — Each gaming platform has characteristic disc capacities
- **Disc metadata** — System-specific identifiers in the disc header

When a gaming disc is detected, the application suggests the appropriate preset.

---

## Gaming Preset Configuration

Each gaming preset configures sector size, subchannel mode, read/write speed, error recovery, and output format:

| Console | Sector | Subchannel | Read Speed | Write Speed | Write Mode | Retries | Audio Paranoia | Output |
|:--------|:-------|:-----------|:-----------|:------------|:-----------|:--------|:---------------|:-------|
| PlayStation 1 | 2352 | RW_RAW | 4x | 4x | DAO | 5 | Yes | BIN/CUE |
| PlayStation 2 | 2048 | None | 4x | 4x | DAO | 3 | — | ISO |
| PlayStation 3 | 2048 | None | 2x | — | — | 3 | — | ISO |
| PlayStation 4/5 | 2048 | None | 2x | — | — | 3 | — | ISO |
| Sega Saturn | 2352 | RW | 4x | 4x | DAO | 5 | — | BIN/CUE |
| Sega Dreamcast | 2352 | RW | 2x | 2x | DAO | 5 | — | CDI/BIN |
| Sega Mega CD | 2352 | RW | 4x | 4x | DAO | 3 | Yes | BIN/CUE |
| Xbox | 2048 | None | 2x | 4x | DAO | 3 | — | XISO |
| Xbox 360 | 2048 | None | 2x | — | — | 3 | — | ISO |
| GameCube | 2048 | None | 4x | — | — | 3 | — | ISO |
| Wii | 2048 | None | 4x | — | — | 3 | — | ISO |
| Neo Geo CD | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |
| 3DO | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |
| PC Engine | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |
| Atari Jaguar | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |
| Amiga CD32 | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |
| Amiga CDTV | 2352 | RW | 4x | — | — | 3 | — | BIN/CUE |

---

## Xbox 360 Stealth Patching

The **Xbox 360 Stealth Service** handles DMI (Disc Manufacturing Information), PFI (Physical Format Information), Security Sectors (SS), topology data, and video partition verification for Xbox 360 disc images (XGD2 and XGD3 formats).

| Feature | Description |
|:--------|:------------|
| **DMI** | Disc Manufacturing Information patching |
| **PFI** | Physical Format Information validation |
| **SS** | Security Sector verification |
| **Topology Data** | Disc topology verification |
| **Video Partition** | Video partition validation and patching |

Xbox 360 images use the XDVDFS (Xbox Disc Volume File System) with magic `MICROSOFT*XBOX*MEDIA`. The service detects XGD2 (game partition at 0xFD90000) and XGD3 (game partition at 0x2080000) formats.

---

## PS2 ESR Patching

ESR (Electron Solid Runtime) patching allows playing backup DVD games on a soft-modded PlayStation 2 console. When enabled (in **Settings → Gaming Disc Defaults → Auto-ESR Patch**), the application automatically applies the ESR patch to PS2 game images during the build or burn process.

---

## Region-Free Patching

Open Burning Suite can automatically remove region restrictions from game discs. Enable **Auto-Region-Free Patch** in **Settings → Gaming Disc Defaults** to strip region encoding when building or burning game images.

---

## XISO (Xbox Disc Image Format)

The **XISO** format (Xbox DVD Filesystem / XDVDFS) is used by original Xbox and Xbox 360 games. Open Burning Suite supports:

| Operation | Support | Notes |
|:----------|:--------|:------|
| **Read** | ✅ | Read Xbox/Xbox 360 discs as XISO images |
| **Build** | ✅ | Create XISO images from files using the XDVDFS builder |
| **Detect** | ✅ | Auto-detect XDVDFS filesystem by magic header |

The XISO/Xbox gaming preset automatically selects XISO format as the output when reading Xbox game discs.

---

## Auto-Patching Settings

| Setting | Default | Description |
|:--------|:--------|:------------|
| **Auto-ESR Patch** | Off | Automatically apply ESR patching for PS2 DVD compatibility |
| **Auto-Region-Free Patch** | Off | Remove region restrictions from game discs automatically |

Configure these in **Settings → Gaming Disc Defaults**.

---

## Tips & Best Practices

1. **Use raw sector mode for PS1 games.** PlayStation 1 games often rely on subchannel data and specific sector layouts. Use 2352-byte sectors and BIN/CUE format.

2. **Pad GameCube/Wii images to capacity.** Some homebrew loaders expect full-size disc images. Use the "Pad to Capacity" option when building.

3. **Check layer breaks for dual-layer games.** PS2 and Wii dual-layer games require correct layer break positions for proper playback.

4. **Verify gaming disc copies.** Always verify your burned gaming discs against the source image to ensure accuracy.

5. **Use quality media.** Gaming consoles can be more sensitive to disc quality than PC drives. Use reputable disc brands for best compatibility.

---

**Next:** [Supported Formats →]({{ '/supported-formats' | relative_url }})
