## ADDED Requirements

### Requirement: AdvancedView action buttons SHALL use icons

All 9 action buttons in AdvancedView SHALL replace emoji with IconButton at IconSize=50.

#### Scenario: Drive operation buttons render with icons
- **WHEN** AdvancedView renders
- **THEN** Eject SHALL use IconKey="Eject", Load="Load", QuickBlank="Lightning", FullBlank="Delete", QuickFormat="Lightning", FullFormat="DiscDvd", Cancel="Stop", Clear="Clear", Refresh="Refresh"
