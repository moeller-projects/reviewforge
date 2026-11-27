---
name: ReviewForge workbench
description: Compact, quiet review controls with explicit states and deliberate external actions
colors:
  ground: "#f5f6f8"
  surface: "#ffffff"
  raised: "#eef0f4"
  ink: "#20232b"
  muted: "#596170"
  line: "#ccd1db"
  accent: "#3357bf"
  on-accent: "#ffffff"
  selected: "#e4ebff"
  error: "#a32732"
  error-bg: "#fcecee"
  warning: "#735207"
  warning-bg: "#fff5d9"
  success: "#226343"
  success-bg: "#e8f4ed"
  modal-backdrop: "rgb(8 12 22 / .6)"
  dark-ground: "#17191e"
  dark-surface: "#20232a"
  dark-raised: "#2a2e38"
  dark-ink: "#e9ebf2"
  dark-muted: "#afb7c8"
  dark-line: "#454d5c"
  dark-accent: "#a6baff"
  dark-on-accent: "#17244b"
  dark-selected: "#303f66"
  dark-error: "#ffb5bc"
  dark-error-bg: "#432b33"
  dark-warning: "#f4d182"
  dark-warning-bg: "#3e3524"
  dark-success: "#98dbb7"
  dark-success-bg: "#253c32"
typography:
  headline:
    fontFamily: "system-ui, sans-serif"
    fontSize: "1.5rem"
    lineHeight: 1.25
    letterSpacing: "-0.025em"
  title:
    fontFamily: "system-ui, sans-serif"
    fontSize: "1.125rem"
  subtitle:
    fontFamily: "system-ui, sans-serif"
    fontSize: "0.9375rem"
  body:
    fontFamily: "system-ui, sans-serif"
    fontSize: "0.875rem"
    lineHeight: 1.5
  label:
    fontFamily: "system-ui, sans-serif"
    fontSize: "0.8125rem"
rounded:
  status: "4px"
  control: "5px"
  worklist: "6px"
  modal: "12px"
spacing:
  small: "8px"
  group: "12px"
  section: "20px"
  page: "24px"
components:
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.control}"
    padding: "8px 12px"
  button-secondary:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.control}"
    padding: "8px 12px"
---

# Design System: ReviewForge

## Overview

**Creative North Star: "The review workbench"**

The confirmed replacement is Linear-inspired: compact hierarchy, clear text, aligned controls and quiet surfaces, without copied branding. The review content carries the screen. This is a working interface, not a marketing display.

**Key Characteristics:**
- Cool layered neutrals with a restrained blue action and focus vocabulary.
- System typography with monospace reserved for code and identifiers.
- Native controls and purpose-built Blazor components, without a general UI framework.

This record describes source implementation, not a completed visual/browser audit. Real-render inspection remains required.

## Colors

Primary blue marks actions, focus and active navigation. Cool neutrals separate page, work surface and hover layers. Error, warning/stale and success use paired text/background roles with readable labels, never color alone. Selected controls use the selection role, distinct from focus outlines.

Both themes define every role through `--rf-*` custom properties in `src/ReviewForge.Service/wwwroot/dashboard.css`. The `focus` role follows the accent in each theme. Developers work under varying ambient light; System is the default, with explicit Light and Dark choices. A synchronous self-hosted script resolves the theme before CSS. Invalid/denied storage falls back to the OS. Manual overrides persist only the presentation preference; System removes it and follows OS changes.

## Typography

One system sans stack carries headings, controls and prose. Code uses `ui-monospace, Consolas, monospace`. There are no third-party font requests. Body is compact, labels one step smaller; headings use fixed rem sizes rather than viewport scaling. Finding prose is limited to 75ch. Titles, paths and identifiers wrap instead of forcing page overflow.

## Layout

The shell wraps navigation and theme controls above the route content. Page padding is 24px, reduced to 16px at 760px. Main history/run content is bounded to 1200px; general dashboard content to 1440px. Controls group at 8–12px; sections separate at 20–24px.

At 760px and below, toolbars wrap, decision forms and metadata stack, and history indentation shrinks. Lists retain labeled expand controls and ordinary links. Run findings use a compact queue beside one focused evidence/editor pane. A native Queue width range supports keyboard and touch resizing (25–65%, presentation preference only). On phones, inspecting a row shows detail; Return to findings restores focus/scroll to that run-local identity. Filters do not discard the focused evidence or general selection; hidden selections are counted. The queue renders rows only, not an editor/evidence tree per finding.

