# Python Azure DevOps review integration migration

## Decision

Move Azure DevOps PR/work item fetching, PR comment posting, and reviewer vote updates into a Python CLI that talks directly to the Azure DevOps REST API.

Keep Bash for Git orchestration and Pi stage execution. Remove the MCP dependency from posting/voting once the Python path is complete.

## Why

The current implementation is split across:

- `scripts/review.sh` for direct REST context fetching,
- `scripts/post-findings.mjs` for MCP-backed posting/voting,
- `@modelcontextprotocol/sdk` and `@azure-devops/mcp` for final PR mutations.

This creates inconsistent integration behavior and extra runtime failure modes.

Python gives us:

- direct Azure DevOps REST calls,
- safer JSON handling than Bash,
- simpler structured validation than shell/JQ-only code,
- better tests and retry/error handling,
- no MCP process/tool schema dependency,
- one place for ADO-specific behavior.

## Target layout

```text
scripts/ado_review.py
```

Subcommands:

```bash
python /app/scripts/ado_review.py fetch-context \
  --org "$ADO_ORG" \
  --project "$ADO_PROJECT" \
  --repo "$ADO_REPO_ID" \
  --pr "$PR_ID" \
  --out "$ARTIFACT_DIR"

python /app/scripts/ado_review.py post-findings \
  --org "$ADO_ORG" \
  --project "$ADO_PROJECT" \
  --repo "$ADO_REPO_ID" \
  --pr "$PR_ID" \
  --findings "$FINAL_FINDINGS_FILE" \
  --out "$ARTIFACT_DIR/posted-findings.json"
```

## Token handling

Prefer:

```text
ADO_AUTH_TOKEN
```

Keep compatibility with:

```text
ADO_MCP_AUTH_TOKEN
```

Resolution order:

```python
token = os.getenv("ADO_AUTH_TOKEN") or os.getenv("ADO_MCP_AUTH_TOKEN")
```

## Fetch-context responsibilities

The Python script should write:

```text
metadata.json
work-items.json
work-item-comments.json
threads.json
context.json
```

`context.json` is optimized for the review prompts and should include:

```json
{
  "pr": {},
  "workItems": [],
  "workItemComments": [],
  "existingThreads": []
}
```

## Post-findings responsibilities

The Python script should:

1. read final findings JSON,
2. validate schema,
3. filter by `POST_MIN_SEVERITY`,
4. optionally drop low confidence findings with `DROP_LOW_CONFIDENCE`,
5. fetch current PR threads,
6. dedupe by marker,
7. create PR threads/comments,
8. set `waiting for author` vote when threshold is met,
9. write `posted-findings.json`.

## REST endpoints

### PR details

```http
GET /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}?api-version=7.1
```

### PR threads

```http
GET /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1
```

### Create PR thread

```http
POST /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1
```

### Work item batch

```http
POST /_apis/wit/workitemsbatch?api-version=7.1
```

### Work item comments

```http
GET /_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4
```

### Connection data

```http
GET https://dev.azure.com/{org}/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1
```

### Vote reviewer

```http
PUT /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{reviewerId}?api-version=7.1
Content-Type: application/json

{ "vote": -5 }
```

## Migration steps

1. Add `scripts/ado_review.py` with `fetch-context` and `post-findings`.
2. Wire `review.sh` to use `fetch-context` instead of inline PR/work item/thread fetching.
3. Wire `review.sh` to use `post-findings` instead of `post-findings.mjs`.
4. Keep `post-findings.mjs` temporarily until Python path is proven.
5. Remove MCP from `Dockerfile` and `package.json` once no longer used.
6. Add Python tests for validation, formatting, dedupe, vote threshold, and reviewer matching.

## Rollout policy

Use a reversible migration:

```text
ADO_REVIEW_BACKEND=python|legacy
```

Default to Python once stable. Keep legacy path short-term for rollback during validation.
