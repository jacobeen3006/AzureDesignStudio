---
phase: 01-land-and-stabilize
reviewed: 2026-10-21T00:00:00Z
depth: deep
scope: "git diff 078a9d7^..3315b94 -- src/"
files_reviewed: 49
files_reviewed_list:
  - src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs
  - src/AzureDesignStudio.Core.Tests/StencilTests.cs
  - src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs
  - src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs
  - src/AzureDesignStudio.Core/Components/AzureGroupComponent.razor.css
  - src/AzureDesignStudio.Core/Components/AzureNodeComponent.razor.css
  - src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs
  - src/AzureDesignStudio.Core/DataModelFactory.cs
  - src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor
  - src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs
  - src/AzureDesignStudio.Core/Network/BastionsForm.razor
  - src/AzureDesignStudio.Core/Network/BastionsModel.cs
  - src/AzureDesignStudio.Core/Network/SubnetForm.razor
  - src/AzureDesignStudio.Core/Network/SubnetModel.cs
  - src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs
  - src/AzureDesignStudio.Desktop/MauiProgram.cs
  - src/AzureDesignStudio.Desktop/wwwroot/app.html
  - src/AzureDesignStudio/Components/CommandPalette.razor
  - src/AzureDesignStudio/Components/DesignInspector.razor
  - src/AzureDesignStudio/Components/DiagramNavigator.razor
  - src/AzureDesignStudio/Components/DiagramNavigator.razor.css
  - src/AzureDesignStudio/Components/DiagramPanel.razor
  - src/AzureDesignStudio/Components/DiagramPanel.razor.cs
  - src/AzureDesignStudio/Components/DiagramPanel.razor.css
  - src/AzureDesignStudio/Components/StatusBar.razor
  - src/AzureDesignStudio/Components/Stencil.razor
  - src/AzureDesignStudio/Components/Stencil.razor.css
  - src/AzureDesignStudio/Components/StencilPanel.razor
  - src/AzureDesignStudio/Components/StencilPanel.razor.css
  - src/AzureDesignStudio/Components/ThemeToggle.razor
  - src/AzureDesignStudio/Components/TopMenu.razor
  - src/AzureDesignStudio/Components/TopMenu.razor.cs
  - src/AzureDesignStudio/Components/TopMenu.razor.css
  - src/AzureDesignStudio/Models/StencilModel.cs
  - src/AzureDesignStudio/Pages/Index.razor
  - src/AzureDesignStudio/Pages/Index.razor.css
  - src/AzureDesignStudio/Program.cs
  - src/AzureDesignStudio/Services/AdsCommandService.cs
  - src/AzureDesignStudio/Shared/MainLayout.razor
  - src/AzureDesignStudio/wwwroot/ads-stencils.json
  - src/AzureDesignStudio/wwwroot/assets/azure/subnet.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/ai.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/containers.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/general.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/integration.svg
  - src/AzureDesignStudio/wwwroot/assets/icons/security.svg
  - src/AzureDesignStudio/wwwroot/css/ads-tokens.css
  - src/AzureDesignStudio/wwwroot/index.html
  - src/AzureDesignStudio/wwwroot/js/ads.js
findings:
  critical: 0
  high: 2
  medium: 2
  low: 7
  total: 11
status: issues_found
---

# Phase 01: Code Review Report

**Reviewed:** 2026-10-21
**Depth:** deep (per-file plus cross-file call-chain and library-behaviour tracing)
**Files Reviewed:** 49
**Status:** issues_found

## Summary

Scope was `git diff 078a9d7^..3315b94 -- src/` — the three land commits restricted to source, test, host, catalog and style files. The docs-only commit's prose was not reviewed.

The C# side is in good shape. `LinkTypeClassifier` is total (null endpoints, every endpoint order), the `Anchor`-to-node resolution added in `DataModelFactory.ParentOf` and `AzureNodeProfile.GetAnchorParentId` is correct — I verified against the installed `z.blazor.diagrams.core/3.0.4.1` assembly that `Anchor.Model` is an `ILinkable` which is a `PortModel` for `SinglePortAnchor` and a `NodeModel` otherwise, so both helpers take the right branch. `link.Color = null` for untyped links is also safe: `LinkWidget` renders `Link.Color ?? Options.Links.DefaultColor`, so no link loses its stroke. The new Blazor shell components dispose their `DotNetObjectReference` and unsubscribe from every diagram event they subscribe to; there is no `async void`, no `StateHasChanged` from a non-renderer thread, and no unbounded interop inside a render loop. The command palette does not evaluate user input — it filters with `Contains` and renders through Razor encoding.

