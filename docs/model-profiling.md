# Model-call profiling

Reference for operators and maintainers inspecting a terminal run in **Execution → Load model calls**.

## Capture and privacy

New Review, ReviewDraft and Resolve executions collect bounded metadata in a per-run Core collector. Execution-context-local immutable scopes carry the collector, stable stage name and agent profile. Shared transports hold no current-run fields. The physical-call wrapper sits inside the process-wide governor and below application retries, task-done guards and function invocation. Synthetic task-done responses do not count.

| Entry path | Capture boundary | Profile |
| --- | --- | --- |
| Review and draft review agent | OpenAI Responses adapter, or underlying Codex stream | Review |
| Separate finding verifier, including its format-repair request | Same provider boundary | Verifier |
| Author-commanded fix pass | Same provider boundary | Fix |
| Resolve comment triage | Same provider boundary | Triage |
| Resolve bounded edits | Same provider boundary | Fix |

Codex nonstreaming callers are adapted to one observed provider stream, not recorded twice. Streaming usage retains the last cumulative usage snapshot instead of summing repeated cumulative counts. Input, output, total, cached-input and reasoning token counts are nullable. Negative provider counts are unavailable. Response model IDs and arbitrary configured model names are not trusted: only explicitly supported provider/model identifiers are retained; others are null.

Rows contain correlation IDs, sequence, fixed stage/profile, allowlisted provider/model, UTC timing, monotonic elapsed milliseconds and closed outcome codes. Prompts, responses, source, paths, tool arguments/results, exception messages, credentials, endpoints, raw SDK representations and additional-property bags are not copied into profiles. Existing diagnostics have their own contract; profiling does not change their content.

The current OpenAI/Codex Responses adapters expose usage but no explicit provider-reported monetary cost field. Cost, currency and cost provenance therefore remain null, with `provider-not-reported` coverage. No configured pricing, estimates, subscription billing assumptions or mixed-currency totals are produced. SDK-internal retries are unobservable and marked `sdk-internal-unavailable`. Parent-call/attempt numbers remain null; correlated tool and retry detail belongs to a later slice. Governor waiting is outside the measured provider boundary; no separate governor-wait measurement is claimed.

## Storage and lifecycle

`IModelProfileStore` is the Core IO boundary. SQLite creates additive `ModelProfiles` summary rows and separate `ModelCalls` detail rows with unique call IDs and indexed `(RunId, Sequence)` paging. Existing run, finding, decision, publication and stage-diagnostic tables are not repurposed. Legacy runs are not backfilled.

After a successful gate, an incomplete marker is written without creating finding-store run history. Skipped gates write no profile marker or finding history. A process interruption leaves this marker incomplete; no missing calls are reconstructed. A reclaimed request with a finalized profile and a terminal stored run is acknowledged without rebuilding or executing its pipeline. The worker restores Completed/Failed from that stored run, even when profile details are empty, expired or incomplete. This closes the crash window between profile finalization and queue acknowledgment without replacing or merging executions. A request with only an unfinalized marker still resumes under the persisted profile identity with incomplete coverage; only newly observed calls contribute to its counts. In-memory detail is flushed only during terminal finalization, using a fresh five-second cancellation token. Call outcomes and counts represent observations, not inferred physical SDK attempts. Run and allowlisted-stage aggregates retain observed calls, separate completed/failed/cancelled/interrupted/provider-incomplete counts, observed duration, and usage summaries independently of detail rows, including calls beyond the detail cap. Each token metric contains a nullable sum, reporting-call count, observed-call count and unavailable/partial/complete coverage. Partial sums are not full-run totals; reported zero remains zero, and missing usage remains null. Provider-incomplete is a fully observed provider outcome (for example, an output limit), not evidence of missing capture. Overlapping call durations are not wall-clock run time. Older run-only summary JSON remains readable with stage and usage summaries unavailable; details are never used to reconstruct them.

A reclaim-preflight read failure or shutdown cancellation is not a new execution failure: the worker preserves existing run/profile records, leaves the durable request unacknowledged, releases its PR claim, and propagates the error. Non-cancellation errors therefore surface through the hosted worker rather than silently retrying or rewriting completed history; durable queue lease recovery can retry the preflight after service recovery.

Successful profile-write failures fail the run. Failure finalization logs persistence failures without replacing the original failure, following the existing worker failure-journal behavior. Writes and cleanup are awaited; no detached writes or fallback store are used. A cleanup failure propagates to the host as a failed hosted service.

| Setting | Default | Allowed range |
| --- | --- | --- |
| `ModelProfiling:RetentionDays` | 30 | 1–3650 |
| `ModelProfiling:DetailLimit` | 10000 | 1–10000 |
| `ModelProfiling:CleanupBatchSize` | 10 profiles | 1–100 |
| `ModelProfiling:CleanupIntervalMinutes` | 60 | 1–1440 |

Options validate at startup. Retention starts at terminal completion. The collector has an independent hard maximum of 10,000 details. Cleanup runs at startup and periodically, expires a bounded batch idempotently, deletes only detail rows, and preserves profile aggregates and existing stage/decision/publication audit. Reads enforce expiration even before cleanup has deleted the rows. Duplicate terminal finalization for the same profile does not rewrite its completion, expiry or call IDs.

## Terminal reads

`GET /reviews/{runId}/model-calls?after=0&count=50` uses the existing API-key or dashboard-session authentication and status rate limit. `after` is a nonnegative sequence cursor; `count` is 1–100. Follow `nextSequence` for another page. The same-origin browser adapter accepts only this exact route/query shape. Reads do not mutate decisions or publish findings, and do not grant edit capabilities to ordinary/resolve runs.

States distinguish active/not-yet-available, skipped-not-recorded, legacy-unavailable, expired, truncated, incomplete and observed-empty profiles. Active reads never query detail storage. Failed terminal runs can expose captured completed, failed, cancelled, interrupted and provider-incomplete calls. The basic retained stage-summary table remains available after detail expiry, including reporting coverage on usage sums. Tables are loaded explicitly after termination, not streamed live.

## Verification limits

Disposable SQLite fixtures cover additive upgrades, restart, duplicate finalization, paging and expiry without touching a user database. Collector/provider contract tests cover concurrent scopes, cancellation, privacy, cumulative streaming usage and actual OpenAI/Codex adapter boundaries. A collector-only measurement is recorded in the task verification logs; it is not a provider-latency or whole-service performance claim. App/browser startup is not authorized for this implementation; desktop/tablet/phone rendering, real focus and theme contrast remain release-verification work.
