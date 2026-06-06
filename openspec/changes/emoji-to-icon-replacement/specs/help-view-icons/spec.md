## ADDED Requirements

### Requirement: HelpView ToC buttons SHALL use icons

Each table-of-contents button SHALL replace emoji prefix with IconButton at IconSize=16.

#### Scenario: ToC buttons render with 16px icons
- **WHEN** HelpView loads
- **THEN** all 18 ToC buttons SHALL use IconButton with IconSize=16, IconKey mapped per the openburning-icons.md stems

### Requirement: HelpView section headers SHALL use icons

Each section header `<TextBlock Classes="SectionHeader"/>` SHALL replace emoji prefix with IconTextBlock at IconSize=50.

#### Scenario: Section headers render with 50px icons
- **WHEN** HelpView loads
- **THEN** all section headers SHALL use IconTextBlock with IconSize=50

### Requirement: HelpView info cards SHALL use icons

The 9 "Technical Detail" info cards SHALL replace 💡 emoji with IconTextBlock IconKey="Lightbulb" at IconSize=16.

#### Scenario: Info cards show lightbulb icon
- **WHEN** a Technical Detail card renders
- **THEN** the header SHALL show a 16x16 Lightbulb icon followed by "Technical Detail" text
