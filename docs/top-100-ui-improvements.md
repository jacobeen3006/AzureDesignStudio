# Azure Design Studio — Round 2: Top 100 UI/UX Improvements

> **Date:** 2026-10-20 (revised after repository audit; original 2026-10-13) · **Scope:** visual design language, responsiveness, motion, transparency, 3D, the tenant forest, the cost sidebar, the design surface, and contextual docs help.
> **Round 1** (`docs/top-100-improvements.md`) = *what the app does*. **Round 2** = *how it looks, moves, and teaches*. Items here are numbered U1–U100 so they never collide with round 1's 1–100.
> **Host truth:** the Desktop host is **MAUI BlazorWebView on Windows only** (`net10.0-windows10.0.19041.0`, WebView2/Chromium, unpackaged WinUI 3). There is no Photino host. The same Razor UI also ships as a **hosted Blazor WASM** app; that host has no `Discovery`/`Workspace` references, no local `workspace.db`, no tenant-scan drawer, and no live actuals. Capabilities reach the shared UI through `AdsHostFeatures`; surfaces that need local truth are Desktop-only.

**Surface/host matrix**

| Surface | Desktop (MAUI/WebView2) | Browser (WASM) |
|---|---|---|
| Design canvas, stencils, export, validation | yes | yes |
| Tenant forest, scans, topology, exposure, reachability, changes | yes (local workspace + Discovery) | no |
| Live tenant cost/actuals, live-plane refresh | yes | no (server estimate only) |
| Tenant scan drawer (10 tabs) | yes | no |
| OS Mica, custom title bar, per-monitor window state | WinUI interop only | n/a |

---

## 0. Audited baseline (2026-10-20)

The catalog below is not a blank slate. Rebase these before scheduling work:

- **Canvas primitives already exist:** `AdsContext.CreateDiagramOptions` sets an `OrthogonalRouter`, `StraightPathGenerator`, and a 10 px snap grid; `SnappingDragBehavior` snaps drags; `DiagramPanel` draws alignment guides and exposes undo/redo over `DiagramHistory`; `DiagramNavigator` is an existing 200×140 minimap.
- **Group frames exist but library behavior is off:** `options.Groups.Enabled = false`. U62 must re-enable and validate the 3.x fork before any UI work.
- **State is single-document:** `AdsContext` owns one `AdsDiagram` and one `CurrentDesignName`; workspace tabs (U16) need per-tab state or route reload.
- **Browse is snapshot-based and capped:** `TenantScanDrawerTemplate.LoadBrowseAsync` loads a completed scan from SQLite into memory and the list renders `Filtered().Take(200)`. There is no live ARG browsing, no lazy prefetch, and no 100k-row path yet.
- **Desktop truth is isolated:** the tenant drawer, Discovery engines, token broker, workspace, and live actuals live in the Desktop assembly; the browser host registers an empty `AdsHostFeatures`.
- **Accounts are single-tenant in practice:** `MsalAccountService` uses the first cached account, and `ScanRecord` stores a scope but no tenant identity. Multi-tenant is a data-model project.
- **Cost is server/gRPC per node:** `CostService.EstimateAsync` awaits a server call per node (with `price-snapshot.json` fallback). A 200 ms rail requires local computation from the workspace `CostCacheEntry`.
- **Theme is a stylesheet swap:** AntDesign 1.6.2 has no `ConfigProvider.Theme`; it ships `ant-design-blazor.variable.css`, `.dark.css`, and `.compact.css`. Both `wwwroot/index.html` and `Desktop/wwwroot/app.html` currently link only the light CSS and duplicate a hardcoded `#36A4FF` body gradient.
- **Accessibility blockers:** both hosts set `maximum-scale=1.0, user-scalable=no` (fails WCAG 2.2 SC 1.4.4); nodes and the sider toggle use `div`/`title` handlers without keyboard semantics.
- **Style surface is small enough to gate:** ~43 hex colors and ~126 px values in 9 app CSS files, plus 67 inline `style=` attributes in Razor. Vendored `prism.css` and AntDesign/Z.Blazor.Diagrams assets must be excluded from any hardcode gate.
- **Doc links:** all 24 registry URLs return HTTP 200 on 2026-10-20.
- **Stencils:** `ads-stencils.json` contains 49 entries, not ~33.
- **Cold start:** the current shell copy is "Loading. It may take a minute or two." Any <1.5 s budget needs AOT/trim/bundle work, not just measurement.

**Decisions locked by this revision**

1. The three-zone shell is **Desktop-first**; WASM keeps the designer and degrades the Desktop-only regions cleanly.
2. The forest is **snapshot-first**. Live ARG browsing is a separate later mode, not an assumption baked into virtualization.
3. The cost rail computes **locally** from workspace cache; server/Retail calls refresh.
4. 2k nodes / 5k edges at 60 fps is a **gate**, not an assumption.
5. Theming uses AntDesign's **variable + dark CSS** layers mapped to `--ads-*`, never a fork of component internals.

---

## 0.1 The honest constraint (read this before the 100)

Three claims need to be true for this app to *feel* expensive, and only two of them are free:

1. **Typography, spacing, and restraint** — this is 70% of the "expensive" feeling. Free, no dependencies, immediately visible. Costs nothing but discipline.
2. **Motion that earns its place** — 150–250 ms, transform/opacity only, staggered, interruptible. Cheap in any web stack, easy to do badly.
3. **Real native glass and real 3D** — **not free and partly not available.** WebView2 cannot request OS Mica/Acrylic through CSS. You get in-app glass (`backdrop-filter`), which reads as glassmorphism but is not Windows 11 Mica. True Mica requires the *native host window* to use Windows App SDK composition (WinUI 3), and MAUI has no first-party Mica/acrylic path; the app's real option is in-app `backdrop-filter`, not OS glass. And 60 fps 3D at 10,000 nodes is not achievable — 3D must be an *overlay mode* for exploration, not the editor.

So: build in-app glass + depth cues + 2D-perfect canvas, treat OS-level Mica and the isometric 3D view as **phase 3 polish with defined fallbacks**, and let type/spacing/motion carry the "premium" verdict. Apps that look expensive are consistent, not effect-laden.

---

