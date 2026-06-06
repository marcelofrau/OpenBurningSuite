## ADDED Requirements

### Requirement: AudioWizard step cards SHALL use icons

The 3 mode step cards (Create Audio CD / Copy Music / Rip Audio) SHALL replace emoji with IconTextBlock at IconSize=64.

#### Scenario: Step cards render with icons
- **WHEN** AudioWizard loads
- **THEN** step cards SHALL use IconKey="AudioDisc", "MusicDisc", "Headphones" respectively

### Requirement: All wizard section headers SHALL use icons

Section headers across AudioWizard, VideoWizard, DataWizard, and GameWizard SHALL replace emoji with IconTextBlock at IconSize=50.

#### Scenario: Wizard section headers render with 50px icons
- **WHEN** any wizard view renders a section header
- **THEN** it SHALL use IconTextBlock with IconSize=50 and the mapped IconKey

### Requirement: All wizard action buttons SHALL use icons

Add, Remove, Browse, Refresh, Start, Stop, Cancel, Done buttons across all wizards SHALL use IconButton at IconSize=50.

#### Scenario: Action buttons render with icons
- **WHEN** a wizard renders
- **THEN** Add buttons SHALL use IconKey="Add", Remove="Remove", Browse="OpenFolder", Start="Burn"/"Play"/"Build", Stop="Stop", Cancel="Stop", Done="Verify"

### Requirement: All wizard media controls SHALL use icons

Play, Stop, Pause buttons in AudioWizard and VideoWizard SHALL use IconButton at IconSize=50.

#### Scenario: Media control buttons render with icons
- **WHEN** media controls render
- **THEN** Play SHALL use IconKey="Play", Stop="Stop", Pause="Pause"

### Requirement: Wizard summary lines SHALL support optional icon

The `AddSummaryLine` method SHALL accept an optional `IconKey` parameter. When provided, the summary line renders an IconTextBlock. When omitted, renders plain TextBlock (backward compatible).

#### Scenario: Summary line with icon
- **WHEN** `AddSummaryLine("Task", value, IconKey: "Burn")` is called
- **THEN** the rendered row SHALL show a 16x16 Burn icon before "Task: value"

#### Scenario: Summary line without icon (legacy)
- **WHEN** `AddSummaryLine("Task", value)` is called without IconKey
- **THEN** it SHALL render as plain text (unchanged behavior)

### Requirement: VideoWizard disc type tabs SHALL use icons

The 8 video disc type tabs (DVD-Video, Blu-ray, BDAV, 3D Blu-ray, VCD, SVCD, XSVCD, UHD BD) SHALL use mapped IconKeys.

#### Scenario: Disc type tabs render correctly
- **WHEN** VideoWizard loads each tab
- **THEN** DVD-Video SHALL use IconKey="DvdVideo", BD="BdVideo", BDAV="VideoCamera", 3D="ThreeD", VCD/SVCD/XSVCD="Videotape", UHD="BdVideo"

### Requirement: GameWizard console detection SHALL show icon

When gaming system is auto-detected, the summary line SHALL show the corresponding console icon.

#### Scenario: Auto-detected console shows icon
- **WHEN** GameWizard detects a PlayStation game
- **THEN** the summary line SHALL use IconKey="ConsolePlaystation"
- **WHEN** Xbox is detected
- **THEN** IconKey="ConsoleXbox"
