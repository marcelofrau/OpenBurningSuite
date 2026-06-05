## 1. Infrastructure

- [x] 1.1 Copy ~85 icon PNGs from icons8-personal-set to `Assets/Icons/` (50/, 16/, big/)
- [x] 1.2 Create `.csproj` AvaloniaResource glob for `Assets/Icons/**/*.png`
- [x] 1.3 Create `Helpers/IconHelper.cs` — static key→URI dictionary and `GetUri(key, size)` method
- [x] 1.4 Create `Controls/IconSourceExtension.cs` — MarkupExtension resolving `{ui:IconSource Key, Size=50}`
- [x] 1.5 Create `Controls/IconTextBlock.cs` — ContentControl with IconKey, Text, IconSize properties
- [x] 1.6 Create `Controls/IconButton.cs` — Button subclass with IconKey, Text, IconSize, IconOnly properties
- [x] 1.7 Create `Controls/IconConverter.cs` — IValueConverter for runtime binding scenarios
- [x] 1.8 Modify `AddSummaryLine` in wizard code-behind to accept optional IconKey parameter
- [x] 1.9 Source ~15 missing icons (Lightbulb, Home, Help, Loading, etc.) and add to Assets
- [x] 1.10 Build and verify no compilation errors

## 2. MainWindow — Navigation + Home Screen

- [x] 2.1 Replace sidebar MAIN section buttons (Home, DiscInfo, Discover, Read, Build, Write, Verify, Advanced) with IconButton
- [x] 2.2 Replace sidebar QUICK START section buttons (Audio, Video, Data, Game, Copy, Blank) with IconButton
- [x] 2.3 Replace title bar buttons (Home, Settings, Help) with IconButton at IconSize=32
- [x] 2.4 Replace home screen tiles with IconTextBlock at IconSize=64 (big) / 50 (small)
- [x] 2.5 Verify sidebar layout, spacing, and click behavior

## 3. AdvancedView — Drive Operations

- [x] 3.1 Replace all 9 action buttons with IconButton (Eject, Load, QuickBlank, FullBlank, QuickFormat, FullFormat, Cancel, Clear, Refresh)
- [x] 3.2 Verify button alignment and operation

## 4. DiscInfoView — Disc Details

- [x] 4.1 Replace Refresh, Save (×2), Copy, Clear buttons with IconButton
- [x] 4.2 Replace loading spinner (⏳) with IconTextBlock IconSize=16
- [x] 4.3 Replace no-media icon with 128x128 hero Image
- [x] 4.4 Replace log expander header with IconTextBlock

## 5. DiscoverView — Drive & Media Scanner

- [x] 5.1 Replace Refresh, Probe Media, Analyze Content buttons with IconButton
- [x] 5.2 Replace no-drives empty-state with 128x128 hero icon
- [x] 5.3 Add 128x128 media-type indicator to MEDIA TYPE card (CD/DVD/BD binding)
- [x] 5.4 Verify media detection updates icon correctly

## 6. SettingsView — Application Settings

- [x] 6.1 Replace 11 ToC sidebar buttons with IconButton at IconSize=16
- [x] 6.2 Replace 12 section icon TextBlocks with IconTextBlock at IconSize=50
- [x] 6.3 Replace Save, Reset, Open File action buttons with IconButton
- [x] 6.4 Verify sidebar scroll and navigation

## 7. AudioWizard — Audio CD / Music / Rip

- [x] 7.1 Replace 3 mode step cards with IconTextBlock at IconSize=64
- [x] 7.2 Replace section headers with IconTextBlock at IconSize=50
- [x] 7.3 Replace action buttons (Add, Remove, Browse, Import, Export, Refresh, Start, Cancel, Done) with IconButton
- [x] 7.4 Replace media controls (Play, Stop, Pause) with IconButton
- [x] 7.5 Replace expander headers with IconTextBlock
- [x] 7.6 Update code-behind summary lines with IconKey parameter
- [x] 7.7 Update code-behind step/progress titles

