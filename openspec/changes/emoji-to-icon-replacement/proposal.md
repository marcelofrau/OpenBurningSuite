## Why

Open Burning Suite uses Unicode emoji extensively for all visual identity — navigation buttons, section headers, action buttons, status indicators, and help documentation. Emoji rendering varies significantly across platforms (Windows Segoe UI Emoji vs Linux vs macOS), creating an inconsistent and unprofessional appearance. The project has a curated 628-icon personal collection at `marcelofrau/icons8-personal-set` with 108 stems already mapped to OBS features in `openburning-icons.md`. Replacing emoji with PNG icons ensures pixel-perfect consistency regardless of OS.

## What Changes

- Replace ~1,200 emoji occurrences across 16 `.axaml` views with PNG icons from the personal icon set
- Introduce reusable Avalonia controls: `IconTextBlock`, `IconButton`, `IconSourceExtension`
- Create `Assets/Icons/` directory with icons at 50x50 (default), 16x16 (status/tight UI), 128x128 (hero/display)
- Add `Helpers/IconHelper.cs` for centralized icon key management and path resolution
- Preserve emoji in runtime log messages (Phase 2, out of scope)
- No API or service layer changes — purely UI presentation

## Capabilities

### New Capabilities

- `icon-system`: Centralized icon infrastructure — helper class, reusable controls, asset pipeline. Icon keys, path resolution, Avalonia resource embedding, and widget library (`IconTextBlock`, `IconButton`, `IconSourceExtension`).
- `main-nav-icons`: MainWindow navigation sidebar, title bar, and home screen quick-start tiles — 32 emoji instances replaced with icons.
- `help-view-icons`: HelpView table of contents, section headers, info cards, and badges — ~52 emoji instances, 3 repetitive patterns.
- `settings-icons`: SettingsView sidebar, section headers, and action buttons — ~28 emoji instances.
- `wizard-icons`: AudioWizard, VideoWizard, DataWizard, GameWizard — step cards, section headers, action buttons, media controls, expanders, and code-behind summary lines across 4 wizard views (~180 XAML + ~100 code-behind).
- `advanced-view-icons`: AdvancedView drive operation buttons — 9 emoji instances.
- `disc-info-icons`: DiscInfoView refresh/loading/no-media/save/copy/log/clear — 7 emoji instances with hero 128x128 empty-state icon.
- `discover-view-icons`: DiscoverView probe/analyze/refresh buttons plus big media-type indicator (128x128) — 6 emoji + new feature.
- `burn-view-icons`: WriteView, ReadView, BuildView, VerifyView — action buttons, section headers, expanders, hints (~41 XAML).
- `blank-copy-wizard-icons`: BlankWizardView and CopyWizardView — ~6 emoji total.

### Modified Capabilities

None. This is purely a UI presentation change — no spec-level behavior changes.

## Impact

**Affected files:**
- 16 `.axaml` view files across `Views/`
- 4 `.axaml.cs` code-behind files (wizard summary/status lines)
- New: `Controls/IconTextBlock.cs`, `Controls/IconButton.cs`, `Controls/IconSourceExtension.cs`, `Controls/IconConverter.cs`
- New: `Helpers/IconHelper.cs`
- New: `Assets/Icons/50/`, `Assets/Icons/16/`, `Assets/Icons/big/` with ~85 PNGs
- Modified: `.csproj` (AvaloniaResource entries for icons)

**Dependencies:**
- Icon files copied from `C:\Users\marcelo\workspace\icons8-personal-set\` (local clone)
- ~15 new icons to source for missing concepts (Lightbulb, Home, Help, Loading, etc.)
- No NuGet or external dependency changes

**Risk:**
- Low — purely visual, no logic changes
- Regression risk: buttons may resize if icon dimensions don't match emoji baseline
- Mitigation: verify each view after conversion, maintain consistent IconSize per context
