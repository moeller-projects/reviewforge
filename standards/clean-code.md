<!-- target path: pr-review-bot/standards/clean-code.md -->

# Coding standards

## Correctness & safety

* No obvious correctness bugs: off-by-one errors, null or undefined dereferences, unhandled error paths, incorrect async or await usage, race conditions on shared state, invalid assumptions about ordering, state, or identity.
* Resources acquired are released on every path: files, handles, connections, locks, subscriptions, timers, streams, temporary files, and external sessions.
* Prefer scope-bound lifetimes where the language supports them.
* Inputs crossing a trust boundary are validated.
* No secrets, tokens, credentials, authorization headers, session IDs, PII, or sensitive business data in logs, errors, comments, telemetry, or model prompts.
* Errors are handled or propagated deliberately.
* Do not silently swallow errors unless the code clearly documents why that is safe.
* Security-sensitive code must fail closed, not open.
* Authorization, authentication, and permission checks must not be weakened.
* Data parsing must handle malformed, empty, partial, or unexpected input.

## Clarity & structure

* Names reveal intent.
* Avoid abbreviations unless they are domain-standard.
* Functions should do one thing.
* Deep nesting, long parameter lists, and mixed abstraction levels should be flagged when they make the change hard to understand or risky.
* No duplicated logic that should reasonably be factored.
* No dead code.
* No commented-out code.
* Public functions and non-obvious decisions should document why, not what.
* Avoid clever code when straightforward code would be safer.

## Change hygiene

* The change should be minimal and focused.
* Unrelated edits should be flagged.
* New behavior should have tests, or the absence of tests should be justified by context.
* No debugging artifacts: temporary prints, console logs, focused tests, skipped tests, TODOs without ownership, local paths, sample credentials, or throwaway comments.
* Backwards-incompatible changes to public surfaces should be called out.
* Configuration changes should be safe by default.
* CI, deployment, and automation scripts should be deterministic and non-interactive.

## Performance

Only flag performance issues when they are likely to matter.

* No needless work in hot paths.
* Avoid N+1 queries or repeated remote calls.
* Avoid repeated allocation in large loops.
* Avoid unbounded memory growth.
* Avoid unbounded retries, polling, or recursion.
* Do not micro-optimize cold paths.

## Tests

* Tests should cover meaningful behavior, not implementation details.
* New branches, error paths, validation, and boundary cases should be tested when practical.
* Tests should be deterministic.
* Tests should not depend on wall-clock time, network availability, shared global state, or execution order unless explicitly controlled.
* Do not leave focused, skipped, or disabled tests unless clearly intentional and justified.

## Review noise policy

* Do not report style-only issues unless they create real ambiguity or violate an explicit rule above.
* Do not report theoretical issues without evidence.
* Do not report broad architectural preferences.
* Do not ask for refactors unrelated to the PR.
* A small number of high-confidence findings is better than a long list of low-confidence findings.
