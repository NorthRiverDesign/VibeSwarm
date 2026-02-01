# Bootstrap Utility Classes Skill

## Overview

This skill focuses on leveraging Bootstrap's built-in CSS utility classes to create efficient, lean, and maintainable HTML/CSS structures. The goal is to minimize custom CSS by stacking Bootstrap's utility classes, similar to how Tailwind CSS operates. Bootstrap provides a comprehensive set of utilities for spacing, layout, typography, colors, borders, flexbox, display, and more. Always prioritize these over custom classes to avoid bloating the stylesheet with redundant declarations—Bootstrap's CSS is already loaded, so reuse it!

**Core Principle:** Use custom CSS classes _only_ when a specific property or combination cannot be achieved with an existing Bootstrap utility class or a close approximation. Exhaust all Bootstrap options first, and document why custom CSS was necessary if used.

## Key Guidelines

1. **Stack Classes Liberally**  
   Combine multiple utility classes on elements to achieve desired styles.  
   Example: Instead of `.my-padding { padding: 1rem; margin: 0.5rem; }`, use `p-3 m-2`.

2. **Responsive Design**  
   Use responsive prefixes (`sm-`, `md-`, `lg-`, `xl-`, `xxl-`) for breakpoints.  
   Example: `d-none d-md-block` hides on small screens but shows on medium and up.

3. **Avoid Redundancy**  
   If Bootstrap has a utility for it (e.g., padding, margin, text alignment), use it.  
   Do not recreate properties like `display: flex;` with custom CSS when `d-flex` exists.

4. **Fallback to Custom CSS Sparingly**  
   If no utility matches (e.g., very specific animation, non-standard value), add a minimal custom class.  
   Prefer inline styles for one-off cases only if it keeps things leaner—but utilities are almost always better.

5. **Performance Mindset**  
   Stacking utilities reduces CSS file size and leverages Bootstrap's optimized code.  
   This keeps the app efficient and avoids unnecessary overrides of Bootstrap defaults.

## Common Bootstrap Utility Categories and Examples

### Spacing (Padding and Margin)

- Padding: `p-{side}-{size}` (sides: t, b, s, e, x, y; sizes: 0–5, auto)  
  Examples: `p-0`, `p-3`, `pt-lg-4`, `px-2`
- Margin: `m-{side}-{size}`  
  Examples: `m-0`, `mt-1`, `mx-auto`, `mb-md-5`

### Display and Visibility

- Display: `d-{value}` (none, inline, block, flex, grid, table, etc.)  
  Examples: `d-flex`, `d-none d-sm-block`, `d-grid`
- Visibility: `visible`, `invisible`

### Flexbox Utilities

- Container: `d-flex`, `d-inline-flex`
- Direction: `flex-row`, `flex-column`, `flex-row-reverse`
- Justify: `justify-content-{start|end|center|between|around|evenly}`
- Align Items: `align-items-{start|end|center|baseline|stretch}`
- Align Self: `align-self-{start|end|center|baseline|stretch}`
- Grow/Shrink: `flex-grow-0`, `flex-shrink-1`
- Order: `order-{0-5|first|last}`
- Example: `<div class="d-flex justify-content-between align-items-center">`

### Grid System

- `container`, `row`, `col-{breakpoint}-{size}`
- Examples: `col-12 col-md-6`, `col-lg-4`
- Gutters: `g-{size}`, `gx-3`, `gy-0`

### Text and Typography

- Alignment: `text-{start|center|end|justify}`
- Weight: `fw-{light|normal|bold|bolder}`
- Style: `fst-italic`, `text-decoration-none`
- Transform: `text-lowercase`, `text-uppercase`
- Sizes: `fs-{1-6}`, `lead`
- Colors: `text-{primary|secondary|success|danger|warning|info|light|dark|body|muted|white|black-50|white-50}`

### Background and Colors

- Background: `bg-{primary|secondary|success|danger|warning|info|light|dark|body|transparent|white|black}`
- Gradients: `bg-gradient`
- Opacity: `bg-opacity-{10|25|50|75|100}`

### Borders

- Border: `border`, `border-0`, `border-{top|bottom|start|end}`
- Width: `border-{1-5}`
- Color: `border-{primary|secondary|...}`
- Radius: `rounded`, `rounded-{0-5|circle|pill|top|bottom|start|end}`

### Positioning

- Position: `position-{static|relative|absolute|fixed|sticky}`
- Offsets: `top-{0|50|100}`, `start-0`, etc.
- Translate: `translate-middle`, `translate-middle-x`

### Shadows and Effects

- Shadows: `shadow-none`, `shadow-sm`, `shadow`, `shadow-lg`
- Opacity: `opacity-{0|25|50|75|100}`

### Sizing

- Width/Height: `w-{25|50|75|100|auto}`, `h-{25|50|75|100|auto}`
- Max/Min: `mw-100`, `min-vh-100`
- Viewport: `vh-100`, `vw-100`

### Other Helpers

- Overflow: `overflow-auto`, `overflow-hidden`
- Float: `float-{start|end|none}`
- Clearfix: `clearfix`
- Screen Readers: `visually-hidden`
- Stretched Link: `stretched-link`

## Implementation Strategy

1. Analyze the required styles/layout.
2. Map needs to Bootstrap utilities (e.g., padding → `p-*`).
3. Stack classes directly in HTML elements.
4. If a gap exists, check if combining utilities approximates it.
5. Only then, add minimal custom CSS (and document the reason).
6. Test responsiveness and cross-browser consistency using Bootstrap's features.

Follow this skill to keep Bootstrap-based apps efficient, maintainable, and free of unnecessary custom CSS.
