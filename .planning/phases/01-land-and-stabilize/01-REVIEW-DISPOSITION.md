---
phase: 01-land-and-stabilize
review: 01-REVIEW.md
disposition_at: 2026-10-21
source_findings:
  critical: 0
  high: 2
  medium: 2
  low: 7
  total: 11
gate: advisory
verification: passed
---

# Review Disposition — Phase 01

The code-review capability is advisory: it never blocks phase execution, and this phase's
verification passed 5/5 independently of these findings. Nothing in this table is fixed inside
phase 01, because D-01 fixes the phase at exactly three land commits — a fourth commit would
invalidate the per-commit worktree proofs and the clone-verified recovery bundle that SC#2 and
SC#3 rest on. Every finding below is recorded `open` with a recommended route.

| ID | Severity | Location | Finding | Status | Route |
|----|----------|----------|---------|--------|-------|
| H1 | high | `src/AzureDesignStudio/wwwroot/js/ads.js:173-220,233-253` | `active` survives a pointer capture lost outside the WebView; a later plain canvas click reaches `finish()` and replays `HandleDropAtAsync` with a stale stencil key (node appears from one click, ghost orphaned) | open | Fix in the next shell/pointer phase; needs a harness case that releases outside the viewport |
| H2 | high | `src/AzureDesignStudio/wwwroot/js/ads.js` ghost vs `Stencil.razor.css:17-34` | `.ads-drag-ghost` / `body.ads-dragging` are scoped by CSS isolation to `[b-c1nacwqzre]`, so the JS-created ghost and `<body>` never match — no drag feedback under `overflow: hidden` | open | Move both rules to the global `wwwroot/css/ads-tokens.css`; `::deep` cannot reach `body` |
| M1 | medium | `src/AzureDesignStudio.Core/Network/SubnetForm.razor:17` | Offers `Microsoft.Database/custom` as a delegation, emitted verbatim into `delegations[].properties.serviceName` — not a real delegating service, so the ARM template is undeployable | open | Restrict the delegation list to real services in the same form |
| M2 | medium | `DiagramPanel.razor.cs:161` vs `AzureFirewallModel.cs:47`, `BastionsModel.cs:28` | Auto-provision finds an existing preset subnet case-insensitively, then rejects it with a case-sensitive `Equals`; a subnet named `azurefirewallsubnet` yields a self-contradicting banner | open | Normalize both comparisons to one case rule |
| L1 | low | `window.AdsDropZone` in `ads.js` | HTML5 drop seam for stencils is unreachable; ~60 lines of dead handling remain, one line (`zone.__adsRef`) still load-bearing | open | Delete the dead seam once `__adsRef` is set by the live path |
| L2 | low | `ads.js`, `AdsShortcuts` | `invokeMethodAsync` rejections unhandled against disposed refs; no `AdsShortcuts.dispose` for the Ctrl+K listener | open | Add a disposal path; rejections surface rather than vanish |
| L3 | low | `NextFreeSubnetPrefix` | Returns an in-use prefix on address-space exhaustion, contradicting its contract | open | Return null and surface a validation error |
| L4 | low | `DesignInspector.razor` | Renders CLR type names to users | open | Map to display names |
| L5 | low | `CommandPalette.razor` | Listbox/option ARIA structurally invalid; backdrop is mouse-only | open | Correct roles; add keyboard dismissal |
| L6 | low | `StencilTests.cs` | Restates the category range as two magic literals instead of deriving it from the enum | open | Derive from the enum |
| L7 | low | `src/AzureDesignStudio.Core.Tests/` | The new auto-provision behaviour has no test; added tests stop at `IsDrappable` — the gap that let M2 through | open | Cover the provisioning path with the case-mismatch case |

## Explicitly disproven (not findings)

`Anchor.Model as PortModel` is correct for `SinglePortAnchor` (verified against
`z.blazor.diagrams.core/3.0.4.1`); `link.Color = null` is safe via `LinkWidget`'s
`Color ?? Options.Links.DefaultColor`; no component disposes a still-referenced
`DotNetObjectReference` without re-adoption; no injection sink or credential string in the diff;
all six new SVGs parse and every CSS block is brace-balanced.

## Recommended next step

H1 and H2 are user-visible defects in code this phase publishes, and M2 plus L7 are the same
defect at two altitudes. They belong in a follow-up plan, not a post-push amend: run
`/gsd-plan-phase 02 --gaps` (or fold them into Phase 7, Round 2 Shell Completion) with H1/H2/M2/L7
as the acceptance criteria, so the fix lands as a reviewable commit on top of the published head
rather than rewriting phase 01's proof chain.
