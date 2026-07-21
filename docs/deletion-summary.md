# Documentation deletion and archive summary

**Purpose:** record what was replaced or retained. **Audience:** maintainers and reviewers. **Mode:** reference.

## Deleted or replaced

The new `README.md` and `AGENTS.md` were created from the current implementation. The prior `CHANGELOG.md` was restored because the release guide references it and it contains unreleased change history. Current-state documentation under `docs/` was regenerated; historical decision records were retained under `docs/archive/`.

Repository cache documentation such as `.pytest_cache/README.md` was not treated as product documentation and was left untouched. OpenSpec proposals, designs, tasks, prompts, and standards were not deleted because they are implementation-adjacent source artifacts rather than current repository docs.

## Archived and retained

The historical records in `docs/archive/` remain tracked: ADO integration triage, production workflow, Python ADO migration, semantic-diff planning, and the archive index. They are explicitly historical and are not authoritative for current behavior.

The deleted design, plan, idea, and HTML handoff files were not restored wholesale. Their still-supported operator, scheduling, posting, and compatibility contracts were moved into the current guides and reference pages.