## Elevation & Depth

Tonal layering and borders supply structure; no decorative shadows. Native modal dialogs enter the top layer with a dark translucent backdrop. Dialogs are viewport-bounded and scroll internally. No entrance choreography; reduced-motion disables animation and transitions.

## Shapes

Small controls have gently rounded corners. History is a single divided surface rather than nested cards. Finding content uses separators rather than decorative boxes. Notices have a full semantic border, not a colored stripe. Modal corners are larger than control corners.

## Components

- **Buttons:** explicit action text, 40px minimum height, neutral hover/active surface, 2px offset focus outline. Primary action uses accent/on-accent. Disabled controls reduce opacity and prohibit pointer action. Worklist rows are at least 44px high.
- **Inputs:** native input/select/textarea with matching surfaces and borders, themed caret, readable placeholders, visible focus and error borders. Theme select is labeled Color theme.
- **Status labels:** state text with semantic tint. Unknown progress uses a native indeterminate progress element and the text Progress unknown; it is not rendered as zero.
- **Notices:** safe errors and stale/partial inventory remain visible with recovery text. Native feedback uses a status region. Existing authenticated request outcomes remain authoritative.
- **Navigation:** ordinary links with active underline, wrapping header and a skip link. No icon font or remote assets.
- **Dialogs:** native `dialog.showModal()` owns focus containment/background inertness. Cancel receives initial focus. Escape invokes cancellation, never confirmation. Disposal removes cancel listeners and restores a connected return target; rerun uses its focusable context wrapper because the initiating button is disabled while pending.
- **Publication:** explicit confirmation remains separate from approval and selection. No mutation is triggered by theme, navigation or dialog dismissal.

## Keyboard review (reference)

The findings workbench owns its keyboard listener and disposes it when the route leaves. Arrow keys move the active row, Enter opens evidence, Space toggles selection, and Shift+arrows extends the selection. Escape returns from evidence before clearing selection; drafts remain intact. `/` focuses loaded-finding search. The clickable Commands & keyboard help entry and Ctrl/Cmd+K open a native command dialog with focus return.

Single-letter J/K, A/R/D and ? are off by default. The dialog offers that preference and advance/stay after saving; only these presentation choices are stored. Editors, native controls, composition, repeated keydown and unrelated dialogs keep their own input behavior. Approve/defer use the guarded individual save; reject opens the existing required-reason editor. Only a confirmed versioned save followed by a successful refresh advances to another pending finding in the filtered deterministic order. Exhaustion is announced without changing filters. Publication still requires its separate selection preview and confirmation.

bUnit and isolated JavaScript tests cover routing and state, not real-browser focus, reflow or assistive-technology behavior. Those checks remain a release gate.

## Batch rejection (reference)

Reject selected with reasons opens one native dialog containing the pinned finding titles, locations and original decision versions. Shared text is inherited by blank overrides; every effective reason must contain 1–2,000 characters after trimming. Confirmation copies reasons and versions into the existing batch endpoint. Approval and publication remain separate.

Reasons live only in the run-local circuit state, across refreshes, filters and run-section changes. Cancel asks before discarding text; leaving the run also warns. Confirmed outcomes remain visible, saved entries leave the composer, and unresolved entries retain their original versions and reasons without retry. To act again, explicitly discard the unresolved draft and compose a new batch after reviewing current decisions. bUnit verifies these state transitions; real-browser focus, assistive-technology announcements and phone reflow remain unverified.

## Continuity and selected publication (reference)

The run-local findings queue stays mounted while Execution or Context is visible, preserving the editor and scroll containers. Reads and saves retain general selection (including hidden findings) and unrelated drafts. Dirty individual decisions and batch rejection reasons both activate navigation warnings; external navigation uses the browser's warning where permitted. Scoped DOM focus/selection and scroll references are retired on disposal and never stored in browser persistence. After a read/save, focus restoration does not override a dialog or a different active control.

Post eligible approved findings prepares a frozen confirmation from the explicitly selected identities. It shows readable titles/locations, reviewed head, exact approval versions and per-finding exclusions. Approval is still not publication. Publication and session expiry do not discard general selection or drafts. Unknown outcomes stay blocked and require the explicit reconciliation action; refresh and reconnect never replay a POST. Native publication-dialog focus returns to the stable `publication-action` wrapper. Automated component/module tests cover these contracts; real-browser focus, scroll, navigation warnings and reconnect behavior remain a release gate.