## 1. The design language (concrete, not adjectives)

Ship these as tokens in **one** JSON file → CSS custom properties + C# constants. No hardcoded hex or px in app-authored `.razor`/`.css`/`.html` (U1); exclude vendored `prism.css`, AntDesign/Z.Blazor.Diagrams assets, and generated icon SVG. Map AntDesign's shipped `--ant-*` variables into the `--ads-*` set rather than restyling component internals; load `ant-design-blazor.variable.css` as the base and swap `ant-design-blazor.dark.css` for dark mode. Both HTML entry points (`wwwroot/index.html`, `Desktop/wwwroot/app.html`) must carry the same token/theme links.

**Type ramp** (Fluent 2 scale, Segoe UI Variable with `font-feature-settings: "ss01"`, tabular numerals for all metrics)

| Token | Size/Line | Use |
|---|---|---|
| `caption1` | 12/16 | tree metadata, badges |
| `body1` | 14/20 | default, table cells, tree rows |
| `body1Strong` | 14/20 · 600 | selected row, key metrics |
| `subtitle2` | 16/22 | panel titles |
| `subtitle1` | 20/28 | page titles |
| `title3` | 24/32 | hero panel headers |
| `title1` | 32/40 | the cost number, empty-state headlines |
| `display` | 68/92 | first-run / KPI hero only |

**Spacing** (base-4): 2, 4, 8, 12, 16, 20, 24, 32, 40, 48. **Radius**: 4 (inputs, badges), 6 (buttons), 8 (cards), 12 (panels), 16 (flyouts), 999 (pills). **Density rows**: compact 24 · comfortable 28 · spacious 36.

**Motion tokens** (Fluent 2 durations/curves — use these exact values)

| Token | ms | Curve |
|---|---|---|
| `ultraFast` | 50 | `cubic-bezier(0, 0, 0, 1)` |
| `faster` | 100 | `cubic-bezier(0.33, 0, 0.67, 1)` |
| `fast` | 150 | `cubic-bezier(0.33, 0, 0.67, 1)` |
| `normal` | 200 | `cubic-bezier(0.33, 0, 0.67, 1)` |
| `gentle` | 250 | `cubic-bezier(0.33, 0, 0.67, 1)` |
| `slow` | 300 | `cubic-bezier(0.33, 0, 0.67, 1)` |
| `decelerateMax` | 200 | `cubic-bezier(0.1, 0.9, 0.2, 1)` — entrances |
| `accelerateMax` | 150 | `cubic-bezier(0.9, 0.1, 1, 0.2)` — exits |

**Elevation** (two-layer shadows, dark mode uses lighter surfaces not bigger shadows): `shadow2`, `shadow4`, `shadow8`, `shadow16`, `shadow28`, `shadow64`. **Blur**: 20 (flyouts) / 30 (panels) / 60 (backdrop wash).

**Surfaces**: `surface-0` (app canvas), `surface-1` (panels), `surface-2` (cards, raised rows), `surface-3` (flyouts, menus), `stroke-subtle` 1px hairline, `stroke-focus` 2px accent ring offset 1px. **Brand**: Azure blue `#0078D4` as accent, Fluent brand ramp `#0F6CBD` for interactive text, semantic `success/warning/danger/info` pairs tuned for AA on both themes.

---

## 2. The four flagship surfaces (what you actually asked for)

### A. The Tenant Forest — "perfect tree of all connected tenants' resources"
**Desktop-only and snapshot-first.** The current data source is a completed scan in `workspace.db`; the account model is one cached MSAL account, and `ScanRecord` has no tenant identity. Start with **Tenant → Management group → Subscription → Resource group → Type → Resource** for the signed-in tenant's scanned scopes (management-group hierarchy already comes from `TenantInventory`). Multi-tenant/cross-tenant is a later connection-store project (U54), not a property of the tree component.
A second, non-hierarchical layer of **cross-links** (VNet peerings, private endpoints, identity grants, diagnostic targets, dependencies) comes from the existing `TopologyDeriver` and reachability/exposure engines, surfaced as an edge inspector on selection and highlightable across the whole forest. Selection in the tree is the app's universal cursor: it drives the cost sidebar, the canvas selection, the change plan, and the decommission scope. That cursor needs a shared selection store; `AdsContext` is canvas-only today.

### B. Cost calculator sidebar
Live, debounced (200 ms), always-visible right rail: sticky total in `title1` with tabular numerals, then breakdown by node/RG/tag/owner/region, SKU/tier/quantity toggles, 1-year/3-year/reservation switches, **provenance badge on every figure** (Retail API live · snapshot as-of date · actuals from Cost Management), delta vs current spend, budget threshold with a warning state, and one-click export. Never show a number without saying where it came from and as of when.
**Data path:** compute locally from `workspace.db` `CostCacheEntry` + the shipped `cost-map.json`/`price-snapshot.json`; the server proxy and Retail API only refresh the cache. `CostService.EstimateAsync` currently awaits a server call per node, so the 200 ms/100 ms targets are impossible until the local engine exists. Actuals are **Desktop-only** (`ActualSpendClient` + token broker); WASM shows estimate/snapshot provenance only.

### C. Central design window
Dominant 2D canvas: SVG+HTML hybrid, orthogonal edge routing (bezier toggle), zoom 10–400 % with level-of-detail, alignment guides, grouping frames, undo/redo with a visual history timeline, and the **two-plane overlay** — live resources rendered as a translucent base plane, planned changes on top, with a swipe split-slider to compare. Change types from what-if (Create/Modify/Delete) paint directly onto nodes as halos before anything runs.
**Baseline:** orthogonal routing, 10 px snap, alignment guides, undo/redo, and group frames already exist (grouping is currently disabled at the library level). **Gate:** `Z.Blazor.Diagrams` renders every node as a Blazor `AzureNodeComponent`; 2k nodes / 5k edges at 60 fps is unproven. Benchmark before committing U57/U97/U98; if it fails, LOD must drop to a canvas/WebGL layer rather than component-level culling.