Two defects are worth fixing before this lands as "done": the pointer-drag state machine can drop a resource from a plain click after an interrupted drag, and the two style rules that give the drag its feedback are unreachable because they live in a scoped stylesheet.

No credential-shaped strings were added (`ads.theme` is the only `localStorage` key, and it is normalised to a three-value whitelist before use). No `MarkupString`, `innerHTML`, `eval`, or SQL/shell sink appears in the diff.

---

### H1: Interrupted pointer drag leaves state armed; a later plain click on the canvas creates a node and invokes `HandleDropAtAsync` a second time

**File:** `src/AzureDesignStudio/wwwroot/js/ads.js:173-220` (pointerdown / drag start), `src/AzureDesignStudio/wwwroot/js/ads.js:233-253` (finish / listeners)

**What is wrong:** `active` is only ever cleared in `finish()` (pointerup/pointercancel, line 239) and in the `window` `blur` listener (line 250), and `pointerdown` (line 173) overwrites `active` (line 185) without first clearing a previous one. Nothing validates that the button is still down on `pointermove`, and no pointer capture is taken.

Reproduction that does not require leaving the window for long:

1. `pointerdown` on a stencil tile → `active = { key, pointerId: 1, dragging: false }` (lines 185-194).
2. `pointermove` past the 4 px threshold → `dragging = true`, ghost appended, `body.ads-dragging` added (lines 204-219).
3. Release the left button **outside the WebView window** (drag to the window edge and release over the desktop/taskbar, or release while a native context menu swallowed the up event). No `pointerup` reaches `document`, and the window never loses focus, so `blur` does not fire either. `active` and the ghost survive.
4. Move the cursor back over the app with **no button held**: `pointermove` (line 196) only checks `e.pointerId`, so `active.dragging` is already `true`; the ghost follows the cursor and `ads-drop-hover` toggles on.
5. Click the canvas. `pointerdown` (line 173) returns early because the target is not a `[data-stencil-key]` tile, so `active` is untouched. The click's `pointerup` reaches `finish()` (line 233), which sees `drag.dragging === true`, `inZone(...) === true` and a live `__adsRef`, and calls `invokeMethodAsync('HandleDropAtAsync', …, drag.key)` (line 244) with the **stale** key.

**Why it matters:** a single left click on empty canvas after an interrupted drag silently creates a resource (`DiagramPanel.HandleDropAtAsync` → `DropStencilAtAsync`) at the click point, and does so outside the intent of any drag. It is also the double-invoke path the review brief asked about: the same `in` — `active` — is replayed. Secondary damage: the ghost element from step 2 is never removed, and the next drag appends a second ghost, so `.ads-drag-ghost` nodes accumulate in `<body>` with no reference held to the older ones. `body.ads-dragging` also stays applied.

**Fix (minimal):** discard a stale drag whenever the pointer is demonstrably no longer dragging, and make `pointerdown` defensive:

```javascript
// in pointerdown, before assigning a new active
if (active) {
    const stale = active;
    active = null;
    clear(stale);
}

// in pointermove, right after the pointerId guard
if (!e.buttons) {          // button was released outside the window
    const stale = active;
    active = null;
    clear(stale);
    return;
}

// and take capture so pointerup is still delivered
tile.setPointerCapture(e.pointerId);
```

`clear()` already removes the ghost and the body class, so the three consequences collapse into one fix.

---

### H2: `.ads-drag-ghost` and `body.ads-dragging` are unreachable rules — the drag ghost renders unstyled (no feedback) because they live in a scoped stylesheet

**File:** `src/AzureDesignStudio/Components/Stencil.razor.css:17-30` (`.ads-drag-ghost`), `src/AzureDesignStudio/Components/Stencil.razor.css:32-34` (`body.ads-dragging`)

