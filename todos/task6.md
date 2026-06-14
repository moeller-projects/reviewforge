Task: Add schema validation for model outputs

You are working in an existing automated PR reviewer repository.

Add strict schema validation for structured model outputs.

Outputs to validate may include:
- intent
- context plan
- context digest
- candidate findings
- verified findings
- severity-calibrated findings
- final review result

Goals:
- Define typed schemas using the project’s existing validation approach, or introduce `pydantic` / `jsonschema` if appropriate.
- Validate model responses immediately after parsing.
- Produce clear validation errors.
- Store invalid raw output safely for debugging if already supported.
- Add tests for valid output, missing fields, wrong types, and invalid enum values.
- Update prompts if needed to match schemas.

Constraints:
- Do not make prompts more verbose than necessary.
- Do not silently coerce dangerous invalid values.
- Do not log secrets.
- Existing tests must pass.

Before editing, inspect current JSON parsing and validation logic.
Then produce a schema plan and implement incrementally.
