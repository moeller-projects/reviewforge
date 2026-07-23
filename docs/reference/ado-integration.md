# Azure DevOps integration

**Purpose:** document posting, formatting, and idempotency contracts. **Audience:** operators and maintainers. **Mode:** reference.

`AdoClient` uses Azure DevOps REST APIs for PR metadata, threads, commits, comments, voting, and generic GET/POST/PUT operations. The primary posting path maps file findings to diff lines when possible; fileless findings are PR-level comments.

## Comment formatting

Set `COMMENT_TEMPLATE_PATH` to use a Jinja2 Markdown template. The formatter exposes `title`, `message`, `severity`, `severity_label`, `confidence`, `context_basis`, `suggestion`, `file`, `line`, `key`, `marker`, `summary`, and `evidence`. Evidence includes `whyNewInThisPr`, `whyNotIntentional`, `contextFilesRead`, and `changedLines`.

Available filters are `join_list`, `fence`, and `fence_lang`. Markdown autoescaping is disabled. The default formatter remains active when `COMMENT_TEMPLATE_PATH` is unset; a missing configured template is an error.

Every rendered comment ends with exactly one canonical marker:

```text
<!-- prb:<dedupe-key> -->
```

New markers use the v2 key: normalized file and line plus a lowercase,
punctuation-free, whitespace-collapsed title. Severity and message are
excluded because they can drift between model runs. During the one-time
transition, posting recognizes both v1 markers (which included severity and
message) and v2 markers; new comments carry only the v2 marker.

Do not remove, rewrite, or relocate this marker. The `prb:<6-32 alnum key>`
contract accepts this HTML-comment form and its bare-line equivalent. Posting
scans existing bot threads for either key version; stale reconciliation, not
deduplication, handles comments whose line anchors move after a push.

## Other posting controls

`POST_MIN_SEVERITY`, `DROP_LOW_CONFIDENCE`, `REQUIRE_CONTEXT_FOR`, `MAX_FINDINGS`, `VOTE_WAITING_ON`, and `FAIL_ON` control filtering, voting, and exit behavior. The primary configuration default for `POST_MIN_SEVERITY` is `none`; the legacy helper `post-findings` defaults to `minor` when unset. See [configuration](configuration.md) and [artifacts](artifacts.md).