**What is wrong:** Both selectors are JS-facing: the ghost is created with `document.createElement('div')` and only `className = 'ads-drag-ghost'` (`ads.js:215-217`), and the body class is set on `document.body` (`ads.js:209`). Blazor CSS isolation rewrites every rule in a `.razor.css` file by appending the component's scope attribute to the right-most selector. The built bundle proves it:

- `src/AzureDesignStudio/obj/Debug/net10.0/scopedcss/bundle/AzureDesignStudio.styles.css:313` → `.ads-drag-ghost[b-c1nacwqzre]`
- `src/AzureDesignStudio/obj/Debug/net10.0/scopedcss/bundle/AzureDesignStudio.styles.css:328` → `body.ads-dragging[b-c1nacwqzre]`

Neither the JS-created `div` nor `<body>` carries `b-c1nacwqzre`, so neither rule ever matches. There is no unscoped duplicate: a search of every `*.css` under `src/` finds these two class names only in `Stencil.razor.css`.

**Why it matters:** the pointer-drag rewrite's visual contract is gone. The ghost has no `position: fixed`, no `pointer-events: none`, no `z-index`, no `transform`, so `ghost.style.left/top` (`ads.js:220-223`) are inert against a static block-level div; with `html, body, #app { height: 100%; overflow: hidden }` (`ads-tokens.css:78-83`) that div is appended after `#app` and clipped, i.e. the user gets **no drag feedback at all** while the drag is in progress. `body.ads-dragging { cursor: grabbing }` likewise never applies. This is measured, not inferred: the compiled bundle is in the working tree.

**Fix:** move both rules to the global stylesheet (`src/AzureDesignStudio/wwwroot/css/ads-tokens.css`, a `/* ---------- drag ---------- */` block beside the diagram-link rules) and delete them from `Stencil.razor.css`. `::deep` cannot rescue `body.ads-dragging` — there is no component-scoped ancestor to hang the attribute on — so a global file is the only correct home.

---

### M1: `SubnetForm` offers a delegation value that no Azure service provides, producing an undeployable ARM template

**File:** `src/AzureDesignStudio.Core/Network/SubnetForm.razor:17`

**What is wrong:** the new form ships `Microsoft.Database/custom` as a selectable delegation:

```razor
<SimpleSelectOption Label="Microsoft.Database/custom" Value="@("Microsoft.Database/custom")" />
```

`SubnetModel.Delegation` is persisted (`[MapToDto]`) and emitted verbatim as `delegations[].properties.serviceName` on export (`SubnetModel.cs:148-159`). ARM accepts only a registered delegating service (`<provider>/<type>`, e.g. `Microsoft.Web/serverfarms`, `Microsoft.App/environments`, `Microsoft.Sql/managedInstances`). `Microsoft.Database` is not an Azure resource provider and `custom` is not a delegating type, so any design using this option exports a template that ARM rejects at deployment with a delegation error. [The exact ARM error string is inference; that the value names no delegating service is not.]

**Why it matters:** the phase's premise is that exported designs deploy. This option is presented as an equally valid choice next to two real ones, with no validation anywhere between the dropdown and `GetArmResources()`.

**Fix:** remove the option, or replace it with a real delegating service. If a free-text escape hatch is genuinely wanted, `SimpleSelect` is the wrong control — use an input that validates against a whitelist in `SubnetModel` and surfaces the problem before export.

---

### M2: The auto-provision drop path matches the subnet name case-insensitively, then `IsDrappable` rejects it case-sensitively

**File:** `src/AzureDesignStudio/Components/DiagramPanel.razor.cs:160-178`, `src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs:47`, `src/AzureDesignStudio.Core/Network/BastionsModel.cs:28`, `src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor:8`, `src/AzureDesignStudio.Core/Network/BastionsForm.razor:8`

**What is wrong:** `DropStencilAtAsync` looks for an existing preset subnet with `StringComparison.OrdinalIgnoreCase`:

```csharp
var existing = vnet.Subnets.FirstOrDefault(
    s => s.Name.Equals(dedicatedName, StringComparison.OrdinalIgnoreCase));   // DiagramPanel.razor.cs:161
```

