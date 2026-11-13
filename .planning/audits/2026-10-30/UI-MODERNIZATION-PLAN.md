# UI Modernization Plan — Azure Design Studio

Status: Complete as a proposal; no source changes made and no unapproved design choice is treated as decided.

## Product and constraints

The UI is a dense Azure architecture editor for architects/platform engineers, shared between hosted Blazor WASM and MAUI Blazor Hybrid on Windows (`.planning/PROJECT.md:3-10,65-74`). Keep canvas fidelity, tenant truth, and gated execution as product priorities. Preserve Blazor, AntDesign, Z.Blazor.Diagrams, the Azure-blue brand, Segoe/system font, and the established editor context; the proposal is a modernization of hierarchy, interaction, states, accessibility, responsiveness, and implementation consistency—not a rewrite or a generic dashboard. Source-design evidence is in `src/AzureDesignStudio/wwwroot/css/ads-tokens.css:6-48` and `docs/top-100-ui-improvements.md:59-94`.

Phase boundary: Phase 7 finishes the already-landed shell only and targets three zones, shell state, palette ownership/keyboard, truthful status, theme consistency, docs help, accessibility, and screenshots (`.planning/ROADMAP.md:157-172`). The roadmap explicitly prohibits new UI surfaces before the gate closes in Phase 9 (`ROADMAP.md:95,171`). So first fix/audit the existing shell; plan all new surface work for after Phase 9, with security/data dependencies respected. The 30 work packages below organize modernization implementation; several deliberately complete existing GSD and Round 2 commitments. They are **not** the separately researched 30-new-improvement backlog in `IMPROVEMENTS-30.md`.

## Design contract

- **Three zones:** existing stencil/navigation rail | dominant canvas | inspector rail. Preserve user-resizable and collapsible zones; do not repurpose a shell pass into a tenant forest/cost surface before the relevant product/data gates. Current host markup is `Pages/Index.razor:14-40`; the named persisted zone contract is Phase 7 SHELL-01.
- **Brand and density:** Azure identity, current blue accent, existing font family; dense editor first, with readable metadata and explicit focus. Reuse `--ads-*` semantic tokens rather than CSS literal proliferation. Avoid requiring new font/icon packages.
- **UI primitive contract:** prefer native buttons/inputs/links; one established APG interaction pattern per widget; visible focus, accessible names, keyboard parity, and no focus hidden behind shell overlays. New states communicate meaning in text/icon as well as color.
- **State contract:** surfaces must define idle, loading, success, empty, error, offline/pending, and conflict behavior before implementation. Save/load and deployment actions remain transactional and safety-first; no optimistic UI may claim remote sync before acknowledgement.
- **Motion contract:** functional transitions only, interruptible, reduced-motion aware; no canvas movement or geometry animation required for routine keyboard actions.
- **Theme contract:** light, dark, and follow-system should render consistently in both entry points; preserve pre-paint theme initialization already present (`wwwroot/index.html:9-18`, Desktop `wwwroot/app.html:9-18`). Audit actual contrast rather than infer from token names.
- **Host contract:** browser WASM must not imply Desktop-only workspace, tenant, or live-cost capabilities. Use existing host seams (`AdsHostFeatures` / documented host split in `docs/top-100-ui-improvements.md:5-15,27,343-344`).

## Modernization work packages

These packages combine known commitments, concrete defect repairs, and proposed refinements. They are not asserted to be new relative to Round 2. Sources are guidance, not evidence of implementation or measured conformance.

| Before | After | Why |
|---|---|---|
| Fixed rails, implicit width ownership | Named zones with persisted widths, keyboard separators and compact collapse | Keep the canvas dominant without clipping controls |
| Mixed dialog/listbox semantics | One APG pattern with focus containment/return and active-option announcements | Keyboard and assistive-tech behavior must agree |
| Always-green local status | Persisted local / pending / synced / conflict state plus last acknowledged sync | Do not imply durability or remote success without evidence |
| Clear canvas before remote load | Validate candidate graph, then commit replacement; preserve current graph on failure | Prevent loss of unsaved work |
| Literal component spacing and z-indices | Small semantic spacing/layer/focus token scales over existing Azure palette | Consistency without a framework or brand rewrite |
| Unverified theme/viewport states | Deterministic host × theme × viewport × state fixtures | Make regressions visible before release |

