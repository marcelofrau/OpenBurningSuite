---
layout: default
title: Video Disc Wizard
permalink: /video-wizard
---

# Video Disc Wizard

Guided step-by-step workflow for authoring DVD-Video, Blu-ray, VCD, SVCD, and XSVCD discs.

> **Prerequisite:** [FFmpeg](https://ffmpeg.org/) must be installed and available on your system PATH. FFmpeg is used for video transcoding and multiplexing.

---

## Supported Video Disc Types

| Type | Description | Media | Max Resolution |
|:-----|:------------|:------|:---------------|
| **DVD-Video** | Standard DVD movie disc | DVD±R, DVD±R DL | 720×480 (NTSC) / 720×576 (PAL) |
| **Blu-ray** | Standard Blu-ray movie disc (BDAV) | BD-R, BD-RE | 1920×1080 |
| **Blu-ray 3D** | Stereoscopic 3D Blu-ray | BD-R, BD-RE | 1920×1080 |
| **VCD** | Video CD (MPEG-1) | CD-R | 352×240 (NTSC) / 352×288 (PAL) |
| **SVCD** | Super Video CD (MPEG-2) | CD-R | 480×480 (NTSC) / 480×576 (PAL) |
| **XSVCD** | Extended Super Video CD (higher bitrate MPEG-2) | CD-R | 480×480 (NTSC) / 480×576 (PAL) |

### Notes
- DVD-Video, Blu-ray, and Blu-ray 3D authoring requires significant temporary disk space
- VCD/SVCD/XSVCD are authored on standard CD-R media
- FFmpeg handles all video encoding and transcoding automatically

---

## Workflow

1. **Select disc type** — Choose DVD-Video, Blu-ray, or VCD/SVCD/XSVCD
2. **Add video files** — Select source video files (the wizard accepts common formats)
3. **Configure options** — Set aspect ratio, audio track, subtitles (DVD/Blu-ray)
4. **Author & burn** — The wizard transcodes, multiplexes, and burns to disc

---

**Next:** [Data Disc Wizard →]({{ '/data-wizard' | relative_url }})
