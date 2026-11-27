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

## Do's and Don'ts

- Do keep focus, selection, validation, human decisions and publication distinguishable.
- Do use semantic tokens for every state in both themes.
- Do label uncertain data and preserve existing security and safe HTTP boundaries.
- Don't add decorative metric cards, remote fonts or a replacement component framework.
- Don't store credentials, findings, reasons or selections in browser persistence.
- Don't treat bUnit/JS tests as proof of real-browser contrast, focus, reflow or first-paint behavior.
