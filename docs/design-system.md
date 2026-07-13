# Design system

Winora uses native WinUI 3 and Fluent resources first. The shell uses Mica, `NavigationView`, and a 240 px expanded pane; transient depth may use Acrylic. Custom templates are limited to cases where native controls cannot express the approved interaction.

## Tokens

- Spacing: 4, 8, 12, 16, 24, 32, 40, and 48 px.
- Radius: 4 px controls, 8 px cards, and 12 px large surfaces or flyouts.
- Icons: Microsoft Fluent System Icons Regular, 20 px geometry; 32 px card and 36 px feature containers.
- Typography: Segoe UI Variable with Segoe UI fallback.
- Window: minimum 1040 x 720; content scrolls without clipping actions.

The system accent remains authoritative for focus and accessibility. Winora green is reserved for the single primary action in a region and positive safety status. Avoid decorative gradients, neon glow, excessive shadows, crowded metrics, emoji, mixed icon families, and controls that imply behavior they do not provide.

## Interaction and accessibility

Every visible interactive element executes an in-memory action, navigates, produces a dry run, invokes a confirmed supported operation, opens an official Windows surface, or reports a clear Guided/Unsupported/In development result.

Keyboard access, visible focus, Narrator names, High Contrast, 200% scaling, WCAG AA contrast, minimum hit targets, and reduced motion are release requirements. Motion uses native transitions, 160–220 ms durations, and respects `UISettings.AnimationsEnabled`. All user-facing strings live in `.resw`; Russian is the initial locale.
