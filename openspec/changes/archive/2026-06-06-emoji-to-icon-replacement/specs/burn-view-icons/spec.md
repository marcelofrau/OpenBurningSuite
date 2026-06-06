## ADDED Requirements

### Requirement: WriteView action buttons and labels SHALL use icons

Action buttons, section headers, hints, and expander headers in WriteView SHALL replace emoji with IconButton/IconTextBlock.

#### Scenario: WriteView buttons render with icons
- **WHEN** WriteView renders
- **THEN** Burn button SHALL use IconKey="Burn", Cancel="Stop", Clear="Clear", AddFolder="AddFolder", Remove="Remove", Refresh="Refresh"
- **AND** section headers SHALL use IconTextBlock: DetectedContent="Search", AdvancedOptions="Settings", GamingPatches="GamePatches"
- **AND** hints SHALL use IconTextBlock at IconSize=16 with IconKey="Lightbulb"
- **AND** warning text SHALL use IconTextBlock at IconSize=16 with IconKey="Warning"
- **AND** encryption indicators SHALL use IconKey="Lock"

### Requirement: ReadView buttons and labels SHALL use icons

#### Scenario: ReadView renders with icons
- **WHEN** ReadView renders
- **THEN** Start Copy SHALL use IconKey="Play", Cancel="Stop", Clear="Clear", Refresh="Refresh", Detect="Search"
- **AND** section headers SHALL use IconTextBlock: DetectedContent="Search", GamingPresets="Game", Encryption="Lock", DragHint="Lightbulb" at IconSize=16

### Requirement: BuildView buttons and labels SHALL use icons

#### Scenario: BuildView renders with icons
- **WHEN** BuildView renders
- **THEN** Build Image SHALL use IconKey="Build", Cancel="Stop", Clear="Clear", AddTrack="Add", RemoveTrack="Remove"
- **AND** expanders SHALL use IconTextBlock: AdvancedOptions="Settings", BootImage="Bootable", CdXaOptions="DiscDvd", AudioCd="Audio", GamingBuilders="Game"

### Requirement: VerifyView buttons and labels SHALL use icons

#### Scenario: VerifyView renders with icons
- **WHEN** VerifyView renders
- **THEN** Start Verification SHALL use IconKey="Verify", Cancel="Stop", Clear="Clear", Refresh="Refresh", Copy="Copy"
- **AND** hint SHALL use IconTextBlock IconKey="Lightbulb" at IconSize=16
