## Context

Bot threads are already identifiable via the stable `prb:<key>` marker (`ado.posting._thread_marker`), and `AdoClient.add_comment` already appends comments to existing threads (used by stale-comment reconciliation). The Pi runner reuses a per-PR session (`pr-<id>-review`), so a reply call in the same run resumes the review conversation with full context.

## Design

**Detection** is deterministic and needs no identity lookup: a thread awaits a reply iff it carries a bot marker, its status is not `closed`, and its last comment is not bot-authored. Bot-authored means the comment carries a marker or its author (by `id`, falling back to `uniqueName`/`displayName`) matches the author of the marker-carrying comment. This makes previously posted bot replies and stale-reconciliation comments count as bot-authored, so the bot never replies to itself and re-runs are idempotent without new state.

**Generation** is a single Pi `run_json` call per run (not per thread) with a new system prompt (`prompts/comment-reply.md`). Input lists each pending thread with its full conversation and anchor; the model may use its read-only tools against the prepared checkout. Output is validated immediately with a Pydantic `CommentReplies` schema; replies for unknown thread ids or with empty bodies are dropped before posting.

**Posting** reuses `AdoClient.add_comment`. Replies carry no `prb:` marker — the marker grammar stays per-finding and untouched; detection uses comment authorship instead. `DRY_RUN` records every reply with `posted: false`; the explicit `reply` command prints drafts, while automatic replies during `review --dry-run` stay in the artifact so findings stdout remains one JSON document. Live posting is best-effort per thread: a failed reply records `posted: false` and an error, while later replies continue.

**Wiring**: `ReplyToCommentsStage` appends to `DEFAULT_PIPELINE` after `PostToAdoStage` and is skipped when `reply_comments` is false. The `reply` subcommand runs `REPLY_PIPELINE` (fetch, prepare, reply) with `force_full_review` semantics so the checkout exists even when review-mode detection would no-op.

Prompt validation is pipeline-aware: review and explicit reply runs validate the reply prompt only when replies are enabled; review-only and post-only runs do not make the optional reply prompt a prerequisite.

The reply stage re-fetches threads at run time so it acts on current state rather than the possibly stale `threads.json` from run start.

## Risks

- A wrong or argumentative reply posts automatically. Mitigated by the prompt contract (concede when the human is right, keep it short) and the `--no-reply` / `REPLY_COMMENTS=0` kill switch.
- Replies arriving mid-run are only seen by the next run; acceptable for a batch tool.
