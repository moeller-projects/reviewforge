# `docs/design/` — architecture decisions and future plans

These docs capture **why** the system is shaped the way it is, the rationale
behind specific design choices, and the plans that haven't been built yet.
They are the explanation-mode companion to [`../reference/`](../reference/).

| Doc | Covers |
| --- | --- |
| [`architecture.md`](architecture.md) | Top-level components, data flow on a single review run, and the invariants the system maintains. |
| [`work-item-verification-false-positives.md`](work-item-verification-false-positives.md) | Root-cause analysis: why work-item verification produces false positives in the current 12-stage pipeline (work item / thread context is fetched but never loaded into the in-memory stage context). Includes concrete false-positive vectors, evidence trail, and the smallest correct fix. |

A note on naming: this directory holds **rationale, design investigations,
and forward-looking plans** — not current behavior. Anything that describes
what the code does *today* belongs in [`../reference/`](../reference/).
Anything that describes a past migration, a triage decision, or notes that
are no longer current belongs in [`../archive/`](../archive/).
