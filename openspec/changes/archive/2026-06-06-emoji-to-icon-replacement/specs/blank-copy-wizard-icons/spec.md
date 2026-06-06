## ADDED Requirements

### Requirement: BlankWizardView SHALL use icons

#### Scenario: BlankWizard renders with icons
- **WHEN** BlankWizardView renders
- **THEN** the header SHALL use IconTextBlock IconKey="Clear" at IconSize=50
- **AND** Refresh button SHALL use IconButton IconKey="Refresh"
- **AND** info text SHALL use IconTextBlock IconKey="Info" at IconSize=16

### Requirement: CopyWizardView SHALL use icons

#### Scenario: CopyWizard renders with icons
- **WHEN** CopyWizardView renders
- **THEN** the header SHALL use IconTextBlock IconKey="Copy" at IconSize=50
- **AND** Refresh button SHALL use IconButton IconKey="Refresh"
- **AND** Browse buttons SHALL use IconButton IconKey="OpenFolder" IconOnly="True" at IconSize=50
