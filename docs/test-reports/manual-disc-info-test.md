# Disc Info — Manual Test Report

**Date**: 2026-06-05
**Drive**: Optiarc DVD RW AD-7540A (Firmware 1.D3)
**Branch**: `feat/disc-info-panel` @ `1b5cdae`
**Tested by**: @marcelofrau

## Results

| Media | Type | Status | Notes |
|-------|------|--------|-------|
| Elgin DVD-R 8x | DVD-R | ✅ OK | MID, disc info, physical format all correct |
| Elgin DVD+R DL 8x | DVD+R DL | ✅ OK | MID `UMEDISC-DL1-64` correctly resolved via format 0x11 + prefix lookup |
| Verbatim CD-R (unknown speed, not empty) | CD-R | ✅ OK | Detected, track/session info correct |
| Verbatim CD-R | CD-R | ✅ OK | Blank media detected correctly |
| PlayStation 1 - Gran Turismo (pressed) | CD-ROM | ⚠️ Almost OK | Mode 2 / Mode 1 mismatch between TOC and track type — open issue |
| Verbatim CD-R 52x | CD-R | ✅ OK |    |
| Xbox 1 - Midtown Madness 3 (pressed) | DVD-ROM | ✅ OK |    |

## Open Issues

- PS1 (Gran Turismo): Mode 2 / Mode 1 mismatch between TOC parsing and track type detection. Needs further investigation.
