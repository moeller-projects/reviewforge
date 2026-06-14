Task: Improve diff line mapping

You are working in an existing automated PR reviewer repository.

Improve mapping review findings to Azure DevOps diff/thread locations.

Goals:
- Add or improve a dedicated diff line mapper.
- Provide an API similar to:

map_file_line_to_diff_position(file_path, new_line) -> AdoThreadContext | None

- Support:
  - added lines
  - modified lines
  - deleted lines where possible
  - renamed files
  - multiple hunks
  - files with no trailing newline
  - binary/generated files fallback
- If exact line mapping is impossible, fall back gracefully to file-level comments or summary comments.
- Add tests with representative patch fixtures.
- Document limitations.

Constraints:
- Do not post comments to obviously wrong lines.
- Prefer no inline comment over an incorrect inline comment.
- Preserve existing ADO behavior where correct.
- Existing tests must pass.

Before editing, inspect current diff parsing and ADO thread creation.
Then produce a line-mapping test plan and implement incrementally.
