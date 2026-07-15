# Work item verification — false positive root cause

> **Scope.** Investigation of why work-item verification produces false positives
> in the current 11-stage pipeline. Question: under what conditions does the
> work-item step in the review pipeline produce false positives, and is the
> pipeline's data flow actually capable of supporting what the prompts describe?
>
> **Method.** Read every stage in `src/reviewforge/pipeline/stages/` that
> consumes work-item / thread data; traced how the orchestrator populates
> `StageContext`; verified with `git grep` that no other code path fills the
> in-memory context; reviewed every prompt that depends on it.

---

## Root cause — work item / thread context is fetched but never loaded

The bot **does** fetch the data. The orchestrator just doesn't use it.

1. `FetchPrMetadataStage` (stage 1) calls the `ado_review` `fetch-context`
   helper (`src/reviewforge/ado/cli.py:370-371`) which writes **four**
   files into the run dir:
   - `metadata.json`
   - `work-items.json`
   - `work-item-comments.json`
   - `threads.json`

   Plus a combined `context.json`. All four are listed in `ARTIFACT_NAMES` and
   declared on the `Artifacts` dataclass
   (`src/reviewforge/ado/cli.py:394-396`;
   `src/reviewforge/artifacts/manager.py:18-36`).

2. **Nothing reads those files back into the in-memory `StageContext`.** This
   was verified exhaustively:

   ```text
   $ grep -nE 'wi_context\s*=|thread_context\s*=|wi_comments_context\s*=' src/ tests/
   (no matches — no writer anywhere in src/ or tests/)

   $ grep -nE 'extras\["wi_context"\]|extras\["thread_context"\]|extras\["wi_comments_context"\]' src/ tests/
   (no matches — no writer anywhere)
   ```

3. The orchestrator's `_make_stage_context`
   (`src/reviewforge/pipeline/orchestrator.py:121-148`) populates only
   `extras["paths"]` — the dict of file paths. It never reads the contents
   of `work-items.json`, `work-item-comments.json`, or `threads.json` into
   memory.

4. The test fixture in `tests/test_stages.py:148-150` hardcodes the empty
   lists, so the broken production behaviour matches the test contract:

   ```python
   ctx.extras["wi_context"] = []
   ctx.extras["wi_comments_context"] = []
   ctx.extras["thread_context"] = []
   ```

5. **Result:** every stage that reads `ctx.extras.get("wi_context", [])`,
   `ctx.extras.get("thread_context", [])`, or
   `ctx.extras.get("wi_comments_context", [])` gets an empty list. The
   affected stages are:

   | Stage | Reads |
   | --- | --- |
   | `ReconstructIntentStage` | `wi_context`, `thread_context` |
   | `PlanContextStage` | `wi_context`, `thread_context` |
   | `CollectContextStage` | (none — operates on the plan only) |
   | `ContextDigestStage` | `wi_context`, `thread_context` |
   | `ReviewDiffStage` | `wi_context`, `wi_comments_context`, `thread_context` |
   | `VerifyFindingsStage` | `wi_context`, `thread_context` |
   | `CalibrateSeverityStage` | `wi_context`, `thread_context` |

---

## What the prompts expect

The prompts all describe a verifier that uses work item data:

- **`prompts/review-system.md` — "Work item verification":**
  > "If a requirement from a work item is not addressed by the changes in the
  > diff, create a finding with: `file: null`, `line: null`, `severity: at least
  > 'major'` (use 'blocker' if the entire work item appears unaddressed)…
  > Work item comments often contain clarifications, refined requirements,
  > design decisions, or scope changes that amend the original description.
  > **Treat these comments as authoritative context** — they may narrow, expand,
  > or override the written acceptance criteria."

- **`prompts/verify-findings.md`:** "You receive candidate findings plus PR
  intent and context digest. **Defend the PR author.** Drop findings that
  are speculative, duplicate existing discussion, pre-existing, contradicted
  by context, or plausibly intentional."

- **`prompts/intent.md`:** "infer what the PR is trying to accomplish from
  metadata, **linked work items, existing PR discussion**, changed file list,
  and the supplied diff excerpt." Output includes a `requirements` field
  meant to mirror work item acceptance criteria.

