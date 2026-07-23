## Context

ReviewForge currently terminates from library modules. CLI consumers need the existing messages and exit codes, while embedded callers need catchable exceptions.

## Goals / Non-Goals

**Goals:** replace library exits with domain errors; preserve error text, stage failure status, and artifact layout.

**Non-Goals:** change CLI arguments, retry policy, or error wording.

## Decisions

- Domain exceptions retain the existing operator-facing message and carry structured details separately.
- CLI entrypoints translate `ReviewForgeError` to stderr and return 1.
- Stages catch `ReviewForgeError` before generic exceptions to retain prior failure text.

## Risks / Trade-offs

- Existing direct library callers must catch domain exceptions rather than `SystemExit`.
