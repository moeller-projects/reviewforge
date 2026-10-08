# Inspection warnings analysis (ReSharper/Rider dump, 2026-10-07)

~260 warnings across the solution. Four classes: real issues (§1), verified false positives (§2),
style/convention (§3), cognitive complexity (§4). Representative sites verified against source.

## 1. Real issues — fix first

| Site | Verdict | Fix |
|---|---|---|
| `Reasoning/SymbolUsageEnricher.cs:45` | `"function"` duplicated in `Keywords` HashSet (also line 39). Harmless but dead. | Delete duplicate. |
| `Pipeline/Stages/BeginRunStage.cs:17` | `_Clock` assigned, never read — run row uses `ctx.StartedAt`. Dead ctor param. | Drop `clock` param + DI arg, or use it for `StartedAt`. |
| `Infrastructure/Ado/AdoPullRequestSource.cs:41` | `_Project` assigned, never used — project travels inside the `VssConnection` URL. | Delete field + assignment. |
| `Pipeline/Stages/AutoFixFindingsStage.cs:433` | `snapshotLines = _LineReader(abs)` never used — wasted file read per commanded fix. | Delete. |
| `Pipeline/Stages/AutoFixFindingsStage.cs:154,531 / 244` | Unused `repoDir` params (`RunDeterministicPassAsync`, `RevertFile`); unused `path` local. | Remove params, update call sites. |
| `Service/Program.cs:22–23` | Dead locals. Traces/metrics gating lives in `ServiceCollectionExtensions.Telemetry.cs:19,25`; only `otlpLogsEnabled` is consumed here. | Delete both lines. |
| `Analysis/ReferenceScanner.cs:9` | `identifier is null` on non-nullable `string` param is always false. | Drop null check (or widen annotation if callers pass null). |
| `Reasoning/VerdictParser.cs:63–65` | Setters never used. | Make `get`-only/`init` or positional record. |
| `Pipeline/CommentFormatter.cs:20` | `Line` return value never consumed. | Return `void` or delete. |
| `Service/Pipelines/StageCatalog.cs:32` | Field assigned-then-read in one scope only. | Convert to local. |
| `Reasoning/ReviewTools.cs:78–82`, `HashLineEditor:43/76/292`, `PromptBuilder:99`, `ContextStore:18`, `ResolvePromptBuilder:53` | Annotation mismatch: `?? string.Empty` on non-nullable params. `RecordFinding` is an LLM tool boundary — the model can send JSON nulls, so the defense is intentional. | Make parameters `string?` so annotation matches defense — do NOT delete the `??`. |
| `CommitFixesStage:81`, `NativeReviewAgent:335`, `VerdictParser:49` | Always-true checks / never-null `?.` per annotations. | **Fixed** — simplified. |
| `ServiceCollectionExtensions.Options:23,91` | `?.`/`is not null` look always-true per annotations, but this is a config-binding boundary: the binder can leave `OrgUrl` null or a `VerifyCommand` element null despite the non-nullable annotation, and a validator NRE would replace a clean `OptionsValidationException` with a crash (`OptionsValidationTests`, `ServiceTests:933` depend on this). | **Kept deliberately** — defense at the binding boundary. |
| `CodexHttpDebugHandler:56` | ReSharper claims the `?.` is never-null, but a net10.0 compiler probe shows `HttpResponseMessage.Content` and `HttpContentHeaders.ContentType` are both nullable-annotated (CS8600 fires). | **Kept** — ReSharper external-annotation false positive. |
| `Persistence/SqliteReviewQueue.cs:157` | `reclaimed = false` initializer never read (reassigned at 173 on every consuming path). | Restructure declaration. |
| `AdoPullRequestSource:94/151/159` | Redundant `ToString()`. | Delete calls. |

## 2. Verified false positives — do not fix

- **"Captured variable is disposed in the outer scope"** — `PublishFindingsStage:132/167/175/203`,
  `TriageThreadsStage:68`, `CheckoutEvictionExecutor:67/149/227/281`, `SqliteFindingStore:394`, test sites.
  Both gate patterns checked: the `SemaphoreSlim` is awaited via `Task.WhenAll` *inside* the `using` scope
  before disposal. Safe. `publishedFixCount` "modified in outer scope": every write is `Interlocked.Increment`. Safe.
