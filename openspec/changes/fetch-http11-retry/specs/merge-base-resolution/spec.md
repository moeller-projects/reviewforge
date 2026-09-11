## MODIFIED Requirements

### Requirement: Retry Git operations that trigger remote transfer

The repository preparation flow MUST retry failed `git fetch` and source checkout operations with Git HTTP/1.1 after the initial attempt. Fetch retries MUST use a blobless pack on the final attempt, while preserving requested refs, depth options, checkout commit, and authentication environment.

#### Scenario: Initial fetch disconnects during pack transfer

- **WHEN** the initial fetch fails with a transport disconnect
- **THEN** the next attempt invokes `git -c http.version=HTTP/1.1 fetch` with the same fetch arguments

#### Scenario: HTTP/1.1 fetch retry still disconnects

- **WHEN** the HTTP/1.1 fetch retry fails during pack transfer
- **THEN** the final fetch attempt invokes HTTP/1.1 fetch with `--filter=blob:none` to reduce transferred pack data

#### Scenario: Checkout-triggered promisor fetch disconnects

- **WHEN** source checkout fails because a promisor remote fetch disconnects
- **THEN** the next checkout attempt invokes `git -c http.version=HTTP/1.1 checkout` for the same commit

#### Scenario: Retry budget is exhausted

- **WHEN** every operation attempt fails
- **THEN** the final `GitOperationError` is raised and repository preparation does not report success
