Task: Improve the Python package layout

You are working in an existing automated PR reviewer repository.

Refactor the codebase toward a cleaner Python package layout without changing behavior.

Goal:
Move reusable application code out of ad-hoc script-style modules and into a proper package layout.

Target direction:

src/
  auto_pr_reviewer/
    __init__.py
    config.py
    cli.py
    ado/
    git/
    ai/
    pipeline/
    artifacts/
scripts/
  main.py

`scripts/main.py` should become a thin entrypoint that imports and calls the package CLI.

Important:
- Do not rewrite the whole repository.
- Do not break existing root-level PowerShell wrappers.
- Preserve current command behavior.
- Preserve Azure Pipelines compatibility.
- Keep imports clean and testable.
- Update tests/import paths as needed.
- Add minimal packaging config if needed.
- Existing tests must pass.

Before editing, inspect the current repo and produce a short migration plan.
Then implement incrementally.
