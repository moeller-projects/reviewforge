# Operator and scheduling workflows

**Purpose:** run ReviewForge container workflows and locate outputs. **Audience:** operators. **Mode:** how-to.

## Cross-platform entrypoints

Use Python on Linux, macOS, or Windows:

```bash
python -m reviewforge.ops build --dry-run
python -m reviewforge.ops run --dry-run --print-command --env-file .env \
  --pr-url https://dev.azure.com/example/project/_git/repo/pullrequest/1 \
  --ado-token placeholder
python -m reviewforge.ops run-open-prs --organization https://dev.azure.com/example/ \
  --projects project --target-branches main --dry-run
```

`build`, `run`, and `run-open-prs` choose Docker then Podman unless
`--runtime` is supplied. Explicit flags override environment variables, and
the chosen `--env-file` is passed directly to the container. `--print-command`
previews a single-review invocation without spawning a container.

`run-open-prs` keeps batch selection semantics: `--max-pull-requests` caps
the sorted matching set before review, and `--interactive` accepts `all`,
`none`, positional indexes, inclusive index ranges such as `1,3-5`, and
exact pull-request IDs. Comma-separated selectors may be combined; selections
are deduplicated and reviewed in displayed order.

`--keep-container` retains stopped review containers while keeping execution
detached. Without it, generated runs include `--rm -d`; with it, they include
`-d` without `--rm`.

## Status, failures, and restart behavior

Every stage writes an explicit outcome to `run.log`. Skipped stages include a
reason, including `no_op` review runs such as “No new commits since the
previous review.” The same reason is persisted in the stage entry in
`run-summary.json`; failures include the captured error there as well.

The ReviewForge process returns exit code `1` when a pipeline stage fails and
exit code `0` when all stages complete without failure. Configuration and
argument errors return `2`.

Container runs are detached (`-d`). Therefore `reviewforge.ops run` reports
whether Docker/Podman successfully started the container, not the eventual
application exit code inside a detached container. Inspect the container
status and the mounted `run.log`/`run-summary.json` for the review result.

The PowerShell wrappers default to `--restart on-failure:3`. A restart policy
retains the named container. On a later invocation, when the calculated
container name already exists, the launcher leaves a running container alone
and restarts a stopped container instead of attempting a duplicate `run`.
Override the policy with `-Restart` / `--restart`; pass an empty PowerShell
value or omit the option in Python to disable it. A restart policy cannot be
combined with `--rm`, so restart-enabled runs retain their containers.

## PowerShell compatibility

`build.ps1`, `run.ps1`, and `run-open-prs.ps1` now forward to the Python
entrypoints. They remain for existing Windows operators and scheduled tasks,
but new automation should invoke `python -m reviewforge.ops`.

`setup-open-prs-schedule.ps1` remains Windows Task Scheduler integration; it
continues to invoke the batch compatibility wrapper.

## Scheduled open-PR runs

`setup-open-prs-schedule.ps1` registers the task with the repository root as
its working directory and re-registers an existing task when run again. The
scheduled wrapper resolves Python deterministically: it uses `uv run
--project <repo>` when `uv` is available, otherwise it uses the repository's
synced `.venv` (`.venv/Scripts/python.exe` on Windows or `.venv/bin/python`
on POSIX). If neither exists, setup fails instead of falling back to a bare
`python`.

Scheduled credentials and discovery settings come from the `.env` file
referenced by `-EnvFile`; the wrapper loads it before each run. Never pass
`-AdoToken` to the scheduled task: tokens must not be stored in Task
Scheduler arguments or XML. Re-run `setup-open-prs-schedule.ps1` after
changing the task's `-EnvFile`, script path, or other registration settings;
it unregisters and re-registers the task.

## Artifacts and posting

Review output is written under `REVIEW_ARTIFACT_ROOT/pr-<PR_ID>/runs/<RUN_ID>/`. Read `run.log` there for the chronological, redacted container log for that run; `pr-<PR_ID>/latest.txt` identifies the latest run directory. Preserve `run-summary.json`, `review-result.json`, and `final-findings.json` when diagnosing or reposting. The container volume is already mounted by `run.ps1` and `run-open-prs.ps1`, so the same path is available to PowerShell operators. Do not edit the `prb:` deduplication marker in posted comment bodies; see [ADO integration](../reference/ado-integration.md).

## CRG graph cache

With `CRG_ENABLED=1`, the Tree-sitter knowledge graph persists across runs at `CRG_CACHE_DIR/<repo_id>/crg-<tool_version>/crg.db`. Container runs mount the dedicated named volume `reviewforge-crg-cache` (override with `REVIEW_CRG_CACHE_VOLUME_NAME`) at `/workspace/crg-cache` and set `CRG_CACHE_DIR` accordingly; local runs default to `REVIEW_ARTIFACT_ROOT/crg-cache`. The first run for a repository performs a full build (seconds to tens of seconds depending on repo size); subsequent runs apply an incremental update, typically under two seconds — watch for `CRG graph incremental build` vs `CRG graph full build` in `run.log`. Upgrading `code-review-graph` changes the version-keyed directory and costs exactly one cold rebuild. To force a cold rebuild manually, delete the repo's `crg-<version>` directory from the volume (`attach-volume.ps1` mounts the artifact volume for inspection; use `--volume reviewforge-crg-cache:/workspace/crg-cache` for the cache volume).


When `GRAPH_API_DIFF=1`, immutable base snapshots are cached under
`CRG_CACHE_DIR/<repo-slug>/base-snapshots/<base-sha>.json`. The snapshot is
built from a detached worktree and a separate temporary graph database; the
warm per-repository SQLite cache is not used for base data. The first run for
a new base SHA pays that disposable build; reruns reuse the JSON and log
`CRG base snapshot reused`. `GRAPH_FLOWS=1` and `GRAPH_ARCH=1` add only warm
Python-side analysis and degrade independently when optional graph data is
unavailable.

## Progressive context disclosure

There is nothing new to operate. `.reviewforge-context/` is created inside the disposable repository checkout, refreshed during the run, and removed with that checkout. It is not an artifact-volume cache or an operator-managed directory.

## Non-root container runtime (uid 10001)

The review container runs as user `review` (uid 10001), not root. Named
volumes (`reviewforge-artifacts`, `reviewforge-crg-cache`) work out of the
box — Docker initializes them with the image's directory ownership.

If you bind-mount **host directories** instead (e.g. `--artifact-path`), the
host directory must be writable by uid 10001:

    sudo chown -R 10001:10001 /path/to/artifacts

A permission-denied error on the first artifact write is the signature of a
host directory still owned by another uid.

Build requirements: the Dockerfile uses BuildKit cache/bind mounts. Docker
>= 23.0 (default BuildKit) or podman >= 4.0 with `BUILDAH_FORMAT=docker` is
required; `python -m reviewforge.ops build` checks this and fails with a clear message.