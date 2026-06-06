## Context

Open Burning Suite uses ~1,200 emoji characters across 16 `.axaml` views and 4 code-behind files for all visual identity — buttons, section headers, status indicators, help documentation, and action labels. Emoji rendering is OS-dependent (Segoe UI Emoji on Windows, varies on Linux/macOS), producing inconsistent UI across platforms.

A personal 628-icon collection at `marcelofrau/icons8-personal-set` provides 108 stems already mapped to OBS features via `openburning-icons.md`. Icons are available in PNG at multiple sizes (16, 32, 48, 50, 128, 256) and `.ico` format, in 3d-fluency (primary) and fluency (flat 2D fallback) styles.

Current emoji usage patterns in XAML fall into 5 categories:
- **Type A** — `Button.Content="🔥  Text"` (~50 occurrences, highest priority)
- **Type B** — `<TextBlock Text="🔥"/>` as separate element (~30, home screen tiles)
- **Type C** — `<TextBlock Text="🔥  Section Header" Classes="SectionHeader"/>` (~60)
- **Type D** — Action buttons like `Content="➕ Add Tracks"` (~30)
- **Type E** — Bullet list entries in HelpView (~500 but only 3 patterns)

## Goals / Non-Goals

**Goals:**
- Replace all UI-visible emoji in `.axaml` views with PNG icons from personal collection
- Create reusable Avalonia controls to minimize XAML verbosity per replacement
- Embed icons as Avalonia resources for consistent cross-platform rendering
- Support multiple icon sizes per context (50x50 default, 16x16 status/tight, 128x128 hero)
- Preserve exact visual layout — icon + text alignment, spacing, button sizes

**Non-Goals:**
- Replace emoji in runtime log messages (deferred to separate change)
- Replace emoji in code comments (arrows, math symbols — not user-visible)
- Animated icons or sprites
- SVG or vector icon formats — PNG only
- Icon theme switching or dark/light variants
- Changing existing layout, margins, or visual design beyond emoji→icon swap

## Decisions

### Decision 1: Custom controls vs inline XAML

Replacing `Content="🔥  Burn / Write"` with inline `<StackPanel><Image/><TextBlock/></StackPanel>` adds ~5 lines per button × ~50 buttons = ~250 extra XAML lines project-wide.

**Chosen: Two custom controls + one markup extension**

| Control | Purpose | Replaces |
|---------|---------|----------|
| `IconTextBlock` | Icon + text label (horizontal) | Types B, C, E |
| `IconButton` | Button with icon + text | Types A, D |
| `IconSourceExtension` | MarkupExtension resolving key→URI | Inline `<Image>` bindings |

**Alternatives considered:**
- Style-only approach (inject icon via Button template) — rejected because Avalonia styles can't inject different icons per button instance without attached properties, which is equivalent complexity
- Single `IconControl` with mode switch — rejected for violating single responsibility
- Pure converter + inline StackPanel — rejected for XAML verbosity cost

### Decision 2: Control inheritance

| Control | Base Class | Properties |
|---------|-----------|------------|
| `IconTextBlock` | `ContentControl` | `IconKey` (string), `Text` (string), `IconSize` (double, default 50) |
| `IconButton` | `Button` | `IconKey` (string), `Text` (string), `IconSize` (double, default 50), `IconOnly` (bool) |
| `IconSourceExtension` | `MarkupExtension` | Constructor(key), `Size` property |

`IconButton` inherits all Button behavior (Click, Command, Flyout, etc.) — no feature loss.

### Decision 3: Icon size by context

| Context | Size | Rationale |
|---------|------|-----------|
| Navigation sidebar buttons | 50x50 | Matches emoji visual weight at ~16pt text |
| Home screen tiles | 64x64 | Larger presentation tiles |
| Settings sidebar | 16x16 | Narrow panel, tight spacing |
| Action buttons (Add, Browse, etc.) | 50x50 | Same as nav |
| Bullet list / ToC (HelpView) | 16x16 | Inline with text, not dominant |
| Status indicators (loading, warning) | 16x16 | Subtle, inline |
| Hero empty-state (no media) | 128x128 | Centerpiece visual |
| Checkbox labels | 16x16 | Small, inline with checkbox |

### Decision 4: Asset embedding strategy

Icons copied to `Assets/Icons/{size}/` and referenced via `avares://OpenBurningSuite/Assets/Icons/{size}/{stem}.png`.

`.csproj` updated once:
```xml
<ItemGroup>
  <AvaloniaResource Include="Assets\Icons\**\*.png" />
</ItemGroup>
```

MarkupExtension resolves path:
```
IconSource("Burn", 50) → "avares://OpenBurningSuite/Assets/Icons/50/icons8-cd-3d-50.png"
```

### Decision 5: Icon key naming

Keys match `openburning-icons.md` stems but use short PascalCase aliases for XAML readability:

| XAML Key | Stem | Example |
|----------|------|---------|
| `Burn` | `icons8-cd-3d` | `<IconButton IconKey="Burn"/>` |
| `Dvd` | `fluentui-dvd` | `<IconTextBlock IconKey="Dvd"/>` |
| `Game` | `icons8-game-controller` | `<IconTextBlock IconKey="Game"/>` |
| `Audio` | `icons8-music-3d` | `<IconTextBlock IconKey="Audio"/>` |
| `Settings` | `icons8-settings-2d` | `<IconButton IconKey="Settings"/>` |

Full mapping in `IconHelper.cs` as static dictionary.

### Decision 6: Code-behind summary lines

Wizards use `AddSummaryLine("🔥 Task", value)`. Replace with:
```csharp
AddSummaryLine("Task", value, IconKey: "Burn");
```

Add optional `IconKey` parameter to existing `AddSummaryLine` method. The method renders an `IconTextBlock` instead of a plain `TextBlock`. This avoids breaking existing calls while enabling icon support incrementally.

## Risks / Trade-offs

| Risk | Mitigation |
|------|-----------|
| Button sizes shift after icon replacement | Use `IconSize` matching original emoji font-size; test each view after conversion |
| Missing icons for ~15 concepts not in collection | Source from FluentUI Emoji set or create placeholder; document in `missing-icons.md` |
| New controls introduce XAML compilation errors | Prototype controls in test project first; validate with `dotnet build` |
| Regression in wizard summary layout | `AddSummaryLine` overload preserves existing signature — old code unaffected |
| PNG resolution at 50x50 looks pixelated on HiDPI | Icons are from Icons8 3d-fluency style designed for 50px; test on 150%+ scaling |
| Asset size bloat | 50x50 icons: ~13MB total; 16x16: ~8MB; selective 128x128: ~5MB — acceptable for desktop app |