### Interaction integrity and keyboard semantics

1. **Transactional load/replace.** Parse and validate loaded design before mutating the live diagram; preserve current canvas on server/offline/parse failure. Evidence: `TopMenu.razor.cs:475-509` currently clears before fetching; product integrity constraint `PROJECT.md:71,74`.
2. **Explicit unsaved-change boundary.** Track dirty state independent of design name; guard Replace/Open/Import and panel navigation with save, discard, or cancel choices. Test save-in-flight races and double clicks. Supports the “warn on unsaved changes” form rule; add only after behavior and offline contract are settled.
3. **Model offline state as a finite state machine.** Distinguish local-only, pending sync, synced, retrying, and conflict; show last successful sync time and explain the next action. Derive from the pending queue, not `LastCallWasOffline` alone. `StatusBar.razor:8-20` is static; `DesignGrpcService.cs:15-16,63-76,103-129` only describes the most recent call. W3C status messages should be programmatically exposed without forcing focus: https://www.w3.org/WAI/WCAG22/Understanding/status-messages.html.
4. **Make command palette semantics internally consistent.** Choose APG combobox/listbox with active-descendant and option IDs, or dialog with ordinary command buttons; don't mix both. Research: https://www.w3.org/WAI/ARIA/apg/patterns/combobox/; current markup `CommandPalette.razor:24,33-41,147-166`.
5. **Complete dialog focus lifecycle.** Capture invoker; move focus into palette; contain Tab; close with Escape; restore focus after close; prevent background interaction when claiming modal. APG requirements: https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/; current `CommandPalette.razor:10-12,78-90,118-123`.
6. **Owner-scoped command registration.** Register/unregister TopMenu commands by owner; clear JS-to-.NET shortcut references on component disposal; verify palette remount yields exactly one handler. `Program.cs:72`, `TopMenu.razor.cs:22-44`, `AdsCommandService.cs:15-35`, `ads.js:312-325`; Phase 7 SHELL-02 explicitly owns this.

### Canvas workflow and feedback

7. **Undo/redo capability in a persistent command location.** Current undo/redo is inside canvas contextual controls (`DiagramPanel.razor:30-45`); keep it discoverable when selection/context menu disappears, with disabled-state explanation and accessible labels.
8. **History group semantics.** Show meaningful batch labels (“Move 3 resources”, “Add subnet and firewall”) rather than opaque step count; make import/load explicit history resets. The current diagram-history event controls are `DiagramPanel.razor:74-99`; Phase 8 owns component tests (`ROADMAP.md:180-188`).
9. **Canvas keyboard selection and movement mode.** Provide a keyboard equivalent to select, move by grid increments, connect ports, group, and inspect; do not make drag the only way to manipulate the diagram. Research baseline for drag alternatives: WCAG 2.2 SC 2.5.7 https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html; existing planned single-pointer/keyboard gates in `ROADMAP.md:167`.
10. **Coordinate-aware focus navigation.** Add a documented “focus next node/port” route and return path between canvas and inspector; visually distinguish keyboard focus from multi-selection. For hierarchical inventory later, follow APG tree keyboard model: https://www.w3.org/WAI/ARIA/apg/patterns/treeview/.
11. **Inline validation is actionable, not only colored.** Pair issue badge with readable rule and a keyboard-reachable “inspect/fix” action; link to same validation drawer source. Existing validator entry point `TopMenu.razor.cs:220-234`; exact new UI can wait until shell gate and policy UX review.
12. **Action feedback with completion and failure persistence.** Give success/failure a stable textual label and non-color cue; let high-impact action outcomes remain visible until acknowledged. Research: status message requirement https://www.w3.org/WAI/WCAG22/Understanding/status-messages.html; current messages in `TopMenu.razor.cs:462-472` are transient service calls.

### Responsive, focus, and accessibility engineering

