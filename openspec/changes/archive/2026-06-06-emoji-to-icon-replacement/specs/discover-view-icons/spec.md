## ADDED Requirements

### Requirement: DiscoverView action buttons SHALL use icons

All action buttons SHALL replace emoji with IconButton at IconSize=50.

#### Scenario: Buttons render with icons
- **WHEN** DiscoverView renders
- **THEN** BtnRefreshDrives SHALL use IconKey="Refresh", BtnProbeMedia="Probe", BtnAnalyzeContent="Analyze"

### Requirement: DiscoverView empty state SHALL show icon

The "No optical drives detected" message SHALL replace 💿 with IconTextBlock IconKey="Disc" at IconSize=128.

#### Scenario: Empty state shows 128x128 icon
- **WHEN** NoDrivesPanel is visible
- **THEN** the icon SHALL be 128x128

### Requirement: DiscoverView media type indicator SHALL show large icon

The MEDIA TYPE info card SHALL display a 128x128 icon representing the detected media type.

#### Scenario: Media type shows appropriate icon
- **WHEN** media is detected as CD
- **THEN** the MEDIA TYPE card SHALL show the CD icon at 128x128
- **WHEN** media is detected as DVD
- **THEN** it SHALL show the DVD icon
- **WHEN** media is detected as Blu-ray
- **THEN** it SHALL show the Blu-ray icon
