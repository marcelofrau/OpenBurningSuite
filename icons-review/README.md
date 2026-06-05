# Icon Review Workspace

## Purpose

Each folder here matches a view (e.g., `MainWindow/`, `WriteView/`).
Inside each folder are the **PNG icons** currently used in that view,
named by their UI context (button label, section header, etc.).

Use this to review icon choices and tell me which icons to swap.

## File naming

```
{UI_Context}_{icon_stem}_{size}.png
```

Example: `Burn_icons8-cd-3d_16.png` = the cd icon used on the Burn button (16px).

## How to review

1. Open a view folder
2. Look at the PNGs — each shows what icon is used where
3. If you want to change an icon:
   - Tell me the **filename** (e.g. `Burn_icons8-cd-3d_16.png`)
   - Tell me which **new icon stem** to use (e.g. `icons8-fire`, `fluentui-optical-disk`)
   - I'll update the AXAML source and copy the new PNG

## Map file

Each folder has `_icons-map.md` with:
- Line number in the AXAML source
- Icon stem name
- Size (16px or 50px)
- UI context text

## Script

The extraction was done by `scripts/extract-icons-for-review.py`.
Re-run it to refresh after changes:

```bash
python3 scripts/extract-icons-for-review.py
```

## Future: isolate icons per context

Currently, if two buttons use the same icon stem (e.g., `icons8-settings-2d` for both sidebar "Advanced"
and "Advanced Options" expander), they share the same PNG file. Changing one changes both.

To isolate them:
1. Pick a new unique stem for the context (e.g., `icons8-advanced-options`)
2. Copy the PNG to `Assets/Icons/{16,50}/icons8-advanced-options-{16,50}.png`
3. Update the AXAML source reference from `icons8-settings-2d` to `icons8-advanced-options`

This can be done per-context during the validation phase or later.

## Icon source mapping

| Stem | Actual file |
|------|------------|
| `icons8-*` | `Assets/Icons/{size}/icons8-{name}-{size}.png` |
| `fluentui-*` | `Assets/Icons/{size}/fluentui-{name}-{size}.png` |

All 76 icons exist in both `Assets/Icons/16/` and `Assets/Icons/50/`.