13. **Responsive shell uses available viewport, not fixed desktop assumptions.** Define zone collapse/order at compact widths and prevent important controls being clipped by flex overflow; keep the canvas as the intentional two-dimensional exception. WCAG reflow permits essential diagrams/tables to remain 2D but expects other content at 320 CSS px: https://www.w3.org/WAI/WCAG22/Understanding/reflow.html.
14. **Focus-obscuration test for overlays and fixed shell chrome.** Assert keyboard focus remains at least partially visible around header/status bar, drawers, palette, and dialogs; add scroll-padding where needed. WCAG 2.2 AA: https://www.w3.org/WAI/WCAG22/Understanding/focus-not-obscured-minimum.html.
15. **Hit-area budget report, not only CSS box size.** Inventory compact rail, theme, tab, and icon controls; meet 24×24 CSS px or spacing exceptions and verify on touch. Source example `.ads-rail-icon-btn` is 30px (`ads-tokens.css:361-371`), while toggle button `.ads-rail-toggle` is 26px (`:430-444`). WCAG 2.2 SC 2.5.8: https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html.
16. **A single focus-visible token across app and library controls.** Define indicator thickness, offset, and contrasting pairs in both themes; test AntDesign and custom canvas focus. Don't remove input outline without replacement: `ads-tokens.css:614-623` sets palette input `outline:0` with no local focus treatment.
17. **Status announcements coalesce high-frequency counts.** Announce important state changes politely and selectively; don't screen-reader spam on every node selection, zoom event, or scan counter. W3C status message guidance distinguishes dynamic status from focus changes: https://www.w3.org/WAI/WCAG22/Understanding/status-messages.html.
18. **A robust zoom/reflow test matrix.** Test keyboard zoom 200/400%, OS text scaling, Windows high contrast, and 320 CSS px width; keep diagrams scrollable without causing the entire app to require two-axis scrolling. Research: WCAG Reflow https://www.w3.org/WAI/WCAG22/Understanding/reflow.html.

### State coverage and trust cues

19. **Palette has explicit no-query, no-results, no-command, and execution states.** Distinguish a genuinely empty registry from “no matches”; preserve query on temporary execution failure and expose keyboard help. Existing no-match copy is `CommandPalette.razor:25-29`; no separate busy/error state is represented around `RunAsync` `:169-173`.
20. **Loading progress tells the truth about the current operation.** Replace indefinite “may take a minute or two” launch message with an accessible indeterminate shell plus real staged progress only where measured. Entry point text: `wwwroot/index.html:63`, Desktop `app.html:46`; don't promise an ETA without data.
21. **Save feedback names where data landed.** Differentiate “saved on this device”, “queued to sync”, “synced to server”, and “failed”; retain the current local/offline success distinction as base (`TopMenu.razor.cs:462-472`) and unify with status bar.
22. **Conflict resolution is a first-class state before multi-device sync.** When remote/local versions diverge, show timestamps and a non-destructive compare/keep-copy path; block silent last-write-wins. Make this dependent on data/sync design, not a shell-only fake feature.
23. **Operation cancel and safe retry boundaries.** For scan/deploy/what-if, show cancel only when supported and distinguish retryable from final errors. Keep the approved-plan safety model (`PROJECT.md:71`) and avoid retrying a mutating request blindly.
24. **Error copy includes recovery action and stable identity.** Map underlying error codes to concise message, scope, retryability and next action; support stable ADS code requirement in `REQUIREMENTS.md:67-69` and avoid dumping raw provider errors in primary UI.

### Design-system implementation and regression control