and, when found, assigns it to `overlappedGroup`. Seventeen lines later the same object is passed to `IsDrappable`, which compares with ordinal case-sensitive `Equals`:

```csharp
if (overlappedGroup is not SubnetModel subnet || !subnet.Name.Equals("AzureFirewallSubnet"))  // AzureFirewallModel.cs:47
    return (false, "Azure Firewall needs a dedicated subnet named AzureFirewallSubnet. …");
```

So a VNet containing a subnet named `azurefirewallsubnet` (reachable: `SubnetForm.razor:6` binds `Name` to a free-text input; the preset text is only a placeholder) takes this path: the lookup finds the subnet, then `IsDrappable` rejects it and the user is told to create the very subnet that exists. `IsValid` (`AzureFirewallModel.cs:65-66`, `BastionsModel.cs:46-47`) and both forms' warning banners (`:8` in each) repeat the case-sensitive comparison, so the inspector reports "needs attention" and the form shows the "place inside an AzureFirewallSubnet" warning for a design that exports correctly. The `existing` branch also skips the auto-provision message, so nothing explains the contradiction.

**Why it matters:** two code paths in the same feature disagree about what a preset subnet is, and the failure mode is a dead-end error dialog whose instruction the user has already followed. ARM treats resource names case-insensitively, so the app is stricter than the platform it targets.

**Fix:** give `SubnetModel` the predicate once and use it everywhere:

```csharp
public bool HasName(string expected) =>
    string.Equals(Name, expected, StringComparison.OrdinalIgnoreCase);
```

then replace the `Name.Equals("AzureFirewallSubnet")` / `("AzureBastionSubnet")` comparisons at `AzureFirewallModel.cs:47,66`, `BastionsModel.cs:28,47`, and both forms' `:8`, and delete the now-duplicate `OrdinalIgnoreCase` lookup in `DiagramPanel.razor.cs:161` in favour of the same predicate.

---

### L1: The HTML5 drop seam (`window.AdsDropZone`) is now unreachable for stencils; ~60 lines of dead drag handling remain, and one line of it is load-bearing

**File:** `src/AzureDesignStudio/wwwroot/js/ads.js:56-118`, `src/AzureDesignStudio/Components/Stencil.razor:6-7`

**What is wrong:** this phase flipped the tile to `draggable="false"` (and the `<img>` too). The only elements carrying `data-stencil-key` are those tiles (`Stencil.razor:6`; verified by grep across `*.razor`/`*.js`/`*.html`), so `dragstart` can no longer originate from a stencil, `self.dragKey` stays `''`, and the `drop` listener's `invokeMethodAsync(…, self.dragKey || '')` resolves to `SingleOrDefault(s => s.Key == '')` → `null` → no-op. `dragKey` is also never reset on `dragend`, so if any future draggable element reused the attribute, a keyless drop would replay the previous key.

**Why it matters:** the block's comment still advertises it as the stencil drop path, so the next reader will maintain two competing implementations. One line in it is genuinely required: `zone.__adsRef = dotNetRef` (line 68) is what makes the pointer path's re-resolved wrapper able to reach the current `DiagramPanel`, and its placement above the `__adsDrop` early-return is what makes remount adoption work.

**Fix:** keep the ref adoption and delete the `dragstart`/`dragend`/`dragover`/`dragenter`/`dragleave`/`drop` listeners and the `dragKey` field; rename the object to something like `AdsDropTarget.init` so its remaining job (adopt the latest ref per wrapper element) is stated rather than implied. If external file drops are wanted later, add that listener together with a real payload, not a lingering `dragKey`.

---

### L2: `invokeMethodAsync` results are unhandled; a disposed `DotNetObjectReference` produces an uncaught rejection, and nothing tears down the Ctrl+K listener

**File:** `src/AzureDesignStudio/wwwroot/js/ads.js:112`, `src/AzureDesignStudio/wwwroot/js/ads.js:244`, `src/AzureDesignStudio/wwwroot/js/ads.js:317-323`

**What is wrong:** all three call sites fire and forget. `DiagramPanel.Dispose` disposes `dropRef` (`DiagramPanel.razor.cs:61`) while `zone.__adsRef` still points at it and the document-level pointer listeners stay attached for the app lifetime; `CommandPalette.Dispose` disposes `self` while `window.__adsShortcutRef` and the `keydown` listener stay. A drop/click or Ctrl+K landing in that window calls a disposed reference, which rejects the returned promise (`Error: There is no tracked object with id…`). `AdsShortcuts` exposes no teardown at all, so the listener and ref cannot be released.

**Why it matters:** low blast radius today (`MainLayout` and `DiagramPanel` live for the session), but it is an uncaught JS error on a normal code path — a drop that races a remount — and it will mask real interop failures in the console during UAT.

**Fix:** append `.catch(function () { })` to the three `invokeMethodAsync` calls, and add `AdsShortcuts.dispose = function () { window.__adsShortcutRef = null; }` (listen-once stays as-is) called from `CommandPalette.Dispose` alongside `self?.Dispose()`.

---

### L3: `NextFreeSubnetPrefix` returns an already-used prefix when the VNet address space is exhausted, contradicting its own contract

**File:** `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs:61-87` (fallback at line 87)

**What is wrong:** the loop returns the first `/24` not present in `used`; when every `/24` in the space is taken it falls through to `return FormatPrefix(baseAddress, prefixLength);` — i.e. the first block of the space, which is by construction in `used`. The XML doc directly above states the opposite: *"First unused /24 … so auto-created subnets never overlap one the user already defined."*

**Why it matters:** `DropStencilAtAsync` (`DiagramPanel.razor.cs:167-172`) uses this to build the auto-created `AzureFirewallSubnet`; on a fully allocated VNet it silently creates a subnet whose `addressPrefix` duplicates an existing one, and `PopulateArmAttributes` emits it. Azure rejects overlapping subnets — the user sees a deployment failure with no hint that the app chose the prefix.

**Fix:** make exhaustion explicit: `public string? NextFreeSubnetPrefix()` returning `null`, and have the caller report "no free /24 in <vnet> — add address space or a subnet" instead of creating the frame. That keeps the single caller honest and removes the false promise from the doc comment.

---

### L4: The inspector shows CLR type names to users

**File:** `src/AzureDesignStudio/Components/DesignInspector.razor:109` (rendered at `:58`)

**What is wrong:** `private string SelectedKind => SelectedModel?.GetType().Name ?? string.Empty;` puts `AzureFirewallModel`, `VirtualNetworkModel`, `SubnetModel`, `LinkModel` in the "Design" panel — the same surface where every other string is human-facing (`ServiceName` → "Azure Firewall", "Virtual Network").

**Why it matters:** it is the one label in the new shell that leaks implementation detail, and `IAzureNode.ServiceName` already exists for exactly this (`AzureNodeBase.cs:29`). Also cosmetic-inconsistent: a selected link reads "LinkModel" where the link itself now has a semantic label ("Peering").

**Fix:**

```csharp
private string SelectedKind => SelectedModel switch
{
    AzureNodeBase node when !string.IsNullOrEmpty(node.ServiceName) => node.ServiceName,
    AzureGroupBase group when !string.IsNullOrEmpty(group.ServiceName) => group.ServiceName,
    LinkModel => "Connection",
    _ => string.Empty,
};
```

---

### L5: Command palette ARIA wiring is structurally invalid, and the backdrop is mouse-only

**File:** `src/AzureDesignStudio/Components/CommandPalette.razor:10` (backdrop), `:24` (`role="listbox"`), `:38` (`role="option"` on `<button>`)

**What is wrong:** `role="listbox"` wraps a container of `<button role="option">` elements, with no `aria-activedescendant` on the combobox input and no `id` on the options, so assistive tech cannot report the arrow-key cursor (`active`). `role="option"` on a native button overrides the button role and drops the button semantics the keyboard path relies on. The backdrop is a plain `<div @onclick="Close">` — dismissible by mouse and by Esc (handled), not by focus.

**Why it matters:** the palette is a primary navigation surface for the new shell; the visual `active` highlight is invisible to screen readers, and the invalid role nesting makes the list ambiguous. Not a data-correctness issue, which is why it is low.

**Fix:** keep the `<button>`s but drop `role="option"` and the outer `role="listbox"` (use `role="dialog"` + a labelled list), or go all the way: give each option `id="ads-cmd-{i}"`, put `role="listbox"` on the list, `aria-activedescendant="ads-cmd-{active}"` and `aria-expanded="true"`/`aria-controls` on the input, and render options as `div[role=option]` with the click handler. The second option is the standard combobox pattern.

---

### L6: `StencilTests` restates the category range as two magic literals instead of deriving it from the enum it is guarding

**File:** `src/AzureDesignStudio.Core.Tests/StencilTests.cs:42`, `src/AzureDesignStudio.Core.Tests/StencilTests.cs:56`

**What is wrong:** `category.GetInt32() is >= 0 and <= 9` and `Assert.Equal(Enumerable.Range(0, 10), used)` both hardcode the ten-member shape of `StencilCategory` (`Models/StencilModel.cs:13-27`). These are the guards that exist to catch a category/enum drift, but they encode the drift target themselves: adding a category means editing two numeric literals in the test file *and* the JSON, with nothing tying the three together — while the test name claims it verifies "every category".

**Why it matters:** the useful invariant is "the catalog uses exactly the enum's members". Written numerically it silently keeps passing for a removed enum member my test would have caught, and it fails with a numeric range error rather than naming the missing category.

**Fix:**

```csharp
var declared = Enum.GetValues<StencilCategory>().Select(c => (int)c).OrderBy(c => c);
var used = LoadStencils().Select(e => (StencilCategory)e.GetProperty("category").GetInt32()).Distinct().OrderBy(c => c);
Assert.Equal(declared, used.Select(c => (int)c));
```

That also makes `EveryStencilEntryHasRequiredFields`'s range check redundant: parsing into the enum already rejects undefined numeric values.

---

### L7: The new auto-provision behaviour has no test, and the tests that were added stop at `IsDrappable`

**File:** `src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs` (whole file); subject logic at `src/AzureDesignStudio/Components/DiagramPanel.razor.cs:94-100` (`DedicatedSubnetName`) and `:152-206` (provisioning)

**What is wrong:** `SubnetPlacementTests` pins `SubnetModel.IsDrappable`, `AzureFirewallModel.IsDrappable`, the stencil-key factory, `NextFreeSubnetPrefix` and the public-IP validity rule — all real behaviour, none of them restating constants. But the phase's actual new behaviour, "drop a firewall/bastion on a VNet and the preset subnet is provisioned", lives in `private static string? DedicatedSubnetName(NodeModel)` and the `existing`/provision branches of `DropStencilAtAsync`, both in the UI component and therefore unreachable from the offline suite.

**Why it matters:** that path contains the case-insensitive lookup, the `/24` allocation, the empty-subnet precondition and the history records — the highest-risk code in the phase — and it is exactly where M2 lives. A test asserting `DedicatedSubnetName` for both models plus the `existing`-subnet decision as a pure function would have caught M2 without a browser.

**Fix:** extract the decision into a pure, testable helper (e.g. `static (SubnetModel? existing, string? createName) ResolvePresetSubnet(VirtualNetworkModel, NodeModel)`), call it from `DropStencilAtAsync`, and cover: no preset subnet → create; exact-name subnet present → reuse; different-case name present → reuse (the M2 regression); VNet with no free `/24` → refuse.

---

## Files read in full