- **`SqliteReviewQueue.cs:116` "Iterator never returns"** — false; `yield return claimed` at line 110.
- **Cli `Program.cs:69/110`** — `using var` lowers to try/finally; disposal guaranteed even if the
  subsequent `DefaultRequestHeaders.Add` throws. Inspection misreads `using` declarations.
- **`DiscoveryService.cs:54`** — explicit name passed to `StartActivity` (parameter carries
  `[CallerMemberName]`) is deliberate: stable activity name wanted.
- **Short-lived `HttpClient` in `CodexHttpDebugHandlerTests`** — one instance per test; no socket-exhaustion risk. Same for the single-shot CLI.
- **Unused `instrument`/`state`/`tags`/`measurement` params in tests** — mandated by `Meter` listener callback signatures.
- **EF Core "unlimited string length" (25×)** — SQLite ignores `VARCHAR(n)`; store is SQLite-only. Informational.

## 3. Style/convention — DONE except namespaces

- **Namespace ≠ folder (~32 files) — OPEN DECISION, not done.** Verified: `AdoTelemetry.cs` sits in
  `Telemetry/` but declares `ReviewForge.Core.Pipeline`; every `ReviewForge.Service` file declares flat
  `ReviewForge.Service`; Cli `Program.cs` has no namespace. Uniform → intentional flat-namespace
  convention. Pick one: rename namespaces to match folders (churn), or suppress the inspection
  solution-wide (matches reality).
- **`_camelCase` → `_PascalCase` fields — DONE.** Renamed: `FetchOutcome._changedFileManifest/_changedFiles`,
  `RepoPreparation._overlapCts`, `ProcessRunner._remaining`, `AutoFixPublishTests._fetches`,
  `ResolveStageBoundaryTests._root`, `ResolvePipelineTests._repo` + `sourcePr`→`SourcePr`,
  `Fakes._openPullRequestsFetches/_workItemFetches/_threadFetches/_lastRunFetches`.
- **XML doc fixes — DONE.** `PriorRun`/`StoredFinding`: positional-param doc comments moved to `<param>`
  tags / plain comment. `ThreadTriage.Plan` + `ApiKeyGate.Evaluate`: missing `<param>` tags added.
  `Fakes.cs:33`: `paramref`→`see cref`. `AutoFixFindingsStage.cs:27`: unresolvable `paramref` → plain text.
  `PathSafety.cs:14`: cref disambiguated to `ResolveReal(string)`.
- **Redundancies — DONE.** Casts (`ClassifyRunStage:59`, `CollectCommentsStage:68`), qualifiers
  (`HomoglyphDetector:240`, `PrepareRepositoryStageTests`, `ChatClientFactoryTests`), default-value
  arguments (`RepoReadTools:396`, `SymbolUsageEnricher:117`, `Endpoints:54`, 8 test sites),
  redundant `!` suppressions (`LineEditEngine:199`, `ReviewWorker:100/108`, 11 test sites),
  redundant type argument (`GitOperationSchedulerTests:166`).

## 4. Cognitive complexity (~50 sites) — address opportunistically

Real maintainability debt; refactoring is behavior-risk work. Fix top-down when touching the file:

| Complexity | Location |
|---|---|
| 52 | `PublishFindingsStage` |
| 51 | `DiscoveryService.RunSweepAsync` |
| 49 | `LineEditEngine` |
| 41 / 40 / 44 | `SymbolUsageEnricher`, `DiffIndex`, `RepoReadTools` |
| 40 | `CheckoutEvictionExecutor` |
| 35 / 34 | `HomoglyphDiffAnalyzer`, `VerifyBuildStage` |

The ≤150% tail is borderline; extraction would cost more than it saves.

## Execution order

1. §1 — one `refactor` commit; all verified, no behavior change (except confirm `ReviewTools` annotation choice).
2. §3 — separate commit; decide the namespace question first.
3. §2 — suppress inspections where noise persists.
4. §4 — only when a stage is being modified anyway.

No live bugs found. Highest-risk items: unused `snapshotLines` read (wasted IO per commanded fix) and
the dead `BeginRunStage` clock (suggests unfinished intent — injectable clock for the run row, or drop the param).
