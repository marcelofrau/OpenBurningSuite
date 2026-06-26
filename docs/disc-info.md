---
layout: default
title: Disc Information
permalink: /disc-info
---

# Disc Information

View detailed technical information about inserted discs and optical drives, including MID, manufacturer, ATIP, physical format information, and supported speeds.

---

## Drive Selection

Select a drive from the dropdown, then click **Refresh** to read disc information. The app queries the drive firmware via SCSI/MMC commands to retrieve detailed disc parameters.

---

## Information Displayed

### Disc Type & Format
- Media type (CD-R, DVD+R, BD-R, etc.)
- Disc state (Empty, Appendable, Closed)
- Physical format information (PFI)
- Number of layers (single/dual layer)
- Layer type (PTP/OTP for DVD, etc.)

### Manufacturer Information
- **Media ID (MID):** Manufacturer identifier code
- **Manufacturer name:** Readable manufacturer name
- **Disc dye type:** Recording layer dye type (for CD-R/DVD±R)

### ATIP / Pre-Recorded Information
- **ATIP:** Absolute Time In Pre-groove (CD media)
- Pre-recorded lead-in information (DVD/BD)
- Disc application code

### Speed Information
- Supported write speeds for the specific media
- Optimal write speed (if auto-detection is enabled)

### Capacity & Layout
- Total disc capacity in sectors and bytes
- Used space
- Free space
- Session count
- Track count and types
- Layer break position (dual-layer media)

---

## Controls

| Control | Description |
|:--------|:------------|
| **Save** | Save all displayed information to a text file |
| **Copy** | Copy all displayed information to clipboard |
| **Refresh** | Re-read disc information from the drive |
| **Log** | Collapsible log panel shows raw SCSI command responses |

---

**Next:** [Burning Discs →]({{ '/burning' | relative_url }})