### D. Contextual info icons → Microsoft Learn
A 14 px help glyph on every property, metric, validation error, cost line, and policy finding. Hover (300 ms intent) / focus / click opens an `InfoTip` flyout: one-line definition, 2–3 bullets of "why it matters", **"Learn more ↗"** deep link, plus "copy link" and "docs verified as of <date>". Links live in a single JSON registry so they can be bulk-re-verified in CI.

---

## 3. The 100

Tags **[Design] [UX] [Perf] [A11y] [WinUI] [Fix]** · Effort **S** ≤1d · **M** 2–5d · **L** ≥1w · Impact **P0/P1/P2** (original catalog labels; the authoritative tiering is §5)

### S1 · Design system & theming (U1–U10)

U1. **[Design][M][P0] Token file as the single source of truth.** `design-tokens.json` → CSS custom properties (`--ads-*`) + generated C# constants; the build fails on any hardcoded hex/px in **app-authored** `.razor`/`.css`/`.html` (including inline `style=` attributes). Exclude vendored `prism.css`, AntDesign/Z.Blazor.Diagrams styles, and generated icon SVG. Baseline: ~43 hex + ~126 px in 9 app CSS files, 67 inline styles in Razor. Without this, a "design system" is a folder of lies.
U2. **[Design][M][P0] Light / dark / follow-system.** Persisted per user; both themes AA-verified per token pair (not spot-checked). Dark mode uses lighter *surfaces* rather than larger shadows — the most common "cheap-looking dark mode" mistake. AntDesign 1.6.2 has **no `ConfigProvider.Theme`**: load `ant-design-blazor.variable.css` as the base, swap the `ant-design-blazor.dark.css` link, and map `--ant-*` → `--ads-*`. Both `wwwroot/index.html` and `Desktop/wwwroot/app.html` currently link only the light CSS and duplicate a hardcoded `#36A4FF` body gradient; fix both.
U3. **[Design][S][P0] Replace AntDesign's default look.** Keep the controls, strip the identity: restyle inputs, tables, tabs, dialogs to the token set. Use the shipped `variable.css` `--ant-*` layer as the base and override only the selectors variables do not cover; do not fork component internals. Shipping a recognizable component-library default look is the single fastest way to look like an internal tool.
U4. **[Design][S][P1] Density modes.** Compact / comfortable / spacious row heights (24/28/36) applied globally from one setting — power users on 10k-node trees will not forgive a comfortable-only UI. AntDesign ships `ant-design-blazor.compact.css`; wire it as the compact base instead of reimplementing row heights.
U5. **[Design][S][P1] Accent customization.** User-selectable accent (Azure blue default, plus gold/green/purple/pink), propagated to focus rings, selection, and chart series.
U6. **[Design][M][P1] Elevation and layering discipline.** Every floating surface on a defined elevation step; a "z-index registry" so no surface ever fights another — a top source of jank in dense Blazor apps.
U7. **[Design][S][P1] Iconography system.** Fluent System Icons + official Azure architecture icons for resources, one consistent 16/20/24 px grid, 1.5 px stroke, plus a generic categorized fallback so unknown types never render blank.
U8. **[Design][S][P2] Illustration & empty-state art.** A small, consistent line-art set (no stock photos) for empty states, onboarding, and scan progress.
U9. **[Design][M][P2] Density- and theme-aware charts.** One chart theme with token-driven palettes, categorical colors that survive both themes and colorblind simulation.
U10. **[WinUI][M][P2] Custom title bar with real app identity.** Per-monitor DPI-aware, drag region, caption buttons aligned to Windows 11 metrics, and a subtle gradient that matches the accent. **Not CSS:** MAUI exposes no first-party title-bar API; this needs `AppWindowTitleBar`/Win32 interop in the Windows head (unpackaged `WindowsPackageType=None`). Gate behind a spike; browser host has no equivalent.

### S2 · App shell, layout & responsiveness (U11–U22)

U11. **[UX][M][P0] Three-zone shell: tree | canvas | cost rail.** Fixed left forest, flexible center canvas, right rail that can collapse to a 44 px strip with a persistent total badge.
U12. **[UX][M][P0] Command palette (Ctrl+K).** Every command, resource type, scope, tenant, and design searchable; recent + fuzzy; replaces the need for a sprawling toolbar. The most reliable way to make a dense app feel fast and modern. Requires extracting the `TopMenu` click handlers into a command registry; tenant/scope commands are Desktop-only until a live-query layer exists.
U13. **[UX][M][P0] Responsive breakpoints.** ≥1600 wide · 1200–1599 standard · 900–1199 compact (rail collapses) · <900 narrow (tree becomes an overlay drawer with a back affordance, cost becomes a bottom sheet). Nothing should ever be unreachable — no horizontal page scroll, ever.
U14. **[UX][S][P0] Splitter polish.** 1 px hairline, 8 px hit target, double-click to reset, snap-to-collapse at 30 %, keyboard-resizable (arrows), cursor change on hover, and layout state persisted per workspace.
U15. **[UX][S][P0] Status bar.** Scan status, resource counts, active scope, selection summary, cost total, drift indicator, online/offline, and the running-operation indicator with cancel. Dense apps feel premium when the bottom edge always answers "what is happening right now?".
U16. **[UX][M][P1] Workspace tabs with state restore.** Multi-design tabs, each remembering tree expansion, camera, filters, and open panels; middle-click close; drag to reorder; unsaved-changes indicator. **Blocker:** `AdsContext.Diagram` and `CurrentDesignName` are single-instance; tabs need per-tab diagram state or route-based reload. Multi-tenant tabs wait for U54.
U17. **[UX][M][P1] Customizable layout presets.** "Analyze", "Design", "Deploy", "FinOps", "Decommission" — each a named panel arrangement, switchable from the palette; user-created presets saved.
U18. **[WinUI][M][P1] Persistent, per-monitor window state.** Bounds, maximized state, and DPI per monitor; restore correctly on multi-DPI setups without a blurry first paint. Requires Win32/`AppWindow` interop in the MAUI Windows head; there is no browser equivalent.
U19. **[UX][M][P1] Touch, pen, and precision. **44 px minimum interactive targets in touch mode, pointer-event-based gestures, pinch-zoom and two-finger pan on the canvas, pen pressure-free but palm-safe input, long-press context menus.
U20. **[UX][S][P1] Keyboard-first navigation.** Full tab order, arrow-key tree traversal, F6 to cycle panes, Esc to dismiss the topmost surface, and a printable shortcut reference (the "expensive app" quiet flex).
U21. **[UX][S][P2] Presentation mode.** One toggle hides chrome and bumps type for demos and screen-sharing — cheap to build, disproportionately impressive in a stakeholder room.
U22. **[UX][M][P0] Onboarding & first-run.** Connect tenant → scope pick → first scan → guided tour of the four surfaces, with a sample tenant available offline for evaluation before touching production credentials.

