# UI Audit — Azure Design Studio

Status: Complete for the bounded source-only audit; visual and runtime validation remain unperformed.

**Audited:** 2026-10-30
**Baseline:** Phase 7 shell contract in `.planning/ROADMAP.md:157-172`, project constraints in `.planning/PROJECT.md:65-74`, existing Round 2 design inventory in `docs/top-100-ui-improvements.md:19-44, 59-94, 300-325`.
**Screenshots:** Not captured; no app launch or dev-server probe was performed under this assignment's source-only constraint.
**Interaction captures:** Not captured; code-derived findings only.
**Repository baseline observed:** branch `main`, HEAD `fa62bb9`; existing untracked `.planning` files and the user's `.planning/CODEBASE-REVIEW.md` were preserved.

## Scope and evidence

Reviewed the shared Blazor client's shell/components, state/service seams, CSS/tokens, JavaScript interop, both HTML entries, Desktop host surface, relevant Phase 1 context/summary, Phase 7 roadmap contract, and existing Round 2 UI inventory. Key source files: `src/AzureDesignStudio/Components/{CommandPalette.razor,TopMenu.razor.cs,StatusBar.razor,DesignInspector.razor,DiagramPanel.razor,DiagramPanel.razor.cs}`, `Services/{AdsContext.cs,AdsCommandService.cs,DesignGrpcService.cs}`, `wwwroot/{css/ads-tokens.css,js/ads.js,index.html}`, `Shared/MainLayout.razor`, `Pages/Index.razor`, `Program.cs`, and `src/AzureDesignStudio.Desktop/wwwroot/app.html`.

No live visual, keyboard, screen-reader, touch, responsive, theme-contrast, performance, build, or test verification was attempted. Scores are provisional source-review scores, not a claim of WCAG conformance or rendered-pixel verification. Existing GSD Phase 1 is complete; Phase 7's contract is still planned (`ROADMAP.md:157-172`).

## Six-pillar assessment

| Pillar | Provisional score | Evidence / limitation |
|---|---:|---|
| 1. Copywriting | 2/4 | Several useful action labels, but the static launch message says “Loading. It may take a minute or two” (`wwwroot/index.html:63`; Desktop `app.html:46`), and failed loads collapse to generic “Cannot load the diagram” (`TopMenu.razor.cs:500-503`). No full-string inventory. |
| 2. Visuals | 2/4 | Intentional shell structure and branded token palette exist (`ads-tokens.css:6-48, 99-120, 316-350`), but narrow layouts have no authored breakpoint rules found in app stylesheet sources; visuals were not rendered. |
| 3. Color | 2/4 | Semantic light/dark tokens exist (`ads-tokens.css:9-29, 51-76`) and the theme is applied before first paint in both entries (`index.html:9-18`, Desktop `app.html:9-18`). Contrast is unmeasured; hardcoded white diagram-link strokes in dark mode are present (`ads-tokens.css:960-966`). |
| 4. Typography | 3/4 | Shared Segoe/system stack, explicit hierarchy, and tabular numeric values are present (`ads-tokens.css:7, 149-163, 464-475, 309-312`). Type scale, zoom/reflow, and real content fit have not been measured. |
| 5. Spacing | 2/4 | Shell spacing is explicit and consistent in inspected CSS, but values are mostly component-local declarations rather than a complete spacing scale; no narrow/zoom layouts were rendered. |
| 6. Experience design | 1/4 | Three high-confidence behavior/accessibility issues below: misleading workspace status, destructive failed-load path, and a modal dialog declaring modality without implementing the expected modal keyboard/focus contract. Runtime behavior remains unobserved. |

## Findings (highest confidence and impact)

1. **BLOCKER — Failed design load can erase the current working canvas.** `src/AzureDesignStudio/Components/TopMenu.razor.cs:475-489` clears nodes, links, and groups before awaiting `designService.LoadDesign`; a non-200, missing payload, transport failure, or later deserialization error then reaches the generic failure branch at lines 493-503, leaving the prior design gone. Preserve the current diagram until a successful response is parsed and validated, then replace it atomically. This conflicts with the product's no-loss / durable-workspace constraints (`.planning/PROJECT.md:71,74`).
2. **BLOCKER — The palette claims to be modal without a modal focus contract.** `CommandPalette.razor:10-12` sets `role="dialog" aria-modal="true"`; opening focuses the search field (`:78-90`), but Tab is not contained, no focus restore is recorded, and `Close()` only changes state (`:118-123`). W3C APG's modal pattern requires Tab/Shift+Tab containment, Escape close, initial focus inside, and return focus to invoker: https://www.w3.org/WAI/ARIA/apg/patterns/dialog-modal/. Implement and test those behaviors or remove the modal claim and make the surface genuinely non-modal.
3. **WARNING — Status display reports a fixed state instead of workspace truth.** `StatusBar.razor:8-20` always renders a green status dot and “Local workspace”; it has no state input, while `DesignGrpcService.LastCallWasOffline` does vary after RPC calls (`Services/DesignGrpcService.cs:15-16,63-76,103-129`). This communicates neither saved/synced nor offline-pending state required by `ROADMAP.md:163-166`. Bind an explicit state model to the actual offline store/sync queue and render text/icon state rather than an unconditional green dot.
4. **WARNING — Command palette's listbox semantics and focus model are inconsistent.** `CommandPalette.razor:24,33-41` uses a `role=listbox` container with focusable `<button role="option">` children, while arrow handling changes an `active` index on the search input (`:147-166`). There is no option ID, `aria-activedescendant`, or roving focus; assistive technology receives no established link between the focused search control and active option. Adopt one documented APG pattern (combobox/listbox or dialog with ordinary buttons) and test the resulting keyboard and announcement model: https://www.w3.org/WAI/ARIA/apg/patterns/combobox/.
5. **WARNING — Key command listener retains a disposed component reference.** `Program.cs:72` registers `AdsCommandService` as singleton. `TopMenu.razor.cs:22-44` registers callbacks capturing the component's methods, and the current file has no dispose/unregister path (cross-check `search_files` found only registration; `AdsCommandService.cs:29-35` overwrites by key but has no owner removal). The global JS shortcut reference is overwritten by each palette init and the document listener persists (`wwwroot/js/ads.js:312-325`); `CommandPalette.razor:175-180` disposes only its .NET reference, not the JS reference. Phase 1 explicitly defers ownership cleanup to SHELL-02 (`01-CONTEXT.md:40,134`). Add owner-scoped registration/removal and dispose the JS callback reference/listener safely; cover remount in Phase 8 tests.

