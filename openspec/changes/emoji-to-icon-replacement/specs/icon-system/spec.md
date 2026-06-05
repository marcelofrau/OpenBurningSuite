## ADDED Requirements

### Requirement: The system SHALL provide a centralized icon key registry

The system SHALL provide a static `IconHelper` class resolving symbolic icon keys to asset URIs.

#### Scenario: IconHelper resolves key to URI
- **WHEN** `IconHelper.GetUri("Burn", 50)` is called
- **THEN** it returns `"avares://OpenBurningSuite/Assets/Icons/50/icons8-cd-3d-50.png"`

#### Scenario: IconHelper contains all required keys
- **WHEN** any key from the approved mapping list is queried via `IconHelper.Keys`
- **THEN** the key SHALL exist in the static `AllKeys` dictionary

#### Scenario: Unknown key returns null
- **WHEN** `IconHelper.GetUri("NonExistentKey")` is called
- **THEN** it returns null

### Requirement: The system SHALL provide an IconTextBlock control

The system SHALL provide an `IconTextBlock` ContentControl rendering an Image followed by a TextBlock horizontally.

#### Scenario: IconTextBlock renders icon and text
- **WHEN** `<ui:IconTextBlock IconKey="Audio" Text="Audio Tracks" IconSize="50"/>` is used in XAML
- **THEN** it renders a 50x50 Image followed by "Audio Tracks" TextBlock

#### Scenario: IconTextBlock uses default size
- **WHEN** `IconSize` is not specified
- **THEN** it defaults to 50

### Requirement: The system SHALL provide an IconButton control

The system SHALL provide an `IconButton` subclass of `Button` with `IconKey`, `Text`, `IconSize`, and `IconOnly` properties.

#### Scenario: IconButton renders with icon and text
- **WHEN** `<ui:IconButton IconKey="Burn" Text="Burn / Write" Click="OnWriteClick"/>` is used
- **THEN** it renders a 50x50 icon followed by "Burn / Write" text, and Click fires normally

#### Scenario: IconButton in IconOnly mode
- **WHEN** `<ui:IconButton IconKey="Refresh" IconOnly="True" IconSize="32"/>` is used
- **THEN** it renders only a 32x32 icon with no text

#### Scenario: IconButton supports all Button features
- **WHEN** Command, CommandParameter, Flyout, or ToolTip are set on IconButton
- **THEN** they behave identically to a standard Button

### Requirement: The system SHALL provide an IconSourceExtension

The system SHALL provide a `MarkupExtension` resolving an icon key + size to an `IImage` source for inline `<Image>` usage.

#### Scenario: MarkupExtension resolves in XAML
- **WHEN** `<Image Source="{ui:IconSource Burn, Size=50}" Width="50" Height="50"/>` is evaluated
- **THEN** the Image source is set to the resolved PNG bitmap

### Requirement: The system SHALL embed icons as Avalonia resources

All icon PNG files SHALL be included in the project as `AvaloniaResource` and accessible via `avares://` URIs.

#### Scenario: Icons accessible at runtime
- **WHEN** an `avares://OpenBurningSuite/Assets/Icons/50/icons8-cd-3d-50.png` URI is loaded
- **THEN** the bitmap is returned successfully

### Requirement: The system SHALL preserve the same visual layout after replacement

Each icon+text combination SHALL maintain the same visual alignment and spacing as the original emoji+text.

#### Scenario: IconButton spacing matches original
- **WHEN** an IconButton renders with IconSize=50 and text
- **THEN** the horizontal spacing between icon and text SHALL be 8px
