# Changelog

All notable changes to this fork are documented here.

## [Unreleased]

### Added
- **Disc Info panel** — drive/media information (MID, ATIP, format, speeds, buffer, firmware, serial)
- **CHD support** — burn `.chd` retro gaming images directly via `chdman extractcd`
- **Elevation indicator** — status bar icon shows admin/elevated privilege state
- **PNG icon set** — replaced all emoji (300+) with custom PNG icons across 16 views, with per-view stem isolation for independent icon updates
- **IconHelper, IconTextBlock, IconButton, IconConverter** — reusable icon controls for Avalonia

### Changed
- **Button colors** — PrimaryBtn (blue) and DangerBtn (red) darkened for better icon contrast
- **Expanders** — all `SettingsExpander` instances now stretch to full width
- **Visual identity** — replaced emoji-based UI with professional icon set throughout the application
- **Wizard step indicators** — unified Border+number pattern across all 6 wizards
- **BuildView boot image** — uses dedicated bootable icon instead of generic idea icon

### Fixed
- **BurnService CHD routing** — `.CHD` extension now correctly detected for burning
- **Shared icon stems** — isolated conflicting icon references across views to allow independent customization

### Technical
- .NET 8 + Avalonia UI 11.3.12
- Zero build warnings, zero errors