## Pillar evidence and limits

- **Copywriting:** palette labels and empty result copy are contextual (`CommandPalette.razor:19,27-28,53-56`); there is no whole-client review of every drawer string. Launch/failure status needs specific timing and recovery guidance.
- **Visuals:** a deliberate three-column shell is implemented (`Pages/Index.razor:14-40`) and theme tokens style the shell. Responsive behavior remains unproven; static review found no app-authored media query in the inspected token sheet, and the separate `app.css` is legacy-heavy. This is not a screenshot verdict.
- **Color:** theme mode is initialized before paint and supports system preference (`wwwroot/index.html:9-18`, `js/ads.js:262-310`); tokenized semantic color families exist. No contrast calculations were run. Hardcoded white paths and arrows (`ads-tokens.css:960-966`) need testing against the dark canvas and selected/hover states.
- **Typography:** `--ads-font` and shell text sizes are present (`ads-tokens.css:7,149-163,413-489`); relative scaling/zoom and component-library text are not comprehensively enumerated.
- **Spacing:** token file contains radii and shell dimensions but no semantic spacing scale (`ads-tokens.css:31-42`); many values are literal local CSS values. No claim that arbitrary values are inherently defects in this dense editor.
- **Experience design:** failure-state source paths exist, but load replaces before success; save messages distinguish local/offline (`TopMenu.razor.cs:462-472`), while status does not; palette focus/semantics needs APG-conformant behavior. No live test.

## Source-only Craft Report

### Checked

Shell hierarchy and three-zone host; palette semantics/focus lifetime; save/load replacement ordering; status state provenance; theming entry points and token declarations; reduced-motion/focus/responsive indicators in app CSS; JS interop and command registration lifetime. Only the specific files listed above; this was not a whole-repository audit.

### Passed

Both host entry documents establish the theme attribute before the component app starts (`wwwroot/index.html:9-18`, Desktop `wwwroot/app.html:9-18`). The inspected shell uses native button elements for rail toggles (`Pages/Index.razor:19-26`) and theme toggle (`ThemeToggle.razor:3-9`), and disposal exists for diagram event subscriptions (`DiagramPanel.razor.cs:53-62`) and status subscriptions (`StatusBar.razor:69-79`).

### Changed

No source files changed. This artifact records review findings and a proposed plan only.

### Left alone

Kept Azure-blue accent and existing Segoe/system font because those are established project decisions (`docs/top-100-ui-improvements.md:63,93`; `ads-tokens.css:7,24`); the UI audit does not authorize a brand reset. Kept Blazor, AntDesign, and Z.Blazor.Diagrams as mandated platform choices. Did not call the status bar a full Phase 7 failure: the roadmap contract is still pending, and this audit is source-only.

### Verdict

Source review found a destructive failed-load path and modal/accessibility contract gaps; fix the failed-load transaction first, then close the palette focus and truthfulness gaps and verify with scripted keyboard/screen-reader and state tests.

## Files audited

`src/AzureDesignStudio/Components/CommandPalette.razor`; `TopMenu.razor.cs`; `StatusBar.razor`; `DesignInspector.razor`; `DiagramPanel.razor`; `DiagramPanel.razor.cs`; `ThemeToggle.razor`; `src/AzureDesignStudio/Services/AdsContext.cs`; `AdsCommandService.cs`; `DesignGrpcService.cs`; `src/AzureDesignStudio/Program.cs`; `src/AzureDesignStudio/Pages/Index.razor`; `src/AzureDesignStudio/Shared/MainLayout.razor`; `src/AzureDesignStudio/wwwroot/css/ads-tokens.css`; `wwwroot/css/app.css`; `wwwroot/js/ads.js`; `wwwroot/index.html`; `src/AzureDesignStudio.Desktop/wwwroot/app.html`; `.planning/ROADMAP.md`; `.planning/PROJECT.md`; `.planning/phases/01-land-and-stabilize/01-CONTEXT.md`; `.planning/phases/01-land-and-stabilize/01-08-SUMMARY.md`; `docs/top-100-ui-improvements.md`.