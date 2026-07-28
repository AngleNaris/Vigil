# SystemAutorun Design System

SystemAutorun is a dense Windows operations tool. Its interface stays quiet,
compact, and optimized for repeated scanning and action rather than decorative
presentation. The native WPF rewrite preserves the existing visual identity.

## Principles

- Dark, neutral work surface with restrained purple interaction accents.
- Square geometry throughout. Cards, buttons, inputs, tables, badges, and
  dialogs use a zero corner radius.
- Information density over oversized headings or decorative cards.
- Status is always expressed with text and color: `OK`, `FAIL`, and `OFF`.
- Cached content renders immediately; refresh and command progress are explicit.
- Disabled, loading, focused, selected, success, warning, and error states are
  visually distinct.

## Three-Layer Tokens

### Primitive

| Token | Value | Purpose |
| --- | --- | --- |
| `Color.Background.950` | `#131116` | Window background |
| `Color.Surface.800` | `#211F27` | Primary card surface |
| `Color.Surface.850` | `#1B191F` | Inset surface and controls |
| `Color.Surface.700` | `#2C2932` | Hover surface |
| `Color.Border.500` | `#4B4652` | Strong border |
| `Color.Border.700` | `#332F39` | Subtle divider |
| `Color.Text.100` | `#E6E0E9` | Primary text |
| `Color.Text.300` | `#C8C0D0` | Secondary text |
| `Color.Text.500` | `#9E90A8` | Hint and off text |
| `Color.Purple.600` | `#58418F` | Primary action |
| `Color.Purple.500` | `#6750A4` | Primary hover |
| `Color.Purple.300` | `#BFABF1` | Focus and accent |
| `Color.Success.400` | `#7EE787` | Healthy state |
| `Color.Error.300` | `#FFB4AB` | Failed and destructive state |
| `Color.Warning.300` | `#FFD166` | Warning state |

Spacing follows a 4 px base scale: `4, 8, 12, 16, 18, 24`. Typography uses
Microsoft YaHei UI with Segoe UI fallback. The base size is 13 px; supporting
sizes are 11, 12, 14, 16, 20, and 24 px. Letter spacing is always zero.

### Semantic

- `WindowBackground` maps to `Color.Background.950`.
- `SurfacePrimary`, `SurfaceInset`, and `SurfaceHover` map to surface primitives.
- `TextPrimary`, `TextSecondary`, and `TextHint` map to text primitives.
- `ActionPrimary`, `ActionPrimaryHover`, and `FocusAccent` map to purple.
- `StatusHealthy`, `StatusFailed`, `StatusDisabled`, and `StatusWarning` map to
  the corresponding state primitives.
- `BorderStrong` and `BorderSubtle` define hierarchy without shadows.

### Component

- Main buttons are at least 38 px high; task action buttons use a 3 px left
  accent border.
- Inputs are at least 32 px high with 10 px horizontal padding.
- Task rows are 34 px high. Headers and cells use compact 8-12 px padding.
- Status badges are at least 64 x 26 px with a 1 px status-colored border.
- Busy indicators are 4 px high and use `FocusAccent`.
- Scrollbars are 8 px wide. Cards use a 1 px border and no shadow.

## Layout Contract

- Default window: 1360 x 880; minimum: 1180 x 760.
- Page margins: 18 px horizontal, 14 px vertical; primary gap: 12 px.
- Horizontal task/detail split starts near 3:2.
- Detail panel width: 420-520 px, preferred 460 px.
- Task columns: status, name, type, distro, reason, and detail.
- Primary task actions remain in a stable two-column grid.

## Interaction Contract

- Render the last valid cache before asynchronous refresh.
- Coalesce duplicate read requests and keep commands serialized per task.
- Auto-refresh is opt-in and runs every 10 seconds.
- Task actions stay disabled until a task is selected.
- Closing the window may minimize to tray; explicit Exit stops only the UI and
  user agent processes owned by SystemAutorun.
- Background launch capability distinguishes no-console, hidden, minimized,
  normal, tray-aware, and Windows-service execution. Hidden GUI launch is
  best-effort unless the target application exposes a supported silent mode.
