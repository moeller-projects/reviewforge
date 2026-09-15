## ADDED Requirements

### Requirement: ADO PR file scope

The pipeline MUST fetch the changed-file list of the pull request's latest iteration from Azure DevOps and restrict the prepared review diff to those files. The fetch MUST be best-effort: when the list is unavailable, empty, or excludes every computed diff file, the pipeline MUST keep the locally computed diff and continue.

#### Scenario: Local diff contains files outside the PR

- **WHEN** the locally computed review diff contains files that ADO does not list as changed in the pull request
- **THEN** the review diff and changed-files artifact MUST be restricted to the ADO-listed files and the exclusion count MUST appear in the prepare stage details

#### Scenario: ADO file list unavailable

- **WHEN** the iteration fetch fails or returns no files
- **THEN** the pipeline MUST keep the locally computed diff and continue the review

#### Scenario: ADO file list excludes everything

- **WHEN** the intersection of the computed diff files and the ADO file list is empty
- **THEN** the pipeline MUST keep the locally computed diff and log a warning

### Requirement: Out-of-scope finding suppression

The pipeline MUST NOT post file-level comments on files that ADO does not list as changed in the pull request. Anchor validation MUST drop such findings, and the posting boundary MUST skip any that remain with reason `not_in_pr_changes`. Findings without a file and work-item findings MUST be unaffected. When the ADO file list is unavailable, posting MUST proceed without the allowlist.

#### Scenario: Finding on a foreign file

- **WHEN** a finding targets a file outside the ADO PR changed-file list
- **THEN** anchor validation MUST drop it with an `out_of_scope` count and a discarded canonical finding

#### Scenario: Foreign finding reaches posting

- **WHEN** a finding on a file outside the ADO PR changed-file list reaches the posting boundary
- **THEN** posting MUST skip it, increment `skipped_reasons.not_in_pr_changes`, and create no thread

#### Scenario: General and work-item findings

- **WHEN** a finding has no file or is a work-item finding
- **THEN** the changed-file allowlist MUST NOT suppress it

#### Scenario: Allowlist unavailable at posting

- **WHEN** the ADO changed-file list cannot be fetched during posting
- **THEN** posting MUST proceed without allowlist filtering

### Requirement: Review range fallback observability

The pipeline MUST record why follow-up range narrowing was refused in the prepare stage details so run summaries explain unexpected full-range reviews. When the current target tip is an ancestor of the source tip, the pipeline MUST use the current target tip as the follow-up base, even if older merge commits occur in the reviewed range.

#### Scenario: Current target was merged into source

- **WHEN** the follow-up range contains merge commits and the current target tip is an ancestor of the source commit
- **THEN** the pipeline MUST use `target_commit..source_commit` and MUST leave `range_fallback_reason` empty

#### Scenario: Ambiguous merge history

- **WHEN** the follow-up range contains merge commits and the current target tip is not an ancestor of the source commit
- **THEN** the pipeline MUST use the full merge-base range and MUST record a non-empty `range_fallback_reason`

#### Scenario: Merge guard check fails

- **WHEN** the follow-up merge check fails
- **THEN** the pipeline MUST use the full merge-base range and MUST record a non-empty `range_fallback_reason`
