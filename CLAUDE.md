# VibeSwarm - Claude Rules

## CSS Architecture: Utility-First (Bootstrap 6 + Single-Property)

The UI is built on **Bootstrap 6.0.0-alpha.1**, served whole from `src/VibeSwarm.Client/wwwroot/lib/bootstrap` with no npm or build step. Components compose small utility classes in markup rather than monolithic custom CSS classes. The full v6 docs for agents are at `https://getbootstrap.com/llms-full.txt`; check them before assuming a v5 class still exists.

Bootstrap 6 names things differently from v5:
- **Responsive and state variants are prefixes:** `md:d-none`, `lg:col-6`, `sm:flex-row`, `hover:underline-70`. Breakpoints are sm 576, md 768, **lg 1024**, xl 1280, 2xl 1536. Media queries in `site.css` use the same values in range syntax: `@media (width >= 1024px)`.
- **Colour comes from theme classes:** `btn-solid theme-primary` (not `btn-primary`), `btn-outline theme-danger`, `badge theme-success`, `badge badge-subtle theme-warning`, `alert theme-info`, `menu-item theme-danger`. Text colour is `fg-*` (`fg-1` second lines, `fg-2` quieter, `fg-danger`); surfaces are `bg-1` to `bg-4` and `bg-subtle-*`.
- **Components:** dialogs are native `<dialog class="dialog">` through `<ModalDialog>`; menus are a `data-bs-toggle="menu"` toggle with a sibling `<div class="menu">` (placement via `data-bs-placement`, no `.dropdown` wrapper); switches are `.switch` and checkboxes `.check`, laid out in a `.form-field`; accordions are `<details class="accordion-item">` through `<AccordionSection>`; `<select>` takes `form-control`.
- **JavaScript is an ES module** with no `window.bootstrap`. Blazor reaches Bootstrap's API through `wwwroot/js/bootstrap-interop.js`, imported with `IJSRuntime`.

### What goes in markup (Bootstrap utility classes)
- **All layout**: `d-flex`, `flex-column`, `flex-grow-1`, `align-items-center`, `justify-content-center`, `gap-*`
- **All spacing**: `p-*`, `m-*`, `px-*`, `py-*`, `mt-*`, `mb-*`
- **All sizing**: `w-100`, `h-100`, `max-w-100`, `min-w-0`, `min-h-0`, `min-vh-100`
- **All overflow**: `overflow-hidden`, `overflow-auto`, `overflow-x-hidden`, `overflow-y-auto`
- **All position**: `position-fixed`, `position-relative`, `position-absolute`, `top-0`, `start-0`, `end-0`, `bottom-0`
- **All typography**: `fw-semibold`, `fw-medium`, `small`, `text-truncate`, `text-break`, `text-decoration-none`
- **All display**: `d-none`, `d-block`, `d-flex`, `lg:d-none`, `sm:d-block`
- **All borders**: `border`, `border-top`, `border-bottom`, `border-end`, `rounded`
- **All colors**: `fg-primary`, `fg-1`, `fg-2`, `bg-1`, `bg-2`, `bg-subtle-success`
- **Flex control**: `flex-shrink-0`, `flex-shrink-1`, `flex-wrap`, `flex-nowrap`
- **Pointer events**: `pe-none`, `pe-auto`
- **Object fit**: `object-fit-contain`
- **User select**: `user-select-none`

### What goes in site.css (custom CSS only)
- **Design tokens** (section 1): the app palette written onto Bootstrap's `--bs-*` tokens as `light-dark()` pairs, plus `--vs-*` layout variables
- **`calc()` with CSS variables** (safe area padding, header heights, sidebar widths)
- **Hover / active / focus states** (`:hover`, `.active`, `:focus`)
- **Transitions and animations** (`transition`, `@keyframes`)
- **Pseudo-elements** (`::before`, `::after`)
- **Specialized colors** not in the palette (syntax highlighting, terminal)
- **Complex or contextual selectors** (`.parent .child`, `:has()`)
- **Media query responsive overrides**
- **Properties Bootstrap lacks** (see utility classes below)

