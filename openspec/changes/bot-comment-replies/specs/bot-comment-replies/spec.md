## ADDED Requirements

### Requirement: Detect unanswered human replies on bot threads

The system MUST identify bot-authored comment threads awaiting a reply using only existing thread data. A thread awaits a reply iff it carries a `prb:` bot marker, its status is not one of `fixed`, `wontFix`, `wontfix`, `byDesign`, or `closed` (case-insensitively), and its last comment is not bot-authored. A comment is bot-authored when it carries a bot marker or any normalized author key intersects with the keys of a thread marker-carrying comment; the keys MUST include every available author `id`, `uniqueName`, and `displayName`. Detection MUST NOT introduce new ADO endpoints or persisted state.

#### Scenario: Human disagrees with a finding

- **WHEN** a bot-marked thread's last comment is authored by a human
- **THEN** the thread is reported as awaiting a reply

#### Scenario: Bot already replied last

- **WHEN** the last comment on a bot-marked thread has a normalized `id`, `uniqueName`, or `displayName` matching any key of a marker-carrying bot comment
- **THEN** the thread is not reported as awaiting a reply

#### Scenario: Terminal or unmarked threads

- **WHEN** a thread has a terminal status of `fixed`, `wontFix`, `wontfix`, `byDesign`, or `closed`, or carries no bot marker
- **THEN** the thread is never reported as awaiting a reply

### Requirement: Generate and post in-thread replies

For each pending thread the system MUST generate a reply with the configured model runner, validate the model output immediately against the `CommentReplies` Pydantic schema, and drop replies targeting unknown thread ids, duplicate thread ids, or empty bodies. Validated replies MUST be posted as comments on their existing threads; no new threads are created and the `prb:` marker grammar is unchanged. Under `DRY_RUN` the system MUST record every accepted reply with `posted: false`; `reviewforge reply --dry-run` MUST print the validated replies, while an automatic `reviewforge review --dry-run` reply stage MUST record the replies only and MUST NOT print reply drafts.

#### Scenario: Replies posted to existing threads

- **WHEN** pending threads exist and posting is enabled
- **THEN** each validated reply is appended as a comment to its thread via the existing add-comment endpoint and recorded in the `comment-replies.json` artifact

#### Scenario: Explicit reply dry run prints drafts

- **WHEN** `reviewforge reply --dry-run` is invoked and pending threads exist
- **THEN** validated replies are printed and recorded with `posted: false`, and no ADO write occurs

#### Scenario: Automatic review dry run records artifacts only

- **WHEN** `reviewforge review --dry-run` runs the automatic reply stage with pending threads
- **THEN** validated replies are recorded with `posted: false` without an ADO write or printed reply drafts

#### Scenario: Invalid model output

- **WHEN** the model returns replies for unknown thread ids or empty bodies
- **THEN** those replies are discarded before posting and valid remaining replies still post

#### Scenario: One reply fails

- **WHEN** an ADO write fails for one accepted reply
- **THEN** that reply is recorded with `posted: false` and an error, later accepted replies are still attempted, and the artifact is written

### Requirement: Reply entry points

The system MUST provide a `reviewforge reply` subcommand that fetches PR context, prepares the repository, and processes pending replies without generating new findings. The `reviewforge review` posting pipeline MUST run the reply stage automatically after posting findings, and MUST skip it when `reply_comments` is disabled via `--no-reply` or `REPLY_COMMENTS=0`.

#### Scenario: Standalone reply run

- **WHEN** `reviewforge reply` is invoked for a PR with pending replies
- **THEN** replies are generated and posted without running the reasoning engine or posting findings

#### Scenario: Review auto-reply

- **WHEN** `reviewforge review` completes and human replies are pending
- **THEN** the reply stage answers them in the same run using the reused Pi session

#### Scenario: Reply disabled

- **WHEN** `--no-reply` or `REPLY_COMMENTS=0` is set
- **THEN** no reply detection, generation, or posting occurs
