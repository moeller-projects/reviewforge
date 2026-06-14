Task: Introduce explicit pipeline stages

You are working in an existing automated PR reviewer repository.

Refactor orchestration into explicit pipeline stages.

Target direction:

stages = [
    FetchPrMetadata(),
    PrepareRepository(),
    BuildArtifacts(),
    ReconstructIntent(),
    PlanContext(),
    CollectContext(),
    ReviewDiff(),
    VerifyFindings(),
    CalibrateSeverity(),
    PostToAdo(),
]

Each stage should expose a clear interface similar to:

stage.run(context) -> StageResult

Goals:
- Make the review flow easier to understand, test, and debug.
- Each stage should have a name, inputs, outputs, status, duration, and diagnostics.
- Keep existing behavior unchanged.
- Avoid a big-bang rewrite.
- Add tests for at least the stage runner and one or two representative stages.
- Update documentation if helpful.

Constraints:
- Do not move everything at once if risky.
- Preserve current prompts, artifact outputs, and ADO behavior.
- Azure Pipelines must continue working.
- Existing tests must pass.

Before editing, inspect the current pipeline/orchestration code and produce a staged migration plan.
Then implement the smallest safe slice.