25. **Semantic spacing and layer tokens.** Extend the existing color/radius/motion token file with documented spacing, focus ring, z-layer, and density tokens; migrate shell CSS in small stages. Existing tokens cover color/radii/shadows/motion but no spacing/z-index scale (`ads-tokens.css:6-48,576-599`).
26. **State-colored token contrast matrix.** Calculate foreground/background pairs for text, selected, warning, error, link, focus, and diagram edges in light/dark; use APCA as design diagnostic but gate to required WCAG contrast criteria. The existing semantic palette is at `ads-tokens.css:17-29,60-72`; do not infer contrast from hex or names.
27. **Theme change respects system preference lifecycle and storage failure.** Keep the pre-paint bootstrap, verify system preference changes while mode is system, storage denied/private browsing, and user override persistence across WASM/Desktop. Current logic: `ads.js:262-310`, `ThemeToggle.razor:28-52`, both entry HTML bootstraps.
28. **Respect reduced motion in app-authored transitions and loading animation.** Existing palette entrance animation is present (`ads-tokens.css:588-604`), and legacy loading animation has keyframes (`app.css:98-133`); neither inspected stylesheet defined a reduced-motion override. Add `prefers-reduced-motion` behavior and preserve meaningful progress state. UI Craft a11y reference: `references/accessibility.md` (loaded for this audit); W3C prefers reduced motion guidance in WCAG 2.3.3 (AAA) is optional support, but still useful user preference.
29. **Visual regression fixtures cover the editor states, not only landing screenshots.** Seed deterministic local designs for empty canvas, dense graph, invalid node, selected node, palette, modal, save/offline, light/dark; capture both shared WASM and Desktop shell at supported widths. Existing Phase 7 screenshot gate is `ROADMAP.md:167`; Phase 8 owns E2E framework (`:180-188`).
30. **Add an interaction contract per shared component.** For each custom control document name, role, keyboard, focus return, reduced motion, disabled reason, and empty/error behavior; reuse the same fixture in both hosts. This reduces divergence between shared Razor surface and host-specific interop, consistent with `.planning/PROJECT.md:67-70` and `docs/top-100-ui-improvements.md:5-15`.

## Responsive, theme, and state matrix

| Surface/host | Wide desktop | Compact / narrow / zoom | Light / dark / system | Failure/offline states |
|---|---|---|---|---|
| Shared editor shell | Three zones; canvas primary | Collapse/overlay rails; 320px reflow except canvas work area; no clipped controls | Same semantic token map in WASM + WebView2; no first-paint flash | Host-appropriate local/server status; no implied tenant data in WASM |
| Command palette | Centered compact command dialog | Fit viewport height; scroll options; focus remains visible | Dialog and option contrast audited | Empty, no match, executing, action failure; announcement without focus theft |
| Canvas | Dense diagrams with panning/zoom | Preserve 2D workspace; keyboard route for every drag-only task | Node/edge/focus states distinguishable without relying only on hue | Load failure preserves existing model; import/replacement asks if unsaved |
| Inspector/status | Selection details and operation state | Reflow sections; rail collapse control stays reachable | Text and icons pair semantic colors | Saved-local / pending / synced / retrying / conflict / error represented explicitly |
| Desktop-only inventory/cost (future) | Only after dependency gates | Virtualized and keyboard APG semantics when introduced | Provenance/status contrast in both themes | Partial scan/snapshot age/offline visibly distinct; not exposed as live truth in WASM |

## Rollout phases and dependencies

1. **P0 — defect and contract closure (current Phase 7 shell work):** fix transactional load; specify palette ARIA/focus contract; owner-scope command registrations and JS lifetime; make status state derive from real state. Preserve existing component APIs where possible. No new surface. Phase 7's required shell/a11y/theme/docs gates remain authoritative (`ROADMAP.md:157-172`).
2. **P1 — proof infrastructure (Phase 8):** seeded component tests for load preservation, palette keyboard/focus and command remount; WASM E2E for save/load/offline/error, screenshot fixture; axe and keyboard checks. Depend on TEST-02…06 and DIAG requirements (`ROADMAP.md:174-188`). No new UI surface before Phase 9.
3. **P2 — post-gate responsive and state pass:** reflow, focus-visibility, hit area, loading/error/conflict and announcement models for existing shared surfaces. Verify WASM and Desktop separately; user-configurable or new product surfaces require design approval and relevant data/security authority.
4. **P3 — after Phase 9 / trust gate:** expand token spine, add visually new surfaces only when corresponding data engines and scope contracts exist. Tenant forest requires paged query/data model, local cost requires provenance-backed local calculation, collaboration remains out of scope. Phase 10 requirements are the source of truth (`ROADMAP.md:203-217`; `REQUIREMENTS.md:89-115`).
5. **P4 — release hardening:** visual diffs across host/theme/viewport/state matrix; update scripted hero images; verify clean packaged install only under Phase 11 (`ROADMAP.md:219-232`).

## Acceptance and verification gates

