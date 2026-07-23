## ADDED Requirements

### Requirement: Safe ADO retries
ReviewForge MUST retry GET requests for HTTP 429, 500, 502, 503, 504 and transport failures using bounded exponential backoff with jitter.

#### Scenario: Transient GET failure
- **WHEN** an ADO GET receives 503 and a subsequent attempt succeeds
- **THEN** ReviewForge MUST return the successful response.

### Requirement: Non-idempotent safety
ReviewForge MUST NOT retry POST or PUT after a received HTTP response, including 5xx responses.

#### Scenario: POST 500
- **WHEN** an ADO POST receives 500
- **THEN** ReviewForge MUST surface the error without retrying.

### Requirement: Retry configuration and observability
ReviewForge MUST honor capped Retry-After values, request retry budgets, and configured retry defaults while logging each retry.

#### Scenario: Retry-After
- **WHEN** ADO responds 429 with Retry-After
- **THEN** ReviewForge MUST wait the capped stated delay before retrying.