- **`prompts/context-digest.md`:** Output includes
  `possible_intentional_choices` — "plausible reason the author made this
  change". The verify stage is supposed to use this to drop findings that
  are "plausibly intentional".

**All four sections assume the model has the work item data, the work item
comments, and the existing PR threads. None of that data is in the prompt,
and nothing in the model session forces the model to load it.**

---

## False positive vectors (concrete, not hypothetical)

| # | Vector | How it manifests | Why it is a false positive |
| --- | --- | --- | --- |
| V1 | "Work item requirement not addressed" — invented requirement | `review_diff` is told to verify against work item requirements and create `blocker`/`major` findings for unaddressed ones, but the model has no work item content. To satisfy the prompt contract it infers requirements from the PR title / description and "verifies" against those. The finding then carries confident `evidence` fields citing author intent that was never expressed. | Likelihood: **HIGH** under legacy mode (`pi_session_enabled=False`), where the prompt literally embeds `"Linked work items: []"`. |
| V2 | Duplicate an existing PR comment | `review-system.md` says "Do NOT create a finding that raises the same issue already discussed in an existing comment." With `thread_context = []`, the model cannot honour that rule. | Likelihood: **HIGH** in chunked reviews of large diffs. |
| V3 | Pre-existing bug mis-attributed to the PR | `review-system.md` says "Do NOT create findings against code that is not modified by this PR" and "Do NOT report unrelated pre-existing issues." But the work item may say "while in this area, also fix the upstream race in `foo.ts`" — that legitimises flagging a pre-existing issue in `foo.ts`. Without the work item, `foo.ts` looks out of scope, so the finding is a false positive. | Likelihood: **MEDIUM** — depends on the maintainers' style of writing work items. |
| V4 | Severity inflation / wrong severity | The review and verify prompts both ask the model to weigh severity by impact. Without the work item type, a `Spike` or `Onboarding` task gets the same treatment as a production bug. Findings end up `blocker` on what is actually an investigative deliverable. | Likelihood: **HIGH** for any PR whose work item is `Spike`, `Onboarding`, or `Refactor`. |
| V5 | Style / non-issues flagged because the standards file is the only "context" the model has | With `wi_context` empty, the model has standards + diff + (if it bothers to read) git history. It is heavily tempted to fill the empty page with style / preference findings ("consider using a constant", "could be extracted", "missing docstring"). The "Defend the PR author" stage then sees only "the standards file said so" and keeps them. | Likelihood: **HIGH** in repos with thin standards files (e.g. `standards/clean-code.md`). |
| V6 | "Missing test" false positive | Common pattern in work items: "verify via manual QA this iteration; add automated tests in follow-up." Without the comment, the model flags missing tests as a `minor` "test gap" finding. | Likelihood: **HIGH** — likely the most common work-item-driven false positive in the archive triage notes. |
| V7 | Comment thread becomes negotiation, not notification | The bot will re-open the same issue as a finding. The author then has to post "already discussed in thread #47" as a reply. That reply, in turn, becomes another item the model does not have access to, on the next run. | Likelihood: **MEDIUM** in long-lived PRs with >3 review iterations. |
| V8 | Evidence presented as fact in the comment | `comment.md.example` renders the evidence fields directly: `> **Why this is introduced by this PR:** {{ ev.whyNewInThisPr }}` / `> **Why this is unlikely to be intentional:** {{ ev.whyNotIntentional }}`. With no source data behind them, the model writes plausible-sounding rationales from inference. The human reviewer reads them as authoritative reasoning. The social pressure to "defend against the bot's evidence" amplifies the false positive. | Likelihood: **HIGH** — every false positive carries confident-looking evidence by default. |
| V9 | Work item data quality — not just the loader | The previous eight vectors are all about the **loader** (work item data is on disk but never read into the in-memory stage context). This vector is about the **data itself** — even with the loader fixed, the work item description is not a reliable source of truth. Stale descriptions, vague acceptance criteria, split implementations, evidence outside the diff, and parent / epic items all cause the model to invent coverage that is not actually missing. | Likelihood: **HIGH** for any team that writes work items the way the prompt assumes (precise, current, in-scope, code-verifiable). |

