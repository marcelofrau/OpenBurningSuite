## ADDED Requirements

### Requirement: DiscInfoView buttons SHALL use icons

All action buttons SHALL replace emoji with IconButton at IconSize=50.

#### Scenario: Buttons render with icons
- **WHEN** DiscInfoView renders
- **THEN** BtnRefresh SHALL use IconKey="Refresh", BtnSaveDiscInfo="Save", BtnCopyInfo="Copy", BtnSaveLog="Save", BtnClearLog="Clear"

### Requirement: DiscInfoView loading indicator SHALL show icon

The loading spinner SHALL replace ⏳ with IconTextBlock at IconSize=16.

#### Scenario: Loading indicator shows icon
- **WHEN** PnlLoading is visible
- **THEN** the indicator SHALL show a 16x16 Loading icon followed by "Reading disc information..."

### Requirement: DiscInfoView no-media state SHALL show hero icon

The no-media empty state SHALL replace 💿 (FontSize=32) with a 128x128 hero icon.

#### Scenario: No media state shows 128x128 icon
- **WHEN** PnlNoMedia is visible
- **THEN** the icon SHALL be 128x128 and use the DiscEmpty icon key

### Requirement: DiscInfoView log expander header SHALL use icon

The log expander header SHALL replace 📝 with IconTextBlock at IconSize=50.

#### Scenario: Log header shows icon
- **WHEN** the log expander renders
- **THEN** the header SHALL show a 50x50 Log icon followed by "Log" text