Bootstrap 6 uses cascade layers (`colors, config, root, reboot, layout, content, forms, components, custom, helpers, utilities`) and its utilities carry no `!important`. `site.css` keeps its `:root` tokens unlayered (Bootstrap's own root tokens are unlayered), puts single-property utilities in `@layer utilities`, and everything else in `@layer custom`, so utilities in markup still win over component styles. A rule in `custom` beats any Bootstrap component rule whatever its specificity, so scope overrides carefully.

### Single-property utility classes (Section 2 of site.css)
When Bootstrap doesn't have a utility, add a single-property class to Section 2 of site.css:
- `.cursor-pointer` → `cursor: pointer`
- `.overscroll-contain` → `overscroll-behavior: contain`
- `.white-space-pre-wrap` → `white-space: pre-wrap`
- `.inset-0` → `inset: 0`
- `.touch-manipulation` → `touch-action: manipulation`

### Rules
1. **Before adding ANY custom CSS**, check if Bootstrap 6 has a utility class, token or component for it
2. **Every CSS class should be minimal** — only the properties Bootstrap can't express
3. **Compose in markup**: prefer `class="d-flex align-items-center gap-3 p-5 border-bottom"` over a named class
4. **Menu placement**: set `data-bs-placement` on the toggle (`bottom-end`, `top-end`, …) when the menu sits near a clipped container edge
5. **Mobile overflow**: test on iPhone SE (375px). No horizontal scroll on dialogs/forms/pages
6. **No hardcoded widths** in flex children unless paired with `flex-shrink` / `min-width: 0`
7. **v6 components are flex or grid containers**: `.alert`, `.card-body` and `.card-header` lay their children out as flex items. Wrap mixed text and inline tags in one element, and add `flex-row` to a card header or body meant as a row

## Design Tokens

Every page draws on one set of tokens, defined in section 1 of `site.css`. Don't add a
`font-family`, a `font-size` value, or a spacing value outside them.

### Fonts
- One system stack for all text, headings included: `--bs-body-font-family`. Code and logs use `--bs-font-mono`.
- Never set `font-family` anywhere else. Reference the variable instead.

### Type scale (five sizes)
| Token | Size | How markup gets it |
|---|---|---|
| `--vs-text-title` | 28px | `<h1>` (page titles), `.text-title` for a headline number |
| `--vs-text-heading` | 17px | `<h2>`–`<h5>`, dialog and card titles |
| `--vs-text-body` | 16px | default text, list rows, `<h6>` |
| `--vs-text-small` | 14px | `.small`: second lines, meta, subtitles |
| `--vs-text-caption` | 12px | `.text-eyebrow` section labels, `.text-caption`, badges |

- Pick a size with the element or class above. `fs-*` (`fs-lg`, `fs-2xl`, …) is only for icon glyphs and spinners, which Bootstrap 6 sizes in `em`.
- Pick the heading level that fits the outline; its size is already set, so don't add size classes.

### Spacing and layout
Use Bootstrap 6's spacer scale only, and from it only these steps: `1` = 4px, `3` = 8px, `5` = 16px, `7` = 24px, `12` = 48px. (v6 renumbered the scale: its `2` is 6px and `4` is 12px; the app doesn't use those.) Pages share one rhythm:
- **Page:** a `d-flex flex-column gap-7` stack, or `<PageHeader>`, which leaves the same `mb-7` below the title.
- **Title row:** `d-flex align-items-center gap-3` holding `<h1 class="mb-0 me-auto">` and `<TitleAction>` round buttons, with the primary action last. When there are more than two actions, put them behind one `+` menu (Skills does this).
- **Section:** `d-flex flex-column gap-3` holding a `<SectionHeader>` (an eyebrow label with quiet `btn-link` actions on the right) above a `list-group rounded-9`. Separate groups (Active, then Paused) instead of filter tabs.
- **Rows:** `<ListRow>` with an icon, a title and one quiet second line. It opens in place (`ChildContent`), links (`Href`) or acts as a button (`OnClick`). `AttentionLine` replaces the second line in amber while something needs doing, and `Trailing` holds short status text such as Off or Running. Open panels show a short fact list, then switches, then actions: secondary buttons first, with Delete last as a quiet `fg-danger` link that confirms.