### S3 · Motion & animation (U23–U32)

U23. **[Design][M][P0] The motion token sheet, enforced.** Only the eight durations and four curves from §1; a lint rule rejects literal durations. Ad-hoc `0.3s ease-in-out` everywhere is what makes an app feel homemade.
U24. **[Design][S][P0] Animate transform and opacity only.** Never width/height/top/left; never `box-shadow` transitions on large surfaces — that's the #1 cause of dropped frames in dense UIs.
U25. **[UX][M][P0] View Transitions for navigation.** Panel/route changes as a 200 ms cross-fade + 8 px translate via the View Transitions API, with a graceful fallback path.
U26. **[UX][S][P0] Staggered list entrances.** 20 ms per item, capped at 8 items, once per surface (not on every re-render); re-renders must never re-animate.
U27. **[UX][S][P0] FLIP for layout changes.** Reordering, filtering, and expanding in the tree animate from their previous positions instead of snapping.
U28. **[UX][S][P1] Micro-interactions with intent.** Button press scale (0.98, 100 ms), toggle knob 150 ms, checkbox check with a 30 ms overshoot, hover elevation +1 on cards at 150 ms. Each one earns its existence; none exceeds 250 ms.
U29. **[UX][M][P1] Canvas motion character.** Nodes use slightly springier easing than chrome (drag inertia, settle on drop, magnetic ports at 12 px), so the workspace feels physical while the shell stays crisp.
U30. **[UX][S][P1] Interruptible, cancellable animation.** Any animation can be reversed mid-flight by user input; gestures win over animation, always.
U31. **[UX][S][P0] Reduced motion, honoured properly.** `prefers-reduced-motion` plus an explicit in-app setting that shortens durations to ≤50 ms and replaces movement with opacity — and *doesn't* kill informative progress indication.
U32. **[UX][M][P1] Long-operation theatre, not spinners.** Scan/deploy/what-if run through a staged progress surface with real stage names, per-stage durations, cancellation, and an ETA learned from previous runs.

### S4 · Depth, transparency & 3D (U33–U44)

U33. **[Design][M][P0] In-app glass, done correctly.** `backdrop-filter: blur(30px) saturate(140%)` on panels/flyouts over an app-painted backdrop layer, with `@supports` fallback to solid `surface-1` and an opaque theme option. Under WebView2 this is real and looks premium — unlike pretending you have Mica.
U34. **[Perf][M][P0] Blur budget.** Maximum 3 concurrently blurred layers, blur disabled during canvas drag/zoom and during layout animations, `will-change` applied only for the duration of an animation. Unbounded `backdrop-filter` will cost you 60 fps and 20 % battery.
U35. **[Design][S][P0] Depth hierarchy without 3D.** Three surface levels, hairline strokes, one gradient wash behind the canvas, and elevation-on-hover — 3D-flavoured depth that costs nothing.
U36. **[Design][S][P1] Parallax that informs.** The backdrop grid, group frames, and node layer move at subtly different rates during pan (~2 % differential) so nested frames read as *containing* their contents. Decorative parallax is a gimmick; containment parallax is a feature.
U37. **[Design][M][P1] Tilt-and-light cards.** `perspective(800px) rotateX/Y ≤ 4°` on hover for KPI cards and resource tiles, with a light sweep following the pointer. Cheap, GPU-only, and a strong "expensive" signal when restrained.
U38. **[Design][L][P1] Isometric tenant view (WebGL2).** Orbit/zoom exploration of the forest as extruded layers (region plane → subscription slab → RG cluster), used as a *read-only overview mode*. Explicitly not the editor.
U39. **[Design][L][P1] WebGL↔Blazor data bridge.** JS module interop passing `Float32Array` instanced buffers, never per-frame JSON; picking via an ID buffer; context-loss recovery that degrades to the 2D graph rather than a blank canvas.
U40. **[Design][M][P2] Animated data flows.** Particle or dash-flow animation along edges to show traffic/private-endpoint paths, with a global "show flows" switch and speed tied to real metric direction when available.
U41. **[Design][M][P2] Time as a third axis.** In the 3D overview, z = snapshot age, so drift and growth are visible as depth — the one 3D idea that's genuinely worth building.
U42. **[Design][L][P1] Exploded-layer design view.** In the canvas, an optional `rotateX(35°)` projection that separates VNet › subnet › NIC › VM into stacked planes with drag-to-reorder. 3D used for a real comprehension problem, not decoration. **Not a CSS transform on the existing canvas:** `Z.Blazor.Diagrams` renders one 2D plane; this needs a separate stacked-plane renderer.
U43. **[Perf][M][P0] Performance mode.** Auto-detects (battery, low-end GPU, >5k nodes) and disables blur, parallax, tilt, particles, and 3D in one switch that the user can also force from settings.
U44. **[Design][M][P2] Export the wow.** Any 3D or animated view exports to PNG/SVG/MP4-style loop for decks, so the visuals earn their keep outside the app. MP4 needs `MediaRecorder`/WebCodecs plus a muxer; PNG/SVG can reuse the existing `dom-to-image-more` path.

### S5 · The Tenant Forest / connections (U45–U56)