## Attention worklist and resume (reference)

The loaded worklist retains full PR scope → observed revision → run grouping. Pending decisions and approved unpublished summaries are separate from publication eligibility. History does not carry authoritative eligibility or reconciliation outcomes: rows label these unavailable and offer Check publication / reconciliation in the existing run, never a ready-to-post count. Successful drafts offer Continue review; failed, active and other read-only runs offer Open execution. Opening any target is navigation only.

Search, manual expansions, bounded loaded pages, retained snapshots and window scroll live in a circuit-scoped presentation state. Returning reuses that window and refreshes its loaded pages; a new circuit starts fresh. Nothing is persisted in browser storage. Stale/partial source metadata and retained-history warnings remain visible. Ctrl/Cmd+K and the discoverable help entry reuse the route-owned command module. Up/Down and Home/End move through visible native controls; Left/Right expand/collapse groups, with native Enter/Space activation and Tab alternatives. Draft PR search and explicitly confirmed advanced automatic publication retain their existing paths.

Component and isolated JS tests verify navigation, circuit return, command guards and scroll wiring. No browser or app startup was authorized for T08; real focus return, browser history/scroll timing, theme contrast and desktop/tablet/phone reflow remain release-verification gaps. No visual approval is claimed.

## Stage execution inspector (reference)

Execution offers a timeline and a semantic duration table over the same recorded stages. Bars align to the measured run start; text exposes start offset and observed duration in milliseconds in either view. Queue wait is separate and available only with valid enqueue/start timestamps. Missing, nonfinite or negative durations remain unavailable; interrupted/active stages and invalid timing are labeled partial. Planned stages never stand in for execution evidence, including gate-skipped and legacy runs.

A native order control offers chronological, slowest-first and fastest-first order, with stable ties and missing measurements last. Stage buttons use roving focus: Up/Down and Home/End select within the execution surface. Previous/next failed-stage buttons wrap chronologically and focus the selected stage. The shared inspector shows safe outcome, timestamps, measured timing and allowlisted failure code. Run/trace copy is explicit, with polite success or safe manual-copy feedback; listeners belong to the execution root and are disposed. No detailed call data is invented or collected by this surface.

The inspector stacks below the stage view at narrower widths; both views retain text independent of bar color. Component and isolated JS tests cover timing, state, sorting, navigation and copy/disposal. App/browser startup was not authorized for T09: actual timeline geometry, keyboard focus/announcements, desktop/tablet/phone reflow and both-theme contrast still require release verification. No visual approval is claimed.

## Alternate board and post-run explorer (reference)

Queue remains default. Board columns use the same filtered findings, selection, focused evidence,
versioned commands and required-reason composer. Native Move to and the phone column picker are
alternatives to drag-and-drop; a move acts on one card, not the selected batch. Placement waits
for authoritative confirmation, and neither moving nor approving publishes or retracts threads.

The terminal-only profiler uses timeline/table and one read-only inspector over at most 50 mixed
logical/attempt/provider/tool records. Filters and safe-metadata search are page-scoped; paging
replaces rows, while a pinned selection may remain outside that page until replaced or expired.
Loaded parent/child navigation never fetches missing records automatically. Retained run/stage
summaries are independent of the page. Usage belongs to physical provider calls with explicit
coverage, overlapping durations are not additive, and absent costs/hidden SDK retries remain
unavailable. Stage and profiler keyboard scopes are separate from finding decisions. No profile
payload or investigation state is persisted in browser storage.

These are source/component/module-tested contracts, not rendered approval. Mechanical detector
results do not establish theme contrast, native focus/drag behavior, assistive-technology usability,
responsive layout or browser restoration timing. Those release checks remain open.

## Do's and Don'ts

- Do keep focus, selection, validation, human decisions and publication distinguishable.
- Do use semantic tokens for every state in both themes.
- Do label uncertain data and preserve existing security and safe HTTP boundaries.
- Don't add decorative metric cards, remote fonts or a replacement component framework.
- Don't store credentials, findings, reasons or selections in browser persistence.
- Don't treat bUnit/JS tests as proof of real-browser contrast, focus, reflow or first-paint behavior.