## 8. DataWizard — Data Disc / ISO Build

- [x] 8.1 Replace 3 step cards with IconTextBlock at IconSize=64
- [x] 8.2 Replace section headers with IconTextBlock
- [x] 8.3 Replace action buttons with IconButton
- [x] 8.4 Replace expander headers with IconTextBlock
- [x] 8.5 Replace hint text with IconTextBlock IconSize=16
- [x] 8.6 Update code-behind summary lines

## 9. GameWizard — Game Discs

- [x] 9.1 Replace 2 step cards with IconTextBlock at IconSize=64
- [x] 9.2 Replace section headers with IconTextBlock
- [x] 9.3 Replace action buttons with IconButton
- [x] 9.4 Replace gaming patches expander and warning with IconTextBlock
- [x] 9.5 Update code-behind summary lines with console detection icons
- [x] 9.6 Update code-behind encryption/decryption indicators

## 10. VideoWizard — DVD/BD/Video Discs

- [x] 10.1 Replace 8 disc type step cards with IconTextBlock at IconSize=64
- [x] 10.2 Replace section headers across 8 tabs with IconTextBlock
- [x] 10.3 Replace Browse buttons with IconButton
- [x] 10.4 Replace media controls (Play, Stop) with IconButton
- [x] 10.5 Replace action buttons (Add, Remove, Start, Cancel, Done) with IconButton
- [x] 10.6 Replace checkbox labels with IconTextBlock at IconSize=16
- [x] 10.7 Replace expander headers with IconTextBlock
- [x] 10.8 Update code-behind summary lines with video-specific icons

## 11. WriteView — Burn Images

- [x] 11.1 Replace action buttons (Burn, Cancel, Clear, AddFolder, Remove, Refresh) with IconButton
- [x] 11.2 Replace section headers with IconTextBlock
- [x] 11.3 Replace expander headers (Advanced, Gaming Patches) with IconTextBlock
- [x] 11.4 Replace hint, encryption, and warning text with IconTextBlock at IconSize=16

## 12. ReadView — Copy Disc to Image

- [x] 12.1 Replace action buttons (Start Copy, Cancel, Clear, Refresh, Detect Disc) with IconButton
- [x] 12.2 Replace section headers and expanders with IconTextBlock

## 13. BuildView — Build ISO from Files

- [x] 13.1 Replace action buttons (Build Image, Cancel, Clear, Add Track, Remove) with IconButton
- [x] 13.2 Replace section headers and expanders with IconTextBlock

## 14. VerifyView — Disc Verification

- [x] 14.1 Replace action buttons (Start Verification, Cancel, Clear, Refresh, Copy) with IconButton
- [x] 14.2 Replace hint text with IconTextBlock at IconSize=16

## 15. BlankWizardView + CopyWizardView

- [x] 15.1 Replace BlankWizard header, refresh, info text with icons
- [x] 15.2 Replace CopyWizard header, refresh, browse buttons with icons

## 16. HelpView — Help Documentation (bulk)

- [x] 16.1 Replace 18 ToC buttons with IconButton at IconSize=16
- [x] 16.2 Replace 18 section headers with IconTextBlock at IconSize=50
- [x] 16.3 Replace 9 info card headers with IconTextBlock IconKey="Lightbulb" at IconSize=16
- [x] 16.4 Replace header badges (CONTENTS, Help & Documentation) with IconTextBlock
- [x] 16.5 Replace inline emoji in paragraph text with [OK]/[FAIL] text

## 17. Polish & Review

- [x] 17.1 Build project and fix any compilation errors
- [x] 17.2 Run application and verify each view visually
- [x] 17.3 Check button sizes, spacing, and alignment across all views
- [x] 17.4 Verify navigation and click handlers still work
- [x] 17.5 Verify wizard summary lines render correctly (both with and without IconKey)
- [x] 17.6 Verify DiscoverView media type icon updates on media change
- [x] 17.7 Commit and create PR
