# Grafana deployment assets

- `alerts/reviewforge.yaml` — PrometheusRule: static floors + baseline-relative drift alerts.
- `dashboards/` — Grafana dashboards.

## Alert baseline semantics

`alerts/reviewforge.yaml` has two rule families:

- **Static floors** detect *broken* — conditions never acceptable regardless of history
  (queue rejecting submissions, run failure rate, `task_done` missing, claim expiry).
  They fire from day one.
- **Baseline-relative** rules detect *drift* — p95 run/stage latency, tokens per run, and
  queue depth divided by 14-day recording-rule baselines of this system's own normal
  (`reviewforge:*:baseline_avg14d`). Cold starts with no history stay silent instead of
  guessing: unlabeled alerts gate explicitly with `and on() (count(<baseline>) > 0)`,
  and the per-stage alert is gated implicitly — its `on(stage)` division drops any
  stage without a baseline. Static floors cover the never-acceptable cases in the
  meantime.

Ratio threshold is 3x baseline for 30m–1h. Tune per deployment after measuring the
false-positive rate on the low-noise channel.

## Storage caveat

The `reviewforge.queue.*` and store-backed panels assume the SQLite store in WAL mode
(the shipped default). WAL requires POSIX advisory locks — keep the store on a local
disk (the container volume is fine). Network filesystems must switch
`ReviewForge:Store:JournalMode` to `Delete`; see the main README's Configuration section.
