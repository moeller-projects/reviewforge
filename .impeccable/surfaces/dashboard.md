---
version: 1
slug: "dashboard"
primary_target: "src/ReviewForge.Service/Components/App.razor"
related_targets: ["src/ReviewForge.Service/Components/Pages","src/ReviewForge.Service/wwwroot/dashboard.css"]
---

# Dashboard workbench

Target: src/ReviewForge.Service/Components/App.razor
Related: src/ReviewForge.Service/Components/Pages, src/ReviewForge.Service/wwwroot/dashboard.css
Mode: Operate

Audience: PR authors/reviewers operating loaded review history. The immediate job is to open a run, inspect it and deliberately act. T01 changes the shell and controls only; queue/evidence and command workflows follow in later tasks.

## Direction contract

THESIS: A compact review workbench, not a decorative metric dashboard. Hierarchy and explicit action labels lead.

OWN-WORLD: Cool neutral layered surfaces, restrained blue focus/selection, system typography, code-only monospace, thin dividers and small native controls.

STORY: Read attention/history first, open the relevant run, inspect findings/context/execution, and explicitly confirm external actions. Unknown states remain labeled.

FIRST VIEWPORT: A narrow persistent navigation header with ReviewForge, Runs and Start review, theme choice at the right; route content below with action toolbar and bounded history. On phones navigation wraps and forms stack. Signature interaction: immediate light/dark/system changes without resetting the current review. Motion is limited to control state feedback, never page entrances.

FORM: seed: none (pinned). Confirmed brief: “compact, quiet, task-focused, Linear-inspired”; “Queue + persistent evidence pane is the default”; “Keep Blazor Interactive Server; replace Radzen with purpose-specific components and remove the dependency.” Existing route contents remain intact for T01.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Verification boundary

No browser or application startup is authorized for T01. bUnit, asset/hosting/security and isolated JS tests supply nonvisual evidence only. Desktop/tablet/phone layout, native dialog focus/inertness, first-paint flash and theme contrast require real-render inspection before release. No raster assets ship. Parent owns independent review; no child agents are authorized in this implementation task.
