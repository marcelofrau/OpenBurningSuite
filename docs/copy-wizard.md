---
layout: default
title: Copy Disc Wizard
permalink: /copy-wizard
---

# Copy Disc Wizard

Guided step-by-step workflow for copying optical discs to image files.

---

## Workflow

1. **Select drive** — Choose the source optical drive
2. **Select output format** — ISO, BIN/CUE, NRG, MDF/MDS, CDI, CCD/IMG/SUB, TOC/BIN, IMG, CHD
3. **Set options** — Output path, read speed, error recovery, subchannel mode
4. **Apply gaming preset (optional)** — Auto-configure settings for a specific gaming console
5. **Copy** — Read the disc and save to the chosen image format

---

## Gaming Presets for Copying

When a gaming preset is applied, the wizard automatically configures:
- **Sector size** (2048 or 2352 depending on console)
- **Subchannel mode** (RW, RW_RAW, or None)
- **Read speed** (optimized for the console)
- **Error recovery** (for damaged gaming discs)
- **Audio paranoia** mode
- **Output format** (ISO for DVD-based consoles, BIN/CUE for CD-based)

See [Game Disc Presets]({{ '/gaming-discs' | relative_url }}) for the full list.

---

**Next:** [Blank Disc Wizard →]({{ '/blank-wizard' | relative_url }})