---

## Why session mode does not rescue this

`pi_session_enabled=True` (the default — `src/reviewforge/config.py:198,
231`) shrinks the per-stage prompt to a one-paragraph briefing that names the
on-disk files (`src/reviewforge/ai/prompts.py:112-137`,
`_briefing_session`). The model **can** in principle `read` `work-items.json`
on its own. In practice:

- No prompt in `prompts/` instructs the model to do so.
- The model has no signal that reading work items reduces its finding count
  or improves a score (there is no such score).
- The model is asked to "Defend the PR author" — i.e. drop findings — which
  is rewarded by doing less. The cheapest path is to not read work items.
- `prompts/verify-findings.md` does not even mention work items by name; it
  only says "PR intent and context digest" — both of which were produced by
  earlier stages that also lacked work items.

So the "session reads file" mitigation is theoretical, not actual.

---

## Why the test suite does not catch it

- `tests/test_stages.py:148-150` hardcodes empty lists in the fixture. It
  matches the broken production behaviour, so the contract is not tested.
- The only end-to-end test that touches work items
  (`tests/test_ado_review.py:743`, `test_fetch_work_items_with_refs`) tests
  the **fetch** helper, not the integration of that data into the review
  prompt.

---

## Beyond the loader — work item data quality risks (V9)

The loader bug above is necessary to fix, but **not sufficient**. Even with
the work item data loaded into the in-memory context, the data itself is a
risky basis for posting findings. The prompt rule that says
"missing work item requirements → `major`/`blocker` finding" is powerful,
but those findings are only as good as the work items they reference.

### The five data-quality risks

1. **Work item descriptions can be stale.** A description written at scoping
   time rarely matches the implementation that actually shipped. By the time
   the PR is reviewed, the description is a hypothesis, not a contract.

2. **Acceptance criteria can be vague.** "Improve performance" or "handle
   errors gracefully" cannot be cross-referenced against a diff. The model
   will treat them as a license to invent concrete sub-requirements that
   were never stated.

3. **Implementation may be split across PRs.** A single work item is often
   implemented as 2–5 PRs. The current PR is responsible for only its slice.
   The model sees a partial implementation and concludes the work item is
   unaddressed, when in fact it is just split.

4. **Evidence may live outside the diff.** A requirement that says
   "the new endpoint must be added to the load balancer" is satisfied in
   another repo, an IaC change, or a runbook — not in the diff under review.
   The model cannot see those.

5. **Linked work items may be parent / epic items, not exact requirements.**
   A PR is often linked to a parent Feature or Epic whose acceptance
   criteria span many child tasks. The model reads the Epic's criteria
   against a single PR's diff and concludes the Feature is unaddressed.

### Existing prompt guardrails (good, not enough)

`prompts/review-system.md` already includes the following guardrails, and
they are worth preserving verbatim in any rewrite:

> - *Do NOT create a finding for requirements that are partially implemented
  >   — only for clearly missing ones. A partial implementation is not the
  >   same as a missing one.*
> - *Do NOT create a finding for requirements that are outside the scope of
  >   code review (e.g., manual testing steps, deployment verification).*
> - *Work item comments often contain clarifications, refined requirements,
  >   design decisions, or scope changes that amend the original description.
  >   Treat these comments as authoritative context — they may narrow, expand,
  >   or override the written acceptance criteria.*

These three rules already deflect several of the five risks above — in
particular (3) split implementations, (4) out-of-diff evidence, and (5)
parent/epic items. The rules are not testable end-to-end (the verifier has
no way to prove it applied them), but the wording is sound.

What the rules **do not** address:

- **Risk 1 (stale description).** The model has no signal that the work
  item was last edited two sprints ago.
- **Risk 2 (vague AC).** The model has no threshold for "this acceptance
  criterion is too vague to verify against a diff". Vague criteria end up
  inflated into concrete sub-requirements the model invents.
