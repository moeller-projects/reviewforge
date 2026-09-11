## ADDED Requirements

### Requirement: Deterministic scope planning
ReviewForge MUST assign every changed file to exactly one primary review scope. Scope planning MUST use existing CRG outputs when available and MUST enforce the configured byte/token budget as a hard boundary.

#### Scenario: CRG groups related files
- **WHEN** changed files share an affected flow or graph relationship and fit within the configured budget
- **THEN** the planner MUST place them in the same primary scope.

#### Scenario: Oversized connected scope
- **WHEN** a related file group exceeds the configured budget
- **THEN** the planner MUST split it deterministically while preserving complete changed-file coverage.

#### Scenario: CRG unavailable
- **WHEN** CRG is disabled, unavailable, malformed, or fails
- **THEN** the planner MUST fall back to deterministic file-based budgeted scopes without failing the review solely because CRG is unavailable.

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
