# VibeSwarm - Claude Rules

## CSS Architecture: Utility-First (Bootstrap + Single-Property)

This project follows a utility-first CSS approach similar to Tailwind. Components compose multiple small utility classes in markup rather than using monolithic custom CSS classes.

### What goes in markup (Bootstrap utility classes)
- **All layout**: `d-flex`, `flex-column`, `flex-grow-1`, `align-items-center`, `justify-content-center`, `gap-*`
- **All spacing**: `p-*`, `m-*`, `px-*`, `py-*`, `mt-*`, `mb-*`
- **All sizing**: `w-100`, `h-100`, `mw-100`, `min-vh-100`
- **All overflow**: `overflow-hidden`, `overflow-auto`, `overflow-x-hidden`, `overflow-y-auto`
- **All position**: `position-fixed`, `position-relative`, `position-absolute`, `top-0`, `start-0`, `end-0`, `bottom-0`
- **All typography**: `fw-semibold`, `fw-medium`, `fs-*`, `small`, `text-truncate`, `text-break`, `text-decoration-none`
- **All display**: `d-none`, `d-block`, `d-flex`, `d-lg-none`, `d-sm-block`
- **All borders**: `border`, `border-top`, `border-bottom`, `border-end`, `rounded`
- **All colors**: `text-primary`, `text-secondary`, `bg-body-secondary`, `bg-body-tertiary`
- **Flex control**: `flex-shrink-0`, `flex-shrink-1`, `flex-wrap`, `flex-nowrap`
- **Pointer events**: `pe-none`, `pe-auto`
- **Object fit**: `object-fit-contain`
- **User select**: `user-select-none`

### What goes in site.css (custom CSS only)
- **CSS custom properties** (`:root` variables for safe areas, layout dimensions, accent colors)
- **`calc()` with CSS variables** (safe area padding, header heights, sidebar widths)
- **Hover / active / focus states** (`:hover`, `.active`, `:focus`)
- **Transitions and animations** (`transition`, `@keyframes`)
- **Pseudo-elements** (`::before`, `::after`)
- **Specialized colors** not in Bootstrap's palette (accent colors, syntax highlighting, terminal)
- **Complex or contextual selectors** (`.parent .child`, `:has()`)
- **Media query responsive overrides**
- **Properties Bootstrap lacks** (see utility classes below)

### Single-property utility classes (Section 2 of site.css)
When Bootstrap doesn't have a utility, add a single-property class to Section 2 of site.css:
- `.cursor-pointer` → `cursor: pointer`
- `.min-width-0` → `min-width: 0`
- `.overscroll-contain` → `overscroll-behavior: contain`
- `.white-space-pre-wrap` → `white-space: pre-wrap`
- `.inset-0` → `inset: 0`
- `.touch-manipulation` → `touch-action: manipulation`

### Rules
1. **Before adding ANY custom CSS**, check if Bootstrap 5.3.8 has a utility class for it
2. **Every CSS class should be minimal** — only the properties Bootstrap can't express
3. **Compose in markup**: prefer `class="d-flex align-items-center gap-2 p-3 border-bottom"` over a named class
4. **Dropdown direction**: use `dropup`, `dropstart`, or `dropend` when near a clipped container edge
5. **Mobile overflow**: test on iPhone SE (375px). No horizontal scroll on modals/forms/pages
6. **No hardcoded widths** in flex children unless paired with `flex-shrink` / `min-width: 0`

## Design Tokens

Every page draws on one set of tokens, defined in section 1 of `site.css`. Don't add a
`font-family`, a `font-size` value, or a spacing value outside them.

### Fonts
- One system stack for all text, headings included: `--bs-font-sans-serif`. Code and logs use `--bs-font-monospace`.
- Never set `font-family` anywhere else. Reference the variable instead.

### Type scale (five sizes)
| Token | Size | How markup gets it |
|---|---|---|
| `--vs-text-title` | 28px | `<h1>` (page titles), `.text-title` for a headline number |
| `--vs-text-heading` | 17px | `<h2>`–`<h5>`, modal and card titles |
| `--vs-text-body` | 16px | default text, list rows, `<h6>` |
| `--vs-text-small` | 14px | `.small`: second lines, meta, subtitles |
| `--vs-text-caption` | 12px | `.text-eyebrow` section labels, `.text-caption`, badges |

- Pick a size with the element or class above. `fs-*` is only for icon glyphs.
- Pick the heading level that fits the outline; its size is already set, so don't add size classes.

### Spacing and layout
Use Bootstrap's spacer scale only (`1` = 4px, `2` = 8px, `3` = 16px, `4` = 24px). Pages share one rhythm:
- **Page:** a `d-flex flex-column gap-4` stack, or `<PageHeader>`, which leaves the same `mb-4` below the title.
- **Title row:** `d-flex align-items-center gap-2` holding `<h1 class="mb-0 me-auto">` and `<TitleAction>` round buttons, with the primary action last. When there are more than two actions, put them behind one `+` menu (Skills does this).
- **Section:** `d-flex flex-column gap-2` holding a `<SectionHeader>` (an eyebrow label with quiet `btn-link` actions on the right) above a `list-group rounded-4`. Separate groups (Active, then Paused) instead of filter tabs.
- **Rows:** `<ListRow>` with an icon, a title and one quiet second line. It opens in place (`ChildContent`), links (`Href`) or acts as a button (`OnClick`). `AttentionLine` replaces the second line in amber while something needs doing, and `Trailing` holds short status text such as Off or Running. Open panels show a short fact list, then switches, then actions: secondary buttons first, with Delete last as a quiet `text-danger` link that confirms.
