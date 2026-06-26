---
name: assets-icons
description: Use ONLY when adding, finding, or referencing icons/images/assets in this project. Covers naming convention, directory structure, format rules, size selection, personal set path, and workflow.
---

# Assets & Icons Guide

## Directory structure

```
XBVault/Assets/
├── Fonts/             # Oxanium-*.ttf, ProFont*.ttf
├── Icons/
│   └── app.ico        # App icon only (sole .ico)
├── Themes/
│   └── BladesTheme.axaml
└── Views/             # Per-view folder
    ├── MainWindow/
    ├── ConnectionWindow/
    └── ...
```

Each view/window needing icons gets subfolder under `Views/`.

## Naming convention

Pattern: `{viewname}-{descriptor}[-{size}].png`

- `viewname` — lowercase, no separator (`mainwindow`, `errordialog`)
- `descriptor` — kebab-case (`about`, `step1-disabled`, `close`)
- `size` — optional pixel size for icons (`16`, `20`, `32`, `48`, `100`)
- Omit size only for full-width banners/backgrounds

Examples: `mainwindow-about-32.png`, `custominstall-step1-disabled-20.png`, `connection-banner.png`.

## Format rules

- **Always PNG.** No JPG, BMP, GIF, SVG, WebP.
- `.ico` only for `Assets/Icons/app.ico` — never elsewhere unless explicitly asked.
- Never convert `.ico` → PNG. Source PNG from personal set.

## Size selection

| Context | Size |
|---------|------|
| Inline with small text / status | 16×16 |
| Toolbar buttons, compact actions | 20×20 |
| Tab / sidebar icons | 32×32 |
| Standalone buttons, dialog body | 48×48 |
| Large indicators (success/failure) | 100×100 |
| Full-width banners | Omit size in name |

When unsure between two sizes, prefer larger — scale down in XAML via `Width`/`Height`.

## Icon source

Personal Icons8-derived collection at:
```
F:\workspace\icons8-personal-set
```

Organized by size: `{size}x{size}/{name}-{size}.png`. Also `ico/`, `catalog/` dirs.

## Workflow

1. Identify needed size from context
2. Copy from `icons8-personal-set/{size}x{size}/{name}-{size}.png`
3. Rename per convention: `{viewname}-{descriptor}-{size}.png`
4. Place in `XBVault/Assets/Views/{ViewName}/`
5. Reference in AXAML: `avares://XBVault/Assets/Views/{ViewName}/{filename}`

Minimum required for a new window: `{viewname}-close-20.png` (close button).

## View-agnostic icons

If icon needed by multiple views, place in `Assets/Icons/` (currently only `app.ico`).

## Attribution

Third-party icons must be attributed in `docs/ATTRIBUTIONS.md`. Confirm license allows redistribution before committing.