- `src/AzureDesignStudio/wwwroot/js/ads.js` (all 326 lines, in ranges 1-49, 50-273, 274-326)
- `src/AzureDesignStudio/Components/CommandPalette.razor`
- `src/AzureDesignStudio/Components/DesignInspector.razor`
- `src/AzureDesignStudio/Components/StatusBar.razor`
- `src/AzureDesignStudio/Components/ThemeToggle.razor`
- `src/AzureDesignStudio/Components/Stencil.razor`
- `src/AzureDesignStudio/Components/Stencil.razor.css`
- `src/AzureDesignStudio/Components/StencilPanel.razor`
- `src/AzureDesignStudio/Components/DiagramPanel.razor`
- `src/AzureDesignStudio/Components/DiagramPanel.razor.cs` (all 369 lines, in ranges 1-18, 19-228, 229-368, 369)
- `src/AzureDesignStudio/Components/DiagramPanel.razor.css`
- `src/AzureDesignStudio/Components/DiagramNavigator.razor`
- `src/AzureDesignStudio/Components/DiagramNavigator.razor.css`
- `src/AzureDesignStudio/Components/NodeDrawerTemplate.razor` (unchanged; read to confirm the `context` binding in the forms)
- `src/AzureDesignStudio/Services/AdsCommandService.cs`
- `src/AzureDesignStudio/Services/AdsContext.cs` (unchanged; read to check `Diagram` lifetime/staleness)
- `src/AzureDesignStudio/Pages/Index.razor`
- `src/AzureDesignStudio/Pages/Index.razor.css`
- `src/AzureDesignStudio/Shared/MainLayout.razor`
- `src/AzureDesignStudio.Core/Common/LinkTypeClassifier.cs`
- `src/AzureDesignStudio.Core/DTO/AzureNodeProfile.cs`
- `src/AzureDesignStudio.Core/Network/VirtualNetworkModel.cs` (all 169 lines)
- `src/AzureDesignStudio.Core/Network/SubnetModel.cs` (all 173 lines)
- `src/AzureDesignStudio.Core/Network/BastionsModel.cs`
- `src/AzureDesignStudio.Core/Network/AzureFirewallModel.cs`
- `src/AzureDesignStudio.Core/Network/SubnetForm.razor`
- `src/AzureDesignStudio.Core/Network/AzureFirewallForm.razor`
- `src/AzureDesignStudio.Core/Network/BastionsForm.razor`
- `src/AzureDesignStudio.Core.Tests/LinkTypeTests.cs`
- `src/AzureDesignStudio.Core.Tests/SubnetPlacementTests.cs`
- `src/AzureDesignStudio.Core.Tests/StencilTests.cs`
- `src/AzureDesignStudio/Models/StencilModel.cs`

## Files reviewed by diff plus targeted reads

- `src/AzureDesignStudio.Core/DataModelFactory.cs` — regions 90-175 and 279-356 (factory, semantics, load path)
- `src/AzureDesignStudio/Components/TopMenu.razor` / `.razor.cs` — markup head plus 20-70 (command registration)
- `src/AzureDesignStudio/wwwroot/css/ads-tokens.css` — 1-110, 300-380, 938-972 read verbatim; remaining blocks checked by selector grep (global-selector audit, token-definition coverage against every new component stylesheet)
- `src/AzureDesignStudio/Components/StencilPanel.razor.css`, `TopMenu.razor.css`, `Core/Components/AzureNodeComponent.razor.css`, `Core/Components/AzureGroupComponent.razor.css` — full diffs plus the rules they touch
- `src/AzureDesignStudio/wwwroot/index.html`, `src/AzureDesignStudio.Desktop/wwwroot/app.html` — heads plus script/link ordering
- `src/AzureDesignStudio/Program.cs`, `src/AzureDesignStudio.Desktop/MauiProgram.cs` — diffs
- `src/AzureDesignStudio/wwwroot/ads-stencils.json` — parsed; category histogram checked against `StencilCategory` (0-9 all populated, one Gallery/General entry each as intended)
- six added SVGs — XML well-formedness checked (all parse)

## Verification notes (reproducible)

- Scoped-CSS finding H2 is confirmed by the build artefact `src/AzureDesignStudio/obj/Debug/net10.0/scopedcss/bundle/AzureDesignStudio.styles.css` lines 313 and 328.
- `Anchor.Model` being a `PortModel` for `SinglePortAnchor` (which makes `LinkedPublicIps`, `ParentOf` and `GetAnchorParentId` correct) was confirmed by reflection against `~/.nuget/packages/z.blazor.diagrams.core/3.0.4.1/lib/net6.0/Blazor.Diagrams.Core.dll`, not assumed from the tests.
- Null `LinkModel.Color`, `Width = 3` and the marker behaviour were checked against `LinkWidget.razor` at tag 3.0.4 of the upstream library, which resolves `Link.Color ?? Options.Links.DefaultColor`, so `ApplySemantics` does not make ordinary links invisible.
- No test run or build was performed as part of this review (the working-tree suite result, `Passed: 755, Failed: 0`, is taken as given by the phase record and was not re-executed).

## Self-Check: PASSED
