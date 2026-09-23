# Seq saved searches for ReviewForge on-call

Seq ingests ReviewForge's OTLP logs; OBS-2 attaches per-run scope properties so every
log line carries `RunId`, `PrId`, `Org`, `Project`, `RepositoryId`, `HeadSha`, and
`Stage`. Bookmark these searches — `RunId = '...'` is the universal "show me this run"
entry point.

| Search | Seq filter | Purpose |
|---|---|---|
| One run, everything | `RunId = '...'` | Reconstruct an entire run from a status URL / alert context |
| One run, one stage | `RunId = '...' and Stage = 'execute-reasoning'` | Isolate the agent loop of a single run |
| Failed runs today | `@Level = 'Error' and @Timestamp > DateTimeNow().AddDays(-1)` | Today's failures across all runs |
| Slow stages | `Stage is not null and ElapsedMs > 5000` | Find stages over 5s |
| Findings rejected outside diff | message search `outside the current PR diff` | Validation rejections (anchor/scope) |
| ADO retries | message search `transient` or `retry` | ADO API retry activity |
| Agent ran out of turns | message search `iteration cap` or `task_done missing` | Agent hit the iteration cap without completing |
| Dedupe rejections | message search `finding deduped` | P0-1 tripwire — a follow-up run re-detecting a previously posted finding |
