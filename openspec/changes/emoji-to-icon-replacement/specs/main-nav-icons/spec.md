## ADDED Requirements

### Requirement: MainWindow navigation sidebar SHALL use icons

All sidebar buttons in MainWindow.axaml SHALL replace emoji+text Content with IconButton using mapped icon keys.

#### Scenario: MAIN section buttons use icons
- **WHEN** the sidebar renders
- **THEN** BtnHome SHALL use IconKey="Home", BtnDiscInfo="DiscInfo", BtnDiscover="Discover", BtnRead="Disc", BtnBuild="Build", BtnWrite="Burn", BtnVerify="Verify"
- **AND** BtnAdvanced SHALL use the same IconButton pattern (currently uses nested TextBlock)

#### Scenario: QUICK START section buttons use icons
- **WHEN** the sidebar renders
- **THEN** BtnAudioWizard SHALL use IconKey="Audio", BtnVideoWizard="Video", BtnDataWizard="Data", BtnGameWizard="Game", BtnCopyWizard="Copy", BtnBlankWizard="Clear"

#### Scenario: Title bar buttons use icons
- **WHEN** the title bar renders
- **THEN** BtnHomeNav SHALL use IconKey="Home" at IconSize=32, BtnSettingsNav="Settings", BtnHelpNav="Help"

#### Scenario: Home screen tiles use icons
- **WHEN** the home screen renders
- **THEN** each tile SHALL use IconTextBlock with IconSize=64 for big tiles and IconSize=50 for small tiles
