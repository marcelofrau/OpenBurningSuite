## ADDED Requirements

### Requirement: SettingsView sidebar SHALL use small icons

The 11 ToC sidebar buttons in SettingsView SHALL replace emoji with IconButton at IconSize=16.

#### Scenario: Settings sidebar uses 16px icons
- **WHEN** SettingsView renders
- **THEN** each sidebar button SHALL use IconButton with IconSize=16, IconKey mapped to the setting section

### Requirement: SettingsView section headers SHALL use icons

The 12 section icon TextBlocks (separate element pattern) SHALL be replaced with IconTextBlock at IconSize=50.

#### Scenario: Section headers render with 50px icons
- **WHEN** any settings section renders
- **THEN** the section header SHALL use IconTextBlock with IconSize=50

### Requirement: SettingsView action buttons SHALL use icons

Save, Reset, and Open File buttons SHALL use IconButton at IconSize=50.

#### Scenario: Action buttons render with icons
- **WHEN** the settings footer renders
- **THEN** BtnSave SHALL use IconKey="Save", Reset="Reset", OpenFile="OpenFile"
