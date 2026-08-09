# Design system

Winora uses native WinUI 3 and Fluent resources first. The shell uses a flat opaque canvas, `NavigationView`, and a 240 px expanded pane; transient depth may use Acrylic. Custom templates are limited to cases where native controls cannot express the approved interaction.

## Tokens

- Spacing: 4, 8, 12, 16, 24, 32, 40, and 48 px.
- Radius: 4 px controls, 14 px cards, 16 px grouped surfaces, 12 px flyouts.
- Icons: Microsoft Fluent System Icons Regular, 20 px geometry; 32 px card and 36 px feature containers.
- Typography: Segoe UI Variable with Segoe UI fallback. Page titles are 46 px Bold at −0.04 em tracking on a 1.0 line height — display type at default tracking is the most reliable sign that nobody chose the typography.
- Window: minimum 1040 x 720; content scrolls without clipping actions.

## Colour

The palette belongs to the user. Two colours are chosen on the **Оформление** screen — a background and an accent — and `Winora.Core.Appearance.SchemeDerivation` produces everything else from them: four text tiers, the sheet, the card, the hovered card, the divider, the stroke, the interaction states, and the colour printed on the accent. Every derived value is overridable; the two-colour default exists because a scheme built from two decisions cannot become unreadable and one built from twelve can.

Four accents ship ready-made — white, violet, red, graphite — each in a dark and a light form. The default is white on near-black, and priority between two buttons is carried by fill rather than hue: filled primary, outlined secondary, bare tertiary.

Surfaces step *toward the ink* — lighter in a dark scheme, darker in a light one. That is the opposite of the usual elevation rule and is chosen because it always has headroom, where stepping toward white collapses the sheet, card and hovered card together the moment a canvas near white is picked.

**Contrast is measured, not judged, and now at runtime.** Text is held to 4.5:1 against the hovered card — the worst surface any scheme can produce — and a scheme that fails cannot be applied, nor loaded back from disk. The accent is held to 3:1 as a non-text mark per WCAG 1.4.11; below that the editor warns and still allows it, because a quiet accent is a taste and unreadable text is not. `PaletteContrastTests` pins the literals in `Palette.xaml` to what the derivation produces for the default preset, so the startup colours cannot drift from the runtime ones.

High Contrast overrides all of it. While Windows is in that theme `ThemeBrushService` paints nothing and the whole `HighContrast` dictionary defers to the system: those colours are an accessibility setting the user chose, not a preference Winora may outvote.

Avoid decorative gradients, neon glow, excessive shadows, crowded metrics, emoji, mixed icon families, and controls that imply behavior they do not provide.

## Interaction and accessibility

Every visible interactive element executes an in-memory action, navigates, produces a dry run, invokes a confirmed supported operation, opens an official Windows surface, or reports a clear Guided/Unsupported/In development result.

Keyboard access, visible focus, Narrator names, High Contrast, 200% scaling, WCAG AA contrast, minimum hit targets, and reduced motion are release requirements. Motion uses native transitions, 160–220 ms durations, and respects `UISettings.AnimationsEnabled`. All user-facing strings live in `.resw`; Russian is the initial locale.
