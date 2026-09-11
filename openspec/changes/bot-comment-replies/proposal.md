## Why

When a PR author disagrees with a bot-posted finding, the conversation stalls: the bot never reads the reply and never responds. Humans must re-argue their case on every re-run, and valid pushback is invisible to the review loop.

## What Changes

- Detect bot-authored comment threads whose latest comment is a human reply (deterministic, marker- and author-based; no new ADO endpoints).
- Generate a reply per pending thread with the configured model runner, using the reused Pi session so the model sees its own prior review context.
- Post replies as comments on the existing threads via the existing `add_comment` client method; draft-only under `DRY_RUN`.
- Add a `reviewforge reply` subcommand that runs fetch + prepare + reply without producing new findings.
- Auto-run the reply stage at the end of `reviewforge review` (posting pipeline only), with a `--no-reply` / `REPLY_COMMENTS=0` opt-out.

## Capabilities

### New Capabilities
- `bot-comment-replies`: Detect unanswered human replies on bot threads and respond in-thread.

### Modified Capabilities
- None.
