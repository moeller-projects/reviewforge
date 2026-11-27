# ReviewForge

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

PR authors and reviewers use the dashboard to inspect automated reviews, decide which draft findings to keep, explicitly publish comments, and investigate execution failures.

## Product Purpose

Automated PR review and opt-in comment resolution, with human control over draft finding decisions and external publication. Success means completing review without losing edits or confusing pipeline validation, human decisions, and publication.

## Operating Context

The dashboard runs in Blazor Interactive Server behind an environment-configured shared-key session. Azure DevOps is the current PR host. Routes are `/dashboard`, `/dashboard/start`, and `/dashboard/runs/{runId}`. Search covers loaded history, not a server-wide archive. Typical reviews contain 10–50 findings, occasionally hundreds.

## Capabilities and Constraints

Existing review and resolve pipelines, private checkout safety, claims, head pins, audit versions and technical publication gates remain authoritative. Only successful completed draft runs permit decisions. Approval does not publish. Unknown outcomes are visible and never automatically retried. Shared-key sessions are not individual-user identity.

The confirmed workbench roadmap adds queue/evidence review, keyboard commands, bulk decisions, alternate Kanban and terminal-run metadata-only profiling. These are delivery goals, not claims that all slices already ship. Detailed profiling must exclude prompts, responses, source and unrestricted errors; costs are provider-reported only.

## Brand Commitments

Retain ReviewForge's name. Replace the old dashboard with a restrained, compact Linear-inspired workbench without copying branding. Use purpose-built native controls, not another component framework. Support light/dark/system with a remembered browser override and no remote fonts.

## Evidence on Hand

Repository domain models, existing dashboard actions and confirmed decisions D01–D20 in the dashboard productivity workbench plan. No commercial benchmarks or customer claims are supplied.

## Product Principles

- Keep pipeline validation, human decisions and publication distinct.
- Preserve context and drafts across reads; never replay mutations on reconnect.
- Make uncertain, partial and stale data explicit.
- Persist only presentation preferences in browser storage, never credentials or finding content.

## Accessibility & Inclusion

Target WCAG 2.2 AA. Desktop-first, usable on tablet and phone with touch alternatives. Standard keyboard controls by default; single-letter commands are opt-in. Real modals require native focus containment, Escape and focus return. Reduced motion is respected.