- **Risk 5 (parent / epic).** The rules mention scope, but do not
  distinguish a child task from a parent epic. A linked Epic is a strong
  signal that the criteria are not PR-scoped, but the model has no way to
  use that signal.

### Recommended additional mitigation: post work item findings as general PR comments

Work item findings are a categorically different kind of finding from
code-level findings:

- They are **not anchored to a file or line.** The prompt already says
  `file: null, line: null`. Anchoring them to a guessed file (V1 above) is
  one of the strongest false-positive vectors.
- They **cannot be verified by reading the diff.** They require reading the
  work item history, comments, and related PRs — which the model may not
  have done.
- They **have a different audience.** The author needs to decide whether
  the work item is in scope, split, or stale — a judgement that belongs to
  a human reviewer of the PR conversation, not to an inline file comment.

The current posting path already does the right thing **when the model
follows the prompt and sets `file: null`** — it posts as a PR-level
general comment with no `threadContext`
(`src/reviewforge/ado/cli.py:514-535`). But two things go wrong in
practice:

1. **The model often "helps" by filling in a guessed file / line.** When
   that happens, the posting path tries to map the file to a diff position.
   If mapping fails, the finding is **silently dropped** rather than being
   posted as a general comment
   (`src/reviewforge/ado/cli.py:541-545`).
2. **There is no explicit rule in the code that distinguishes work item
   findings from code findings.** The system relies on the model following
   the prompt's `file: null, line: null` instruction, and a single bad
   model call produces either a wrong-file inline comment or a silent skip.

**Recommended code change:** detect a work item finding by title prefix
(`Work item #`) and force-post it as a general PR comment, regardless of
what `file` / `line` the model produced:

```text
In src/reviewforge/ado/cli.py (or the new PostToAdoStage
implementation), in the post loop:

  is_work_item_finding = str(f.get("title", "")).startswith("Work item #")
  if is_work_item_finding:
      # Drop any file/line and post as a PR-level comment.
      # The author needs to read the work item to judge the finding;
      # an inline file comment makes the false positive look authoritative.
      f = {**f, "file": None, "line": None}
  ...then continue with the existing post logic. The existing path
  already handles f.get("file") is None correctly (general comment).
```

**Recommended prompt change:** in `prompts/review-system.md`, make the
"Work item verification" section explicit that work item findings **always
have `file: null, line: null`** — do not guess a file or line. This gives
the posting path a stable signal to detect work item findings by structure
(not by title prefix), which is more robust to title rewording.

---

## Compounding — stages downstream of blind stages

`intent`, `plan`, `digest`, `verify`, `severity` all read empty context. The
gap is **multiplicative, not additive**:

```text
intent.requirements              ← should derive from work item AC
                                  ; derived from PR title instead, often wrong
                                  │
                                  ▼
context_digest.possible_intentional_choices
                                  ← should include "PM said in comment #3 deferred"
                                  ; not present
                                  │
                                  ▼
review_diff.findings             ← cannot honour "plausibly intentional" or
                                  "comment already discussed"
                                  │
                                  ▼
verify_findings                  ← cannot defend the author against invented
                                  requirements or duplicated issues
                                  │
                                  ▼
calibrate_severity               ← still no work item grounding to downgrade
                                  "blocker" on a Spike task to "minor"
```

Every stage does its job in isolation. The pipeline is structurally correct.
The data the prompts require is not on the wire. The result is a verifier
that operates on the diff alone and applies a work-item-aware rubric to the
diff anyway. A default-empty context turns every prompt instruction that
references work items into a prompt for the model to **invent** work item
content.

---

## Summary

The cost is concentrated in the prompt's most aggressive test: the
"Work item requirement not addressed" finding construction at `blocker` or
`major` severity (`prompts/review-system.md`). With no work item data behind
it, this is the highest-severity finding the system can produce, and the
one most likely to be wrong. Any repo whose maintainers write acceptance
criteria in work items but not in PR descriptions is at material risk of
this finding being posted.

The comment template's evidence fields
(`whyNewInThisPr`, `whyNotIntentional`, `contextFilesRead`) need to be
downgraded to "model's reading" or marked as "not author-stated" when the
model has not actually read the work item source. Right now they are
presented as confident first-class reasoning, which gives the false
positive more social weight than it deserves.

