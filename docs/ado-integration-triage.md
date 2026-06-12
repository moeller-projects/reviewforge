# Azure DevOps integration triage

## Purpose

This document evaluates whether the reviewer should keep `scripts/post-findings.mjs` with its direct dependency on `@modelcontextprotocol/sdk` and the Azure DevOps MCP server, or replace posting and voting with direct Azure DevOps REST API calls.

## Current state

### PR/work item/context fetching

Most PR context fetching is already implemented directly against the Azure DevOps REST API in `scripts/review.sh`.

Current direct API usage includes:

- resolving source and target branches when not passed explicitly,
- fetching PR details,
- fetching linked work items,
- fetching work item comments,
- fetching existing PR threads,
- cloning/fetching source and target refs through Git.

The relevant helpers are:

```bash
ado_get()
ado_post_json()
```

These use:

```bash
curl
jq
ADO_MCP_AUTH_TOKEN
```

Despite the token name, these calls are direct Azure DevOps REST calls and do not require MCP.

### Finding posting and voting

Posting is currently handled by `scripts/post-findings.mjs`.

It depends on:

- `@modelcontextprotocol/sdk`,
- `npx -y @azure-devops/mcp`,
- MCP tool discovery,
- MCP tool schemas for argument selection.

Current MCP-backed actions:

- list pull request threads for deduplication,
- create pull request thread,
- vote `waiting for author`.

## Recommendation

Replace MCP-based posting/voting with direct Azure DevOps REST API calls.

Keep the higher-level posting logic, but remove the MCP transport dependency.

Recommended direction:

```text
scripts/post-findings.mjs
  keep: validation, hashing, comment formatting, severity thresholds
  replace: MCP client calls with fetch/curl-based Azure DevOps REST calls
```

Alternatively, the posting layer can be rewritten as Bash, but a small Node script is still preferable for JSON validation, hashing, truncation, escaping, and payload construction.

## Why replace MCP here?

### 1. Production reliability

MCP adds an extra runtime layer:

```text
post-findings.mjs -> MCP SDK -> npx @azure-devops/mcp -> ADO REST API
```

For production automation, this introduces failure modes that are not directly related to Azure DevOps:

- `npx` package download problems,
- MCP package version drift,
- tool name changes,
- tool schema changes,
- transport startup failures,
- slower cold starts,
- harder debugging.

Direct REST is simpler:

```text
post-findings.mjs -> ADO REST API
```

### 2. The rest of the reviewer already uses REST

`review.sh` already uses direct Azure DevOps API calls for PR metadata, work items, work item comments, and PR threads.

Keeping only posting/voting on MCP makes the integration inconsistent.

### 3. The required API surface is small

The posting layer needs only three Azure DevOps operations:

1. list PR threads,
2. create PR thread/comment,
3. set reviewer vote.

That is not enough complexity to justify a full MCP dependency.

### 4. Better control over payloads

Direct REST gives exact control over:

- thread status,
- file path and line mapping,
- right-side diff positions,
- vote value,
- retry behavior,
- error handling,
- response logging.

MCP tool abstraction hides some of this and can make failures less transparent.

## Why not rewrite everything as Bash?

A pure Bash implementation is possible, but not recommended for the entire posting layer.

`post-findings.mjs` currently does useful structured work:

- JSON parsing and schema validation,
- severity ranking,
- stable finding keys,
- Markdown formatting,
- comment truncation,
- duplicate marker handling,
- fail/vote threshold calculation.

These are easier and safer in Node than Bash.

Recommended split:

```text
Bash: orchestration, Git, curl-friendly context collection
Node: finding validation, formatting, posting payload construction, REST calls
```

## Proposed target architecture

### Rename token variable

Current name:

```text
ADO_MCP_AUTH_TOKEN
```

Recommended name:

```text
ADO_AUTH_TOKEN
```

For backward compatibility, scripts can accept both:

```bash
ADO_AUTH_TOKEN="${ADO_AUTH_TOKEN:-${ADO_MCP_AUTH_TOKEN:-}}"
```

### Replace MCP calls in `post-findings.mjs`

Use Node's built-in `fetch` instead of MCP.

Required environment:

```text
ADO_ORG
ADO_PROJECT
ADO_REPO_ID
PR_ID
ADO_AUTH_TOKEN or ADO_MCP_AUTH_TOKEN
```