U45. **[UX][L][P0] Virtualized forest (snapshot-first).** Flattened node index + windowed rendering so 100k nodes scroll at 60 fps; sticky ancestors, collapse-all, and remembered expansion per scope. **Current limit:** browse loads a completed scan into memory and renders `Filtered().Take(200)`; add a paged/streaming read API and a flat virtualized tree (not AntDesign `Tree`) before claiming 100k. Live ARG browsing is a separate mode.
U46. **[UX][M][P0] Hierarchy and edge layers, cleanly separated.** The tree shows containment; a "Connections" inspector shows everything non-hierarchical (peerings, private endpoints, identity grants, diagnostic sinks, dependencies) for the selected node with jump-to-target and highlight-all-instances.
U47. **[UX][M][P0] Connection highlighting on hover.** Hovering a subtree dims unrelated nodes and brightens every inbound/outbound link across the forest — the fastest way a human understands a blast radius.
U48. **[UX][M][P0] Contextual row content.** Each row: type icon, name, region, SKU/tier, trend badge, cost, drift badge, compliance badge, tags — all column-toggleable, all from the same query layer, with tri-state checkboxes for multi-select.
U49. **[UX][S][P0] Search that keeps ancestors.** Type-ahead across the whole forest (name, type, tag, resource ID, IP, private-endpoint target) that shows matches with their ancestor path instead of a flat list, plus a "filter, don't hide" mode that keeps the shape of the tree.
U50. **[UX][M][P1] Saved scopes as first-class objects.** Any tree state (tenant/MG/sub/RG/type/tag/region combination) saves as a named scope reusable as a design source, cost scope, deploy target, or decommission scope.
U51. **[UX][M][P1] Child loading, per data mode.** Snapshot mode: children are already local, so expand instantly from the loaded index — no prefetch. Live mode only: RG children load on expand; hovering a collapsed node for 150 ms prefetches its children so expansion feels instant on a throttled tenant.
U52. **[UX][S][P0] Perf-honest loading states.** Snapshot mode: show scan as-of time, the manifest's partial-failure list, and loaded/total counts. Live mode: skeleton rows for loading subtrees, "N of M loaded" for paged scopes, and a visible "results may be partial — throttled, retrying" state. Never render a partial snapshot as an all-clear.
U53. **[UX][M][P1] Drag from tree into canvas.** Drag a live resource (or a whole RG) onto the design surface to import it into a design, with a confirm step showing the generated Bicep preview before it lands (bridges to round 1's item 8).
U54. **[UX][L][P2] Cross-tenant search.** One query across every connected tenant, with results grouped by tenant and a tenant badge on every row — the multi-tenant story has to be visible, not just supported. **Blocked on a connection store:** desktop currently uses the first cached MSAL account (`FirstOrDefault`) and `ScanRecord` carries no tenant identity. Add multi-account/tenant identity first; this is a data-model project, not a search box.
U55. **[UX][S][P1] Tree density + column layouts.** Compact/comfortable per user, columns resizable and hideable, column set saved per workspace preset (U17).
U56. **[UX][M][P2] Overview strip above the tree.** Tiny KPI row (resources, subscriptions, monthly spend, open findings, drift count) that turns the left pane from navigation into a dashboard — Hava-grade summary without a second screen.

### S6 · The central design surface (U57–U68)

U57. **[UX][L][P0] Level-of-detail canvas.** ≤40 % zoom renders icon+status only, 40–70 % adds name, >70 % adds SKU/region/cost chips; label collision avoidance and priority-based culling keep the canvas readable at every zoom. **Gate:** benchmark 2k nodes / 5k edges at 60 fps first; each node is a Blazor `AzureNodeComponent`, so if the gate fails, LOD must drop to a canvas/WebGL layer instead of component-level culling.
U58. **[UX][M][P0] Edge routing that behaves.** Orthogonal by default with rounded corners, bezier toggle, obstacle avoidance around group frames, edge bundling above ~40 parallel links, and hover-to-isolate a single connection. **Baseline:** `AdsContext.CreateDiagramOptions` already sets `OrthogonalRouter` + `StraightPathGenerator`; extend those, do not replace them.
U59. **[UX][S][P0] Alignment guides, smart spacing, snap-to-grid.** Visible baseline guides when edges align, equal-spacing hints between siblings, grid at 8 px with a visual toggle — the difference between a diagram and a *designed* diagram. **Baseline:** guide lines (`DiagramPanel`) and snap (`SnappingDragBehavior`, 10 px) already exist; add equal-spacing hints and make grid size a token — 8 px is the target, migrate from 10 px.
U60. **[UX][M][P0] Two-plane overlay with swipe compare.** Live plane + planned plane, individually fadeable, with a split slider that wipes between them; ghost nodes for resources to be deleted and dashed outlines for planned additions. **Engine exists:** `LivePlaneMerger` already merges live facts into the design and drives the desktop refresh path; the fade/swipe UI is new.
U61. **[UX][M][P0] Change halos on canvas.** Paint what-if results directly on nodes: Create (green, dashed→solid entrance), Modify (amber, pulsing border with a property-change count badge), Delete (red ghost with a strikethrough label), NoChange (dimmed), NoEffect (dot badge). Reviewing a change should never require reading a table.
U62. **[UX][M][P1] Grouping frames as real containers.** Resize handles, drag-in/drag-out membership, nested frames with correct clipping, frame-scoped visibility toggles, and a frame-level cost/compliance rollup header. **Blocker:** `AdsContext.CreateDiagramOptions` sets `options.Groups.Enabled = false`; re-enable and validate the 3.x fork's group behavior before UI work.
U63. **[UX][M][P1] Selection toolbox on canvas.** Multi-select lasso, shift/ctrl semantics, align/distribute, group/ungroup, duplicate-with-connections, copy/paste with auto-offset, and "select all of type" / "select all in subnet".
U64. **[UX][M][P1] Visual history timeline.** Scrubbable timeline of design + live changes, with a diff halo per step so you can scrub to any point and see what differed. **Baseline:** undo/redo and `Ctrl+Z`/`Ctrl+Y` already run over `DiagramHistory`; this item is the scrubbable timeline/diff face, not the command stack.
U65. **[UX][S][P1] Minimap with viewport and heat overlay.** Cost heat, compliance heat, or drift heat behind the viewport rectangle — a navigation aid and an insight surface at once. **Baseline:** `DiagramNavigator` is already a 200×140 minimap with viewport box, node/group rects, and drag-to-pan; add heat overlays and richer navigation.
U66. **[UX][S][P1] Inline validation on the canvas.** Invalid or blocked nodes get a badge with the specific rule and a fix action; hovering explains it, clicking opens the info flyout with the doc link (U79).
U67. **[UX][M][P2] Comments and annotations.** Pins on nodes/frames with threads, resolved state, and export into the review pack — brings design review into the tool instead of into screenshots.
U68. **[UX][M][P2] Template gallery with live previews.** Animated thumbnails of reference architectures (hub-spoke, AKS baseline, App Service, data platform) that render the real design before insert.

### S7 · Cost calculator & FinOps UI (U69–U78)

U69. **[UX][L][P0] Live cost rail.** Debounced 200 ms recompute on any design/selection change, sticky `title1` total with tabular numerals, and a sub-100 ms perceived update (optimistic total + recompute indicator). **Data path:** `CostService.EstimateAsync` currently awaits a server gRPC/HTTP call per node (snapshot fallback); build a local estimator over `workspace.db` `CostCacheEntry` + `cost-map.json` and use the server/Retail API only to refresh the cache. Desktop-only for actuals.
U70. **[UX][M][P0] Provenance on every number.** Source badge per figure — *Retail API (live)*, *snapshot (as of 2026-09-30)*, *actuals (Cost Management)* — plus region, currency, and a warning state when a snapshot is stale. Never a bare number. **Host split:** `SpendProvenance`/actuals are Desktop-only (Discovery + token broker); WASM shows estimate/snapshot provenance, never a fabricated actuals badge.
U71. **[UX][M][P0] Breakdown control.** Group by node, RG, tag, owner, region, or service family; drill-down with a breadcrumb; virtualized table for thousands of lines. **Baseline:** `CostEstimateResult.Breakdown` groups by service label only; node/RG/tag/owner/region need new aggregation in the local cost engine.
U72. **[UX][M][P1] What-if switches.** Quantity/tier/SKU/region changes computed against the current design with the delta shown inline (green/red), plus a "compare A vs B" mode for two design variants side by side.
U73. **[UX][S][P1] Commitment modelling.** Pay-as-you-go vs 1-year vs 3-year reservation vs savings-plan toggles per resource where applicable, showing the break-even point, not just the discount.
U74. **[UX][S][P1] Budget slider with threshold states.** Set monthly budget; the total shifts to warning/danger as it crosses; a burn-rate projection line for the current month.
U75. **[UX][M][P1] Design-vs-actual comparison.** Estimated vs real spend on the same graph, with the biggest divergences listed and linked to the resources causing them (ties to round 1's cost-attribution item).
U76. **[UX][S][P1] Sparkline + composition chart.** 30-day trend per scope with a stacked composition bar; SVG, token-themed, no chart library dependency required.
U77. **[UX][S][P0] Number discipline.** Locale-aware formatting, currency symbols, compact k/M/B, `font-variant-numeric: tabular-nums`, fixed-width slots so totals don't jitter during recompute, and `aria-live="polite"` throttled announcements. **Baseline:** `CostEstimator.FormatMoney` exists; add locale/compact variants, tabular nums, and the live-region throttling.
U78. **[UX][S][P2] Export & share the calculation.** XLSX/CSV/Markdown export, copy-as-table, and a link back to the exact design state that produced the number.

### S8 · Contextual help: info icons & docs links (U79–U87)

U79. **[UX][M][P0] `InfoTip` component.** 14 px glyph (subtle stroke, stronger on hover/focus), accessible name `Help: <topic>`, opens on 300 ms hover intent / focus / click, dismisses on Esc or blur, positions itself to stay on-screen, and never traps focus. Contents: one-line definition, ≤3 bullets of why-it-matters, **Learn more ↗**, copy-link, and "verified as of".
U80. **[UX][M][P0] Doc-link registry with CI link-rot testing.** One JSON file: `key → {title, url, anchor?, verified:YYYY-MM-DD}`. A CI job HEAD-checks every URL and fails on non-200, so a link can never silently rot. All 24 URLs in §4 were re-verified 200 on 2026-10-20.
U81. **[UX][M][P0] Coverage map, not scattered icons.** Info icons on: every property row, every validation error (linking the exact rule), every cost line (linking the pricing model), every policy finding (linking the definition), every SKU/tier choice, every deployment-stack setting (linking stack semantics), every metric in the tree. Coverage is a checklist, not a vibe.
U82. **[UX][S][P0] Open docs out-of-app, deliberately.** System browser (or an in-app browser pane the user can opt into), with the app's theme/region/version context appended where Learn supports it (e.g. query terms), and a "copy link" fallback for air-gapped users.
U83. **[UX][S][P1] Offline doc cache for the essentials.** Ship concise local summaries + links for the ~40 most-referenced concepts so help works with no network; mark cached content with its retrieval date.
U84. **[UX][S][P1] First-class "?" surface.** In-app help panel with the concept index, the full doc registry as a browsable table, keyboard shortcut reference, and per-surface quick tours — plus the ability to search within it.
U85. **[UX][S][P2] Guided fix flows.** A validation error or policy finding offers "how to fix" steps with the doc link and a button that applies the fix on the canvas where it's mechanical (e.g. enable zone redundancy, switch to private endpoint).
U86. **[UX][S][P2] Doc-freshness surfacing.** Registry entries older than 180 days show as `review_due`; a weekly job re-verifies and reports the diff, so the help system ages well instead of dying quietly.
U87. **[UX][S][P2] Learn-from-usage.** Opt-in counters on which help topics are opened most (never content or tenant data) so doc coverage follows real confusion.

### S9 · Feedback, states & data display (U88–U95)

U88. **[UX][S][P0] Skeleton-first loading everywhere.** Shimmer matched to final layout (no reflow), replaced by content at 150 ms; spinners only for indeterminate sub-second waits.
U89. **[UX][S][P0] Error surfaces that carry the fix.** Every error shows what happened, which scope/API it came from, whether it's retryable, and a one-click retry; ARM errors get parsed into code + message + suggested action instead of a raw JSON dump.
U90. **[UX][M][P1] Toast + notification centre.** Bottom-right glass toasts (stacked max 3, 5 s auto-dismiss, hover to hold), backed by a persistent notification centre with filters; destructive results never auto-dismiss.
U91. **[UX][S][P1] Destructive-action confirmation as a ritual.** Typed resource name for high-blast-radius operations, an explicit diff preview, and a distinct confirm style — with the safety test in round 1's item 99 asserting no delete call is issued without an approved plan hash.
U92. **[UX][M][P1] Virtualized tables with real affordances.** Sticky header, column pinning/resizing/reordering, saved column sets, row grouping, inline expand, keyboard navigation, and 60 fps at 10k rows.
U93. **[UX][S][P1] Empty states that teach.** Each empty surface names what belongs there and offers the one action that fills it (scope picker for the tree, template for the canvas, connect-tenant for the cost rail).
U94. **[UX][S][P2] Session restore without surprises.** Reopen exactly the workspace, then show a dismissible "what changed since your last session" summary (drift count, new findings, cost delta) — an expensive-app habit that costs one small query.
U95. **[UX][S][P2] Sound and haptics off by default, available for completion events.** Off unless enabled; subtle, short, never on error.

### S10 · Accessibility, performance & design quality (U96–U100)

U96. **[A11y][M][P0] WCAG 2.2 AA as a build gate.** axe-core in CI, contrast assertions on token pairs, visible focus rings never removed, 3:1 non-text contrast for icons/borders, no colour-only encoding (always pair with icon/shape/label), full keyboard reachability on every new surface. **Fix first:** both hosts set `maximum-scale=1.0, user-scalable=no` (fails SC 1.4.4); replace with a zoomable viewport. Nodes and the sider toggle also need real keyboard semantics instead of `div`/`title` handlers.
U97. **[Perf][M][P0] Rendering discipline for 10k+ nodes.** `content-visibility: auto`, `contain: layout paint`, `IntersectionObserver` for offscreen work, incremental tree patches on scan updates (never a full re-render), `offscreenCanvas` for charts, and a bundle-size budget with the 3D chunk lazy-loaded. **Likely bottleneck:** Blazor diffing, not CSS layout — every node is an `AzureNodeComponent`. Pair with the U57 gate; CSS containment alone will not buy 60 fps at 2k nodes.
U98. **[Perf][S][P0] Perceived-performance budget, measured.** Host-specific: Desktop WebView2 cold start to interactive <1.5 s; WASM cold start measured after AOT/trim/bundle work (the current shell warns "may take a minute or two"). Tree scroll 60 fps at 10k nodes · canvas 60 fps at 2k nodes / 5k edges · cost recompute <100 ms · help flyout <50 ms. Every canvas/tree/cost number is gated by the U57/U45/U69 spikes; measured in CI, not asserted in a README.
U99. **[Design][M][P1] Component gallery + visual regression.** A "kitchen sink" page rendering every component in every state (hover/focus/disabled/loading/error/empty × light/dark × 3 densities), with screenshot diffing per breakpoint and a max-diff threshold in CI. Reuse the existing WebView2 CDP E2E harness + seeded offline workspace (`AdsHostAuth`); do not stand up a second Playwright stack that has to fight Entra auth.
U100. **[Design][S][P0] Screenshots in every PR.** Before/after captures from the app itself for any UI change, committed to `docs/ui/` and attached to the review — plus a "hero shots" set regenerated per release. Design quality survives only where it's visible in review. Reuse the existing `dom-to-image-more` export and CDP screenshot paths; add a small capture script rather than a new harness.

---

## 4. Doc-link registry (re-verified 2026-10-20 — all 24 HTTP 200)

| Info-icon topic | URL |
|---|---|
| Well-Architected Framework | https://learn.microsoft.com/en-us/azure/well-architected/ |
| WAF (classic entry) | https://learn.microsoft.com/en-us/azure/architecture/framework/ |
| Resource naming | https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-naming |
| Naming abbreviations | https://learn.microsoft.com/en-us/azure/cloud-adoption-framework/ready/azure-best-practices/resource-abbreviations |
| Official Azure icons | https://learn.microsoft.com/en-us/azure/architecture/icons/ |
| Azure Resource Graph | https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview |
| Azure Policy | https://learn.microsoft.com/en-us/azure/governance/policy/overview |
| Bicep | https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/overview |
| Deployment stacks | https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deployment-stacks |
| What-if | https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-what-if |
| Cost analysis | https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/quick-acm-cost-analysis |
| Retail Prices API | https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices |
| Optimization recommendations | https://learn.microsoft.com/en-us/azure/cost-management-billing/costs/tutorial-acm-opt-recommendations |
| Azure Advisor | https://learn.microsoft.com/en-us/azure/advisor/advisor-overview |
| Defender for Cloud | https://learn.microsoft.com/en-us/azure/defender-for-cloud/defender-for-cloud-introduction |
| Reference architectures | https://learn.microsoft.com/en-us/azure/architecture/browse/ |
| Azure Monitor | https://learn.microsoft.com/en-us/azure/azure-monitor/overview |
| Azure MCP Server | https://learn.microsoft.com/en-us/azure/developer/azure-mcp-server/overview |
| Acrylic (design) | https://learn.microsoft.com/en-us/windows/apps/design/style/acrylic |
| Mica (design) | https://learn.microsoft.com/en-us/windows/apps/design/style/mica |
| BlazorWebView (MAUI host) | https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/blazorwebview |
| WebView2 | https://learn.microsoft.com/en-us/microsoft-edge/webview2/ |
| Fluent 2 design system | https://fluent2.microsoft.design/ |
| Azure Pricing Calculator | https://azure.microsoft.com/en-us/pricing/calculator/ |

Per-*resource-type* links (the long tail: one per stencil — `ads-stencils.json` has **49** entries today, not ~33) belong in the same registry and get verified by the same CI job — expand the table mechanically rather than writing URLs by hand.

---

## 5. Sequencing (revised 2026-10-20)

**Capacity reality:** the inline `P0/P1/P2` tags are the original catalog's impact labels; they are **not** a schedule (44 P0s is not executable). The tier table below is authoritative. After re-tagging U10, U18, U42, U44, and U54, the catalog is 42 S / 51 M / 7 L — **≈244 dev-days lower bound** at the plan's own S=1/M=3/L=7 scale, before integration, QA, auth/CI work, and the Phase 0 spikes.

### Tier table (authoritative)

| Tier | Items | Count | Why |
|---|---|---|---|
| **M0 — gate** | U1, U2, U3, U11, U12, U13, U45, U57, U69, U79, U80, U96, U97, U100 | 14 | No shell/canvas/cost delivery starts until tokens, host seam, a11y, the forest/canvas/cost spikes, and the doc registry exist. U45/U57/U69/U97 are **gate/spike** here, not full delivery. |
| **Must** | U4, U14, U15, U22, U23, U24, U25, U26, U27, U31, U33, U34, U35, U43, U46, U47, U48, U49, U58, U59, U70, U71, U77, U81, U88, U89, U93 | 27 | The visible overhaul: shell, forest, canvas, cost, help, states. |
| **Should** | U5, U6, U7, U16, U17, U18, U19, U20, U28, U29, U30, U32, U50, U51, U52, U53, U60, U61, U64, U65, U72, U73, U74, U75, U76, U82, U83, U84, U90, U91, U92, U94, U98 | 33 | Depth and power features; schedule after the Must set is stable. |
| **Later** | U8, U9, U10, U21, U36, U37, U38, U39, U40, U41, U42, U44, U54, U55, U56, U62, U63, U66, U67, U68, U78, U85, U86, U87, U95, U99 | 26 | Wow/3D/multi-tenant/native-title-bar; explicitly not on the critical path. |

M0 items are **decided and spiked first**; their full delivery lands in the phase named below (U45/U57/U69/U97 are explicitly spikes, not finished UI in Phase 0).

### Implementation status (2026-10-20)

First visible slice landed in the shared shell (Desktop host verified end-to-end):

| Item | Status | What shipped |
|---|---|---|
| U1 tokens | **partial** | `ads-tokens.css` with `--ads-*` light/dark surfaces, type/spacing/radius/shadow/motion tokens; JSON source file and build gate still pending |
| U2 light/dark/system | **done** | persisted mode, `prefers-color-scheme`, AntDesign `dark.css` stylesheet swap, no flash bootstrap |
| U3 de-AntDesign | **partial** | header, menus, tabs, inputs, drawer/modal surfaces, canvas, minimap and node tiles restyled via tokens |
| U11 three-zone shell | **partial** | stencil rail · canvas · inspector rail; the forest and live cost rail are still pending |
| U12 command palette | **done (first cut)** | Ctrl+K/header trigger, fuzzy filter, keyboard nav, wired to save/export/validate/cost/compare/import/names/guardrails/user/scan |
| U15 status bar | **done (first cut)** | design name, resource/link/frame/selected counts, workspace state, zoom |
| U96 a11y | **partial** | zoomable viewport replaces `user-scalable=no`; sider controls are real buttons; axe gate pending |
| U100 screenshots | **partial** | CDP capture used for verification; scripted per-PR capture still pending |

**Verified:** palette opens via Ctrl+K and button, filters and executes `Validate` (drawer: “No issues found”), theme toggles dark→light with the AntDesign link swap, inspector collapses 300↔44 px, stencil drop updates counts and minimap, node selection fills the inspector, zero console exceptions. **Not yet:** tenant forest, live cost rail, U45/U57/U69/U97 performance spikes, doc registry, virtualized tables, 3D.

**Phase 0 — gate (M0).** Tokens + AntDesign `variable.css`/`dark.css` swap in both HTML hosts; shell state store + `AdsHostFeatures` seam; viewport/a11y fix; doc registry + link CI; baseline screenshot capture (U100); three spikes — 2k-node canvas, 100k-row forest read path, local cost compute. **Exit:** spike numbers written down; the browser host still renders and hides Desktop-only regions.

**Phase 1 — shell + truth.** Three-zone shell, command palette, rehome the 10 scan-drawer surfaces, virtualized forest (full U45–U49), local cost rail (full U69–U71, U77), InfoTip + coverage (U79–U81), splitter/status bar (U14–U15), onboarding (U22), skeleton/error/empty states (U88–U89, U93).

**Phase 2 — canvas + motion + glass.** LOD/routing/guides (U57–U59, extending the existing router/guides), motion tokens/View Transitions/reduced motion (U23–U27, U28–U30, U31, U32), glass + blur budget + performance mode (U33–U35, U43), density/accent/elevation/icon polish (U4–U7), rendering discipline (full U97).

**Phase 3 — depth.** Two-plane swipe + change halos (U60–U61), virtualized tables (U92), tabs/presets/window state (U16–U18), saved scopes + child loading + drag-to-canvas (U50–U53), touch/keyboard (U19–U20), commitment/budget/design-vs-actual/charts (U72–U76), help depth (U82–U84), notification centre + destructive ritual + session restore (U90–U91, U94), measured budgets (U98).

**Phase 4 — wow.** 3D/parallax/tilt (U36–U42), frames/timeline/minimap heat/comments/template gallery (U62–U68), cross-tenant search (U54), tree density/overview strip (U55–U56), native title bar (U10), illustration/export/sound (U8–U9, U44, U78, U95), visual regression (U99), onboarding-only extras (U21).

The order still follows the original principle: type, spacing, motion restraint, density, and provenance-honest numbers first; 3D and glass land twice as hard once those are consistent.

---

## 6. Where round 2 meets round 1

| Round 1 capability | Round 2 surface that expresses it |
|---|---|
| Tenant inventory + Resource Graph spine | The Tenant Forest (U45–U52, U56) |
| Relationship derivation / reachability | Connection highlighting + edge inspector (U46–U47) |
| What-if preflight + change plans | Change halos + two-plane overlay (U60–U61), destructive ritual (U91) |
| Deployment stacks | Info icons on stack settings (U81) + plan review surfaces |
| Drift detection | Timeline scrubber (U64), heat overlay (U65), session summary (U94) |
| Cost estimation & actuals | The whole cost rail (U69–U78) |
| Decommission planner | Tree-scoped selection (U50, U53) + confirmation ritual (U91) |
| Docs-linked help | InfoTip + registry + CI link-rot test (U79–U87) |

**Host split:** the forest, live-plane overlays, cost actuals, deployment stacks, and decommission surfaces are **Desktop-only** until a server workspace API exists. The browser host keeps the designer, cost estimates/snapshot provenance, and docs help. Any shell work that cannot degrade cleanly on WASM fails the Phase 0 exit.
