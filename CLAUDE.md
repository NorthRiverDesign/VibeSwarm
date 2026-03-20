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