---

## Evidence trail (all verified on `main`)

- `src/reviewforge/pipeline/orchestrator.py:121-148` — `_make_stage_context` only sets `extras["paths"]`.
- `src/reviewforge/pipeline/stages/fetch_pr_metadata.py:31-46` — calls `call_helper("fetch-context", …)`, writes only `ctx.metadata` to memory.
- `src/reviewforge/ado/cli.py:193-240` — `fetch_work_items` writes four files; nothing in `src/` reads them back.
- `src/reviewforge/ado/cli.py:350-396` — `command_fetch_context`.
- `src/reviewforge/pipeline/stages/review_diff.py:42-58` — reads `wi_context`, `wi_comments_context`, `thread_context` from `ctx.extras`.
- `src/reviewforge/pipeline/stages/verify_findings.py:32-48` — same.
- `src/reviewforge/pipeline/stages/reconstruct_intent.py:18-40`, `plan_context.py:18-32`, `collect_context.py:28-72`, `context_digest.py:18-40`, `calibrate_severity.py:19-43` — all read the same `ctx.extras` keys.
- `src/reviewforge/ai/prompts.py:108-167` — `_briefing_session`, `stage_instruction`, `review_instruction`.
- `src/reviewforge/artifacts/manager.py:18-36` — `ARTIFACT_NAMES`.
- `prompts/review-system.md` — "Work item verification" section.
- `prompts/verify-findings.md` — "Defend the PR author" rule.
- `prompts/intent.md` — "linked work items, existing PR discussion" input.
- `prompts/context-digest.md` — `possible_intentional_choices` output.
- `comment.md.example` — `evidence` fields rendered verbatim in the post.
- `tests/test_stages.py:148-150` — fixture sets `[]`; tests the broken contract.
- `src/reviewforge/config.py:168, 289, 569` — `verify_findings` default = `True`.

---

## Follow-up

- **Smallest correct fix (loader bug):** ~15 lines in `FetchPrMetadataStage.run`
  to read the four JSON files back into `ctx.extras` (and into `ctx.metadata`
  for the work item / thread / comment lists). One-line prompt addition in
  `prompts/verify-findings.md` requiring the model to record the work items
  it actually consulted (so the evidence field can be audited).
- **Data quality guard (V9 mitigation):** in `prompts/review-system.md`, the
  "Work item verification" section should be **conditional on whether work
  items were provided** AND should explicitly require `file: null, line: null`
  for every work item finding (no guessed file / line). In
  `src/reviewforge/ado/cli.py` (or `PostToAdoStage`), detect work
  item findings by title prefix `Work item #` and **force-post as a general
  PR comment** — strip `file` / `line`, never enter the diff-mapper path,
  never silently drop on `no_line_mapping`. This protects against a model
  that "helps" by guessing a file (silent skip) or by filing the finding
  inline (wrong context).
- **Test coverage gap:** the suite needs a fixture that populates
  `wi_context` with a known shape, plus an end-to-end test asserting that a
  finding whose rationale is "deferred per work item comment #3" is
  actually dropped. Plus a posting test: a work item finding with a guessed
  `file: "src/payments/charge.ts"` must end up posted as a general comment,
  not dropped and not inline.
- **Evidence presentation:** `comment.md.example` should distinguish
  `evidence` populated from a work item source from `evidence` inferred
  from the diff alone. Today they are visually identical, which is why
  false positives read as authoritative. Work item findings should add a
  small banner: *"This finding is based on the work item, not on the diff.
  Author: please confirm the work item is in scope and current."*
- **Work item quality signal (longer-term):** if the platform exposes
  "last edited" / "state" / "work item type" on the work item, surface
  them in the prompt so the model can:
  - **downgrade** severity for `Spike` / `Onboarding` / `Refactor` types;
  - **skip** parent / epic items whose AC clearly span multiple PRs;
  - **flag** stale descriptions (last edited > N days before the PR).
  This is opt-in and would require a new context shape and a prompt
  section, but it directly addresses risks 1, 2, and 5.
