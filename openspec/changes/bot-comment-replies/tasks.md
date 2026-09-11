## 1. Detection and Contracts

- [x] 1.1 Add `find_awaiting_replies` thread detection to `ado/posting.py`
- [x] 1.2 Add `CommentReplies` Pydantic schema and `comment-replies.json` artifact
- [x] 1.3 Add `prompts/comment-reply.md`, `comment_reply_prompt_path`, and `reply_comments` config

## 2. Stage and Wiring

- [x] 2.1 Implement `ReplyToCommentsStage` (detect, generate, validate, post/draft)
- [x] 2.2 Append the stage to `DEFAULT_PIPELINE` and add `REPLY_PIPELINE` + `run_reply_only`
- [x] 2.3 Add `reviewforge reply` subcommand and `--no-reply` opt-out

## 3. Verification

- [x] 3.1 Add detection, schema, stage, and CLI tests
- [x] 3.2 Run focused tests, full suite, and coverage gate
- [x] 3.3 Validate the OpenSpec change and update docs

Verification note: focused regression tests pass. The full suite retains five pre-existing `code_review_graph` import failures and one pre-existing Docker pin assertion. Coverage on the passing suite completed at 94%.