Base URL:

```text
https://dev.azure.com/{org}/{project}
```

### REST endpoints

#### List PR threads

```http
GET /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1
```

Purpose:

- dedupe findings by existing marker,
- avoid reposting already-posted findings.

#### Create PR thread

```http
POST /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/threads?api-version=7.1
Content-Type: application/json
```

General comment payload:

```json
{
  "comments": [
    {
      "parentCommentId": 0,
      "content": "comment body",
      "commentType": 1
    }
  ],
  "status": 1
}
```

File/line comment payload:

```json
{
  "comments": [
    {
      "parentCommentId": 0,
      "content": "comment body",
      "commentType": 1
    }
  ],
  "status": 1,
  "threadContext": {
    "filePath": "/src/example.ts",
    "rightFileStart": {
      "line": 42,
      "offset": 1
    },
    "rightFileEnd": {
      "line": 42,
      "offset": 1
    }
  }
}
```

#### Vote waiting for author

Azure DevOps reviewer vote values include:

```text
10   approved
5    approved with suggestions
0    no vote
-5   waiting for author
-10  rejected
```

Endpoint shape:

```http
PUT /_apis/git/repositories/{repositoryId}/pullRequests/{pullRequestId}/reviewers/{reviewerId}?api-version=7.1
Content-Type: application/json
```

Payload:

```json
{
  "vote": -5
}
```

Important: this requires the current authenticated user's reviewer ID. The posting script can determine it from PR reviewers by matching the authenticated user.

Options:

1. Fetch connection data and match by `authenticatedUser.id` or `uniqueName`.
2. Use PR reviewer matching if the token identity appears in `reviewers`.
3. If reviewer identity cannot be resolved, log an explicit vote failure.

Connection data endpoint:

```http
GET https://dev.azure.com/{org}/_apis/connectionData?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1
```

The Azure CLI extension had trouble with `7.1-preview.1`, but direct REST via `curl`/`fetch` can use it.

## Migration plan

### Step 1: Introduce REST client in `post-findings.mjs`

Add helpers:

```js
async function adoGet(path) {}
async function adoPost(path, body) {}
async function adoPut(path, body) {}
```

Use built-in `fetch` from Node 18+.

### Step 2: Replace thread listing

Replace MCP `repo_list_pull_request_threads` with direct `GET threads`.

Existing marker logic can stay unchanged:

```text
<sub>prb:{key}</sub>
```

### Step 3: Replace thread creation

Replace MCP `repo_create_pull_request_thread` with direct `POST threads`.

Keep existing formatting from `commentBody()`.

### Step 4: Replace voting

Replace MCP `repo_vote_pull_request` with direct reviewer vote API.

Add explicit logging:

- no threshold met,
- threshold met but reviewer identity not found,
- vote succeeded,
- vote failed with HTTP status/body.

### Step 5: Remove MCP dependency

Remove from `package.json` if no longer used:

```json
"@modelcontextprotocol/sdk": "..."
```

Update package description to remove MCP reference.

### Step 6: Rename token variables

Move toward:

```text
ADO_AUTH_TOKEN
```

Keep compatibility with:

```text
ADO_MCP_AUTH_TOKEN
```

### Step 7: Add tests

Add Node tests for:

- validation,
- finding key stability,
- comment body formatting/truncation,
- thread payload construction,
- vote threshold behavior,
- reviewer identity matching.

## Decision

Use direct Azure DevOps REST API for posting, deduplication, and voting.

Keep `scripts/post-findings.mjs` as a Node script for structured JSON handling, but remove direct MCP dependency from it.

## Summary

| Area | Current | Recommended |
| --- | --- | --- |
| PR details | Direct REST in `review.sh` | Keep direct REST |
| Work item details | Direct REST in `review.sh` | Keep direct REST |
| Work item comments | Direct REST in `review.sh` | Keep direct REST |
| Existing PR threads for context | Direct REST in `review.sh` | Keep direct REST |
| Existing PR threads for dedupe | MCP in `post-findings.mjs` | Replace with direct REST |
| Create PR comments | MCP in `post-findings.mjs` | Replace with direct REST |
| Vote waiting for author | MCP in `post-findings.mjs` | Replace with direct REST |
| JSON validation/formatting | Node | Keep Node |
| MCP dependency | Required for posting | Remove |
