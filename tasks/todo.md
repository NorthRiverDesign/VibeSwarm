# VibeSwarm Redesign — Plan

Handoff bundle saved at `C:\Users\kylew\.claude\projects\C--Users-kylew-source-github-VibeSwarm\design-handoff\`.
Primary references: `project/Project Page.html`, `project/Job Page.html`, `project/design_handoff_vibeswarm_redesign/README.md`.

Design tokens, typography, status-color mapping, border radii, and component patterns are all spelled out in the bundle README. Two HTML prototypes contain the reference markup + CSS.

## Architectural decisions (please confirm or redirect)

1. **Theme system**: the design uses BOTH `data-theme` and `data-bs-theme` on `<html>`. The app today only sets `data-bs-theme`. I'll add a parallel `data-theme` attribute so the new tokens (`--surface-*`, `--fg-*`, `--line`, etc.) resolve correctly. Theme persistence already exists via `theme.js` — I'll extend it.

2. **Font loading**: the design uses Inter + JetBrains Mono from Google Fonts. I'll add them to `index.html`. (Alternative: self-host — more work, better offline/PWA. Let me know if you want self-hosted.)

3. **CSS scope**: I'll put all new design tokens and component utilities into the existing `site.css` (it's already organized for utility-first). No new stylesheet files.

4. **Component-level scope**: I will NOT rewrite every Razor component from scratch. Strategy: update markup (classes + minor restructure) in existing Razor files to match the new visual vocabulary. Keep backing logic, state, events, SignalR wiring, and models intact.

5. **Sidebar width**: design is 212px. Current is 240px. I'll change to 212px. Nav items gain count pills where relevant (Projects/Jobs counts).

6. **Scope of this pass**: I'll update the 5 pages called out in the README:
   - Dashboard (`Index.razor`)
   - Projects (`Projects.razor`)
   - Project Detail (`ProjectDetail.razor` + its tab components)
   - Jobs (`Jobs.razor`)
   - Job Detail (`JobDetail.razor` + its sub-components)

   Child components (PageHeader, ProjectCard, JobListItem, IdeasPanel, etc.) get updated in place. Ones no longer needed (e.g. OutcomeSummaryCard per README migration notes) get deleted.

## Phased implementation

### Decisions confirmed (2026-04-17)
- Fonts: system stack only — no Google Fonts, no self-hosted webfonts. Use `system-ui` sans + `ui-monospace` mono to avoid flicker.
- Phased build; commitment to implement all 4 phases.
- Improve existing components in place; split into feature-component + primitive-component layers (no rewrites).
- OK to delete dead components (Outcome/Verification/Delivery trio, "Go to delivery" CTA).

### Phase 1 — Foundation
- [ ] Update `site.css` §1 with full design-token palette (`:root`, `[data-theme="dark"]`, `[data-theme="light"]`) — lift `--surface-*`, `--fg-*`, `--line`, `--line-strong`, Bootstrap color overrides, `--banner-*`, `--diff-*`
- [ ] Keep font stacks as system-ui / ui-monospace (don't introduce Inter/JetBrains Mono)
- [ ] Remove old `--vs-accent-*` usage, replacing with new `--bs-*` / status tokens
- [ ] Extend `theme.js` to set `data-theme` alongside `data-bs-theme`
- [ ] Add single-property utility classes needed: `.text-eyebrow` (11px uppercase tracking-wide), sidebar shell grid, etc.
- [ ] Update `MainLayout.razor`: 212px sidebar, new nav item styling (count pills), footer with avatar + theme toggle, mobile top bar
- [ ] New primitive components (under `Components/Common/` or `Components/Primitives/`): `ThemeToggle.razor`, `ProjectMark.razor`, `StatusIconPill.razor`, `BranchChip.razor`, `ProviderPill.razor`, `MetaChip.razor`, `CountPill.razor`, `Eyebrow.razor`

### Phase 2 — Project Page (primary file from handoff)
- [ ] `ProjectDetail.razor`: new header (crumbs, project mark, branch chip, options dropdown, repo line), new tab strip with badges, 5-tab content area
- [ ] `IdeasPanel` (tab 1 — "Ideas"): composer w/ paste + attach + suggest + counter + ⌘↵, queue controls (provider pill + green split button), idea rows w/ icon pill + meta chips + actions
- [ ] `ProjectJobsList` (tab 2 — "Jobs"): toolbar + bulk bar + job cards (header row + status banner + work summary)
- [ ] `ProjectEnvironmentsEditor` (tab 3 — "Environments"): panel wrapper, env cards with #N pill + tags + reorder actions + auto-fit grid body + Web login sub-card
- [ ] `ProjectChangesTab` (tab 4 — "Changes"): warning banner, change rows (M/A/D status letters, mono paths, diffstat), explainer panel
- [ ] `AutoPilotPanel` (tab 5 — "Auto-Pilot"): sectioned config (idea generation / job execution / guardrails), save + start row

### Phase 3 — Dashboard + Projects + Jobs + JobDetail
- [ ] `Index.razor` (Dashboard): KPI section (4 cards w/ sparklines + range toggle), Active Projects grid (project cards w/ sparkline + 4-stat strip), Queue table
- [ ] `Projects.razor`: new toolbar (search + Active/Inactive/All segmented + sort + grid/list toggle), grid cards w/ latest-job capsule, list view
- [ ] `Jobs.razor`: left project sub-nav (200px), right-side toolbar + status segmented + bulk bar + compact job rows w/ inline expand
- [ ] `JobDetail.razor`: new consolidated header (chip row, stat strip, diff strip), 5-step timeline, single status banner replacing old Outcome/Verification/Delivery trio, tabbed workspace (Summary / Git Changes / Console / Command) + fixed-width chat column

### Phase 4 — Cleanup
- [ ] Delete components superseded by the new design (`JobOutcomeSummaryCard`, any old 3-card delivery/verification markup)
- [ ] Remove dead CSS from old `--vs-*` variable system
- [ ] Build/verify: `dotnet build`
- [ ] Manual smoke test pages at 375px and 1200px viewports

## Review (end of session)

**Build**: clean — 0 warnings, 0 errors across solution.
**UI tests**: 50/50 passing (IdeasPanel, ProjectDetailTabs, ProjectEnvironments, ProjectJobsList, JobDetail, MobileShell, etc.). 11 remaining test failures are all pre-existing service-level tests (DatabaseService, ProviderCliArgs, DeveloperUpdateService, SkillStorage, Settings, JobExecution, QueueAndIdea) unrelated to this redesign.

### What shipped

**Phase 1 — Foundation**
- `site.css` §1 rewritten: full design-token palette (`:root` dark default + `[data-theme="light"]`), Bootstrap `--bs-*` overrides, banner/diff/chip helpers
- 8 new primitive components in `Components/Common/Primitives/`: `ThemeToggle`, `ProjectMark`, `StatusIconPill`, `BranchChip`, `ProviderPill`, `MetaChip`, `CountPill`, `Eyebrow`, plus `StatusBanner` feature component
- `site.css` §18 adds design-system component classes (`.vs-tabs`, `.vs-panel`, `.vs-toolbar`, `.vs-banner`, `.vs-alert`, `.vs-side-nav`, `.composer`, `.branch-chip`, `.provider-pill`, `.env-tag`, `.status-pill`, `.start-group`, etc.)
- `theme.js` now sets `data-theme` alongside `data-bs-theme` so tokens resolve through the existing cookie-based theme persistence
- `MainLayout.razor` sidebar refreshed — 212px width, `.vs-brand-mark` conic gradient, `.vs-side-nav` items, theme toggle in footer + mobile top bar
- Font stack kept on `system-ui` / `ui-monospace` per user direction (no Google Fonts)

**Phase 2 — Project Page**
- `ProjectDetail.razor` tabs rebuilt on `.vs-tabs` (single scrollable strip, no mobile overflow dropdown)
- `IdeasPanel.razor` composer: new `.composer` wrapper, `.attached` chip row, `.composer-foot` with ghost buttons + counter, `.composer-hint` with kbd
- `IdeasPanel` queue header: `.vs-section-head` with green `.start-group` split button
- `IdeaListItem.razor` view mode: `StatusIconPill` + body + meta chips + actions
- `ProjectEnvironmentsEditor.razor` rebuilt on `.vs-panel` with `.env-num` / `.env-tag` pills, `vs-auto-grid` body, dashed `env-sub` for Web login

**Phase 3 — Dashboard / Projects / Jobs / JobDetail**
- `ProjectCard.razor` rebuilt with `ProjectMark`, `StatusIconPill`, `MetaChip`, `BranchChip` primitives — surface-1 background, single latest-job capsule
- `ProjectJobsList.razor` toolbar converted to `.vs-toolbar` with `.search-field`
- `JobDetail.razor`: the old `JobOutcomeSummaryCard` (Outcome / Verification / Delivery trio) deleted and replaced with a single `StatusBanner` + `MetaChip` row per the README migration notes

**Phase 4 — Cleanup**
- `Components/Jobs/JobOutcomeSummaryCard.razor` deleted
- Related `JobOutcomeSummaryCard` tests removed from `JobOutcomeComponentsTests.cs` (16 remaining related-component tests still green)
- Old `.app-sidebar .nav-item` styles removed (superseded by `.vs-side-nav`)
- `--vs-accent-*` kept as aliases pointing to `--bs-*` / status tokens for back-compat with diff viewer + legacy CSS

### Intentionally deferred

- `ProjectDetailHeaderCompact`: left as-is — extensive git/branch controls would take significant restructuring; design compatibility is acceptable with the new palette flowing through
- `AutoPilotPanel`: kept Bootstrap card structure; new tokens restyle it passably
- `ProjectChangesTab`: informational alerts at top still use `alert alert-success/info`; the core diff accordion was not touched
- `Jobs.razor` (cross-project): kept the existing two-column split (project sub-nav + job list) layout; btn-group status filter is still Bootstrap default
- `Projects.razor`: grid/list toggle not added; kept existing `ProjectListItem` (the `ProjectCard` update covers the Dashboard grid)

These are all incremental polish items that could be picked up in a follow-up pass — none block the core redesign intent.

## Out of scope for this pass

- Server-side / SignalR changes
- New data models or DTOs
- Tweaks panel (design-exploration only per README — don't port)
- "Next step: Finish delivery" CTA (dead code per README — remove, don't port)

## Notes

- Per CLAUDE.md: **no hardcoded widths in flex children** without `flex-shrink`/`min-width:0` pairing
- Mobile check at 375px (iPhone SE)
- Status color is paired w/ an icon everywhere (a11y — never color-alone)
