---
layout: default
title: Project Status
permalink: /status
---

# Open Burning Suite — Project Status

> Updated: Jun 2026
> Active branch: `main`

## ✅ Completed

### 🎨 UX — Emoji to PNG Icons (100%)
- **Branch:** `feat/emoji-to-icon-replacement`
- **76 icons** in `Assets/Icons/16/` and `Assets/Icons/50/` (icons8 + fluentui)
- **16 views** with emoji replaced by `<Image>` (~315 occurrences):

| View | Emoji | Status |
|------|-------|--------|
| MainWindow | 31 | ✅ |
| AdvancedView | 9 | ✅ |
| DiscInfoView | 8 | ✅ |
| BlankWizardView | 2 | ✅ |
| CopyWizardView | 4 | ✅ |
| DiscoverView | 6 | ✅ |
| VerifyView | 7 | ✅ |
| ReadView | 7 | ✅ |
| BuildView | 10 | ✅ |
| WriteView | 14 | ✅ |
| AudioWizardView | 27 | ✅ |
| VideoWizardView | 80 | ✅ |
| DataWizardView | 28 | ✅ |
| GameWizardView | 19 | ✅ |
| SettingsView | 28 | ✅ |
| HelpView | 52 | ✅ |

- **Infrastructure:** `IconHelper.cs`, `IconSourceExtension`, `IconTextBlock`, `IconButton`, `IconConverter`, `.csproj` with `AvaloniaResource`
- **Logger:** `Logger.cs` + try-catch in `Program.cs`/`App.axaml.cs` → logs at `bin/Debug/net8.0/logs/obs_*.log`
- **Build:** 0 errors, 0 warnings

### 🔬 Media Info — Disc Info Panel (100%)
- **Branch:** `feat/disc-info-panel` (merged via PR #1)
- Populate `TxtMediaId` with real disc data
- MID, manufacturer, layers, write speeds

### 🔧 CHD — Bug routing (B1 fixed)
- `BurnService.cs:59` — `.CHD` added to routing check

## 🔄 In Progress

*(none at the moment)*

## 📋 Pending

### 🔥 CHD — Complete support
| # | Task | Priority |
|---|------|----------|
| 1 | `ConvertChdToBinCueAsync` — add `IProgress<int>` | medium |
| 2 | CHD as output format (`chdman createcd`) | low |
| 3 | CHD in VerifyService | low |
| 4 | CHD DVD/HD/Blu-ray support (`extracthd`/`extractld`) | low |
| 5 | Display CHD metadata (`chdman info`) | low |
| 6 | Validate extracted BIN/CUE | low |

### 🔬 Media Info — Expansion
| # | Task | Priority |
|---|------|----------|
| 8 | Add MID, Manufacturer, Write Speeds to panel | low |
| 9 | Real speeds per media type | low |
| 10 | Drive buffer, firmware, serial | low |

### 🛡️ Protection Profiles (CloneCD-style)
| # | Task | Priority |
|---|------|----------|
| 13-16 | SafeDisc, SecuROM, StarForce etc. | low |

### 📝 Inline ISO Editor
| # | Task | Priority |
|---|------|----------|
| 17-19 | IsoEditorService, TreeView, drag & drop | low |

### 📦 Burn Queue
| # | Task | Priority |
|---|------|----------|
| 20-22 | BurnQueueService, UI, multi-drive | low |

### 💾 Save/Load Project (.obsproject)
| # | Task | Priority |
|---|------|----------|
| 23-24 | Serialize/restore config | low |

### 🚀 Bootable USB
| # | Task | Priority |
|---|------|----------|
| 25-27 | Detect USB, raw write, wizard | low |

### 📋 Quality of Life
| # | Task | Priority |
|---|------|----------|
| 28 | Recent files list | low |
| 29 | Drag & drop .iso/.chd onto MainWindow | low |
| 30 | Graphical layer break picker (dual-layer DVD) | low |
| 31 | Split `FormatHelper.GetImageType()` | low |

### 🔒 Security / Admin
| # | Task | Priority |
|---|------|----------|
| R1-R3 | Alternatives to requireAdministrator | low |

## Note: icon isolation by context
Currently, different buttons/sessions using the same icon stem (e.g. sidebar "Advanced"
and expander "Advanced Options" both use `icons8-settings-2d`) share the same PNG.
To isolate: create new stem, copy PNG, update AXAML reference.
Decision postponed to validation phase.

## 🐛 Known Bugs
- B1: ✅ fixed (CHD routing)

## Architecture
- .NET 8 + Avalonia UI 11.3.12
- BIN/CUE universal intermediate format
- Static format parsers
- External tools: ffmpeg, chdman

## Referências
- [Upstream](https://github.com/SvenGDK/OpenBurningSuite)
- [Fork](https://github.com/marcelofrau/OpenBurningSuite)
