## ADDED Requirements

### Requirement: Deterministic scope planning
ReviewForge MUST assign every changed file to exactly one primary review scope. Scope planning MUST enforce the configured byte budget as a hard boundary. When existing CRG output supplies impacted files, the planner MUST prioritize matching changed files ahead of non-impacted files deterministically; it MUST NOT require files with a shared flow or graph relationship to occupy the same scope.

#### Scenario: CRG prioritizes impacted files
- **WHEN** parseable changed-file diff sections include files listed in CRG `impacted_files`
- **THEN** the planner MUST pack impacted changed files before non-impacted changed files while preserving their source-diff order within each priority group.

#### Scenario: Byte budget boundary
- **WHEN** a file section or sequentially packed scope would exceed the configured budget
- **THEN** the planner MUST emit only scopes whose diff text is within that budget, truncate an oversized file section when necessary, and preserve complete changed-file coverage.

#### Scenario: CRG unavailable
- **WHEN** CRG is disabled, unavailable, malformed, or fails
- **THEN** the planner MUST fall back to deterministic sequential file-based budgeted scopes without failing the review solely because CRG is unavailable.

### Requirement: Isolated parallel review
ReviewForge MUST review independent scopes with isolated Pi sessions, bounded concurrency, deterministic result ordering, and a whole-PR synthesis after all scopes complete.

#### Scenario: Successful parallel review
- **WHEN** multiple scopes are planned and every scope returns valid output
- **THEN** ReviewForge MUST merge results in scope order and run one whole-PR synthesis.

#### Scenario: Scope failure
- **WHEN** any required scope fails or returns invalid output
- **THEN** the review MUST fail without publishing a partial postable review, while retaining scope-specific diagnostics.

### Requirement: Scope observability
ReviewForge MUST write an additive scope artifact containing the planner version, whether CRG was used, scope IDs, primary files, context files, graph signals, budgets, and truncation state.

#### Scenario: Scope artifact
- **WHEN** scope planning completes
- **THEN** the run artifacts MUST include deterministic scope ownership and planning diagnostics.

### Requirement: Scope-aware prompts
ReviewForge MUST identify each review scope and its primary files in the scope prompt and MUST provide cross-scope graph signals to whole-PR synthesis without changing the JSON output contract.

#### Scenario: Context-only files
- **WHEN** a file is supplied as graph context but is not a scope's primary file
- **THEN** the prompt MUST instruct the model not to emit duplicate findings for that context-only file.