- **Behavior:** failed design load cannot mutate current canvas; successful load commits as one replacement; unsaved edits are guarded; undo/redo remain consistent after replace.
- **Keyboard:** every task available without drag; palette Tab loop / Escape / focus return; tree follows APG when added; arrows and selection semantics tested separately.
- **Assistive tech:** accessible names and roles checked; status changes announced once and politely; modal semantics match behavior; focus is not obscured. Axe/WCAG 2.2 AA zero violations as Phase 7 target (`ROADMAP.md:167`), with manual keyboard + screen reader passes since automated scans don't prove usability.
- **Sizing:** automated target-size threshold report for 24x24 CSS px and exceptions, focus indicator visibility, 200/400% zoom and 320px reflow; diagram can remain 2D as an essential workspace (WCAG reflow exception).
- **Theme:** both hosts pass token pair contrast checks; theme preference first paint/no flash, system updates, reload, storage-unavailable state; no unexplained white-on-dark diagram artifacts.
- **State truth:** local/pending/synced/error/conflict labels validated against actual persisted state and sync outcomes; no green/synced implication based solely on last operation.
- **Regression:** component tests and full E2E criteria defined by Phase 8; capture scripted before/after for UI-touched PR as SHELL-07 requires; no test count or performance target asserted without execution.
- **Performance:** measure canvas/tree/cost before enforcing budgets; do not introduce blur, animation or virtualization assumptions without the Phase 0/10 benchmarks already named in `docs/top-100-ui-improvements.md:293,317-325`.

## Implementation file map

| Concern | Likely files (confirm APIs when implementing) |
|---|---|
| Transactional design open | `src/AzureDesignStudio/Components/TopMenu.razor.cs`; `Services/DesignGrpcService.cs`; component tests after Phase 8 scaffolding |
| Palette roles/focus | `Components/CommandPalette.razor`; `wwwroot/css/ads-tokens.css`; shared JS only if necessary |
| Command owner lifetime | `Services/AdsCommandService.cs`; `Components/TopMenu.razor.cs`; `Components/CommandPalette.razor`; `wwwroot/js/ads.js`; client/Desktop DI in `Program.cs` and `Desktop/MauiProgram.cs` |
| Status and sync truth | `Components/StatusBar.razor`; `Components/TopMenu.razor.cs`; `Services/DesignGrpcService.cs`; Desktop offline store abstraction; avoid new status-only source of truth |
| Responsive/theme/a11y | `wwwroot/css/ads-tokens.css`; `wwwroot/css/app.css`; `Pages/Index.razor`; `wwwroot/index.html`; `Desktop/wwwroot/app.html`; shared components |
| Test/visual gates | New component/E2E test projects and workflows per Phase 8; screenshot artifact policy from Phase 7 `ROADMAP.md:167` |

## Research references

- W3C ARIA APG Modal Dialog Pattern: https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/ — modal focus sequence, Tab containment, Escape, and focus restoration.
- W3C ARIA APG Combobox Pattern: https://www.w3.org/WAI/ARIA/apg/patterns/combobox/ — keyboard and active option relationships.
- W3C ARIA APG Tree View Pattern: https://www.w3.org/WAI/ARIA/apg/patterns/treeview/ — keyboard hierarchy and focus-vs-selection distinction.
- WCAG 2.2 SC 2.4.11, Focus Not Obscured (Minimum): https://www.w3.org/WAI/WCAG22/Understanding/focus-not-obscured-minimum.html — keep focused item at least partially visible.
- WCAG 2.2 SC 2.5.7, Dragging Movements: https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements.html — provide a non-drag pointer alternative unless essential.
- WCAG 2.2 SC 2.5.8, Target Size (Minimum): https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html — 24×24 CSS px target or defined exception/spacing.
- WCAG 2.2 SC 4.1.3, Status Messages: https://www.w3.org/WAI/WCAG22/Understanding/status-messages.html — programmatically determinable status updates without focus.
- WCAG 2.2 SC 1.4.10, Reflow: https://www.w3.org/WAI/WCAG22/Understanding/reflow.html — 320 CSS px reflow except essential two-dimensional content.

## Open decisions

No big model or backend system choice is involved. Before any UI implementation beyond existing Phase 7 scope, confirm whether status labels should surface local-only vs sync state differently in Desktop and WASM, whether the command palette should be a true modal or a non-modal search surface, and whether a user-facing density preference is wanted. Theme, Azure brand, font, framework, and three-zone structure are intentionally treated as existing constraints—not reopened choices.
