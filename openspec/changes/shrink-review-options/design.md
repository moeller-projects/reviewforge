# Design

## Context

See `proposal.md` for motivation. The existing configuration is split across review, repo-read-tools, enrichment, workspace, host, Git, and LLM settings. Review execution also has an optional sharding path. Deployment config includes TOML and environment-bound options.

## Goals / Non-Goals

**Goals:** Keep each setting under its owning section, preserve automatic concurrency defaults, make legacy-key migration visible, and remove the unused sharding path without altering the established single-pass truncation behavior.

**Non-Goals:** Preserve runtime behavior for removed keys, change review prompts or diff truncation semantics, remove finding verification or symbol usage, or change checkout eviction behavior.

## Decisions

- Move grep budgets and symbol-usage enablement into `ReviewOptions`; preserve `VerifyFindingsOptions` as a distinct capability.
- Keep checkout eviction settings nested under `WorkspaceOptions`. Its policy type remains in Core because `RepoCheckoutPool` consumes it; moving it to the Service configuration file would invert the Core/Service dependency.
- Resolve host worker count, Git scheduler concurrency, and LLM concurrency cap in options post-configuration so consumers observe final values. Register one worker hosted service that fans out queue-read loops using the resolved worker count.
- The service composition always passes the resolved scheduler to `LibGit2SharpGitOps`; retain its standalone optional-scheduler behavior to avoid an unrelated public constructor break.
- Keep a one-release warning path for detected `RepoReadTools:GrepMaxMs`, `RepoReadTools:GrepMaxLines`, and `Review:Sharding` keys. Warnings inform migration only; old values are not compatibility inputs.
- Remove the sharding planner, orchestration branch, sharding metrics, and associated tests together. The existing configured maximum-diff truncation path remains the sole behavior for oversized diffs.
- Supply stage diff limits from `ReviewOptions` at composition time; remove constructor argument defaults that duplicate configuration.

## Risks / Trade-offs

- Existing deployments that rely on moved keys must migrate; startup warnings reduce silent misconfiguration but do not preserve behavior.
- Sharding-specific tests and coverage are deleted with the unused path; the Core coverage threshold still applies to remaining production code.
- Removing a review execution path can affect long-diff outcomes; the intended outcome is one full-context truncated review rather than shard-local reviews. Existing reasoning and pipeline behavior must remain covered by the allowed validation script.
