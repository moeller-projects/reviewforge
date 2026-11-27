# Technician review dashboard

The technician dashboard is part of the Service host, not the Aspire telemetry dashboard.
Use it to start read-only review drafts, inspect runs, record finding decisions, and explicitly
post selected approved findings. Opening or refreshing it never enqueues reviews.
The advanced ordinary-review action can publish automatically; it is not the default.

## HTTPS and deployment

Service, CLI, browser pages and MCP share **`https://localhost:9090`** by default:
`/` redirects to `/dashboard`, unauthenticated dashboard navigation redirects to
`/dashboard/login`, REST lives at `/reviews`, and MCP lives at `/mcp`. API and circuit
auth failures remain 401, not HTML redirects. Aspire exposes the same HTTPS port directly,
so do not run standalone and Aspire-hosted Service instances simultaneously.

Dashboard login requires HTTPS **as seen by Kestrel**. HTTP login returns 400 and
cookies are always Secure. There is no HTTP cookie fallback or automatic HTTP listener.

For local development, configure credentials as described in the [README](../README.md#run-it),
then provision and trust the development certificate and start the Service:

```bash
dotnet dev-certs https --trust
dotnet run --project src/ReviewForge.Service
```

Visit `https://localhost:9090/`. Bindings remain configurable with `--urls` or the
unprefixed `Urls` environment variable. The container uses `Urls=https://+:9090` to
listen on all container interfaces; Compose publishes it on host loopback by default.
Local `config.Production.toml` overrides must not reintroduce an old HTTP/second-port
binding. The checked-in default is HTTPS in every environment.

A browser trusting the development certificate does not necessarily make Node/Bun MCP
clients trust it. Export **only its public certificate** and launch the client with
`NODE_EXTRA_CA_CERTS` pointing to that PEM file; restart the whole client after setting
the variable. For example, in PowerShell (with the existing `.omp` directory):

```powershell
$cert = "$env:USERPROFILE\.omp\reviewforge-dev-cert.pem"
dotnet dev-certs https --export-path $cert --format PEM
$env:NODE_EXTRA_CA_CERTS = $cert
omp
```

Use `https://localhost:9090/mcp`. Do not disable TLS verification. If Kestrel uses a
custom certificate, trust its actual issuing CA instead of exporting a development
certificate. These setup commands are not evidence of a completed browser/MCP smoke test.

### Docker certificates

Compose maps host 9090 to container HTTPS port 9090. Before starting it, provision:

- `REVIEWFORGE_TLS_CERT_PATH`: absolute host path to a password-protected PFX containing
  the server private key, certificate and intermediate chain.
- `REVIEWFORGE_TLS_CA_PATH`: absolute host path to the public CA PEM used to verify the
  server certificate. For a self-signed development certificate this is its public PEM.
- `REVIEWFORGE_TLS_CERT_PASSWORD`: the PFX password, supplied through the environment only.

Keep TLS material outside the repository. Mounts are read-only at `/https/reviewforge.pfx`
and `/https/ca.pem`; missing host files fail instead of becoming directories. On Linux,
ensure the container's uid 1654 can read both files while restricting private-key access
(for example owner 1654 and mode 600 for the PFX). The image uses curl for an HTTPS
`/health` check with `--cacert /https/ca.pem`; it validates the chain and localhost hostname.
The server certificate must therefore cover `localhost`, plus any other hostname used by
clients. For a different deployment hostname/topology, explicitly adjust both the health
check and certificate rather than bypass validation.

For local Compose development only, the same .NET development certificate can be exported
outside the repository (set a nonempty password securely in the environment first):

```bash
mkdir -p "$HOME/.reviewforge/tls"
dotnet dev-certs https --export-path "$HOME/.reviewforge/tls/reviewforge.pfx" --password "$REVIEWFORGE_TLS_CERT_PASSWORD"
dotnet dev-certs https --export-path "$HOME/.reviewforge/tls/ca.pem" --format PEM
export REVIEWFORGE_TLS_CERT_PATH="$HOME/.reviewforge/tls/reviewforge.pfx"
export REVIEWFORGE_TLS_CA_PATH="$HOME/.reviewforge/tls/ca.pem"
# Apply restrictive permissions/read access for uid 1654 before starting Compose.
docker compose up --build
```

For production, use operator-provisioned certificates and direct HTTPS to Kestrel or TLS
pass-through. Kestrel settings can be supplied through environment variables such as
`Kestrel__Endpoints__Https__Url` and `Kestrel__Endpoints__Https__Certificate__Path`;
keep certificate passwords in the environment only. Preserve the read-only root filesystem,
non-root user, loopback default and existing hardening. `.env*`, local `config.*.toml`
and certificate/key files are excluded from the Docker build context. No writable Data
Protection key mount is required. Docker/Aspire TLS startup, browser/WebSocket behavior
and end-to-end MCP trust require deployment-owner validation.

**TLS termination at a proxy followed by HTTP to this host is not a supported dashboard
setup as shipped.** The host does not configure trusted forwarded-header processing.
A browser-facing HTTPS URL alone does not make `Request.IsHttps` true. Do not trust
arbitrary `X-Forwarded-*` headers or weaken Secure cookies to work around this. A future
proxy deployment must explicitly restrict trusted proxies/networks, validate scheme and
client-IP handling, and test authentication, antiforgery, WebSocket upgrade/reconnection,
and rate limits before exposure. Without client-IP forwarding, proxy clients share the
proxy's rate-limit partition.

Use **one Service instance**. Sessions and PR claims are process-local; sharing only a
SQLite database or adding load-balancer affinity does not provide a supported multi-instance
security/concurrency model. Data Protection uses ephemeral process-local keys. Restarting
the service intentionally invalidates all dashboard sessions; persistent key storage alone
would not preserve the process-local session registry.

## Login and session security

1. Open `GET /dashboard/login` over HTTPS and enter an existing API key configured through
   `REVIEWFORGE_API_KEYS`. Login is an antiforgery-protected form POST, limited to five
   attempts per minute per remote IP. Invalid credentials return 401; HTTP or invalid
   antiforgery input returns 400; session-capacity exhaustion returns 503.
2. Successful login redirects to `/dashboard`. The nonpersistent, HttpOnly
   `__Host-ReviewForge` cookie has Secure and SameSite=Strict protection and a **fixed
   eight-hour maximum**, with no sliding renewal. Browser session closure can end it earlier.
3. Use Log out to POST `/dashboard/logout` with antiforgery protection. Logout revokes
   the server session and clears the cookie. GET is not a logout operation.

The submitted API key is not retained in session claims, browser storage, rendered page
state, or application logs. Do not enable request-body logging on the login route. API
keys are shared credentials, not individual user identities or per-project authorization.
Removing a configured key is not a revocation mechanism for already-issued sessions;
logout, expiry, or service restart ends those sessions.

The page, initial rendering, and interactive-server endpoints require authentication.
Each dashboard HTTP read/action validates its session. Circuit identity revalidation runs
every 30 seconds, and connections are configured to close at authentication expiration.
Expiry must stop reads and reruns, not merely hide controls. Actual deployed browser and
WebSocket expiry/reconnection checks are still required before release.

## API and antiforgery reference

Existing `X-Api-Key` clients keep their authentication behavior. A valid dashboard cookie
is an alternative **only** for these API endpoints:

| Endpoint | Contract |
|---|---|
| `GET /reviews/dashboard` | PR inventory and advisory operational state for PR selection. |
| `GET /reviews/history` | Bounded run worklist; `offset`, `count`, `attentionOnly`, and optional complete PR scope. |
| `GET /reviews/drafts/capability` | Whether strict finding verification permits draft reviews. |
| `POST /reviews/drafts` | Submit an explicitly requested read-only draft. |
| `GET /reviews/{runId}` | Safe status read-through from tracker, queue, then durable run. |
| `GET /reviews/{runId}/details` | Finding payloads, verification evidence, human decisions, and publication audit. |
| `POST /reviews/{runId}/decisions` | Save a revision-scoped decision using its expected version. |
| `GET /reviews/{runId}/decisions/history?findingId=…` | Latest 100 immutable decisions for one run-local finding, loaded on demand. |
| `POST /reviews/{runId}/publish` | Explicit nonempty selected approved findings and expected approval versions. |
| `POST /reviews/{runId}/reconcile` | Explicitly inspect existing threads and repair publication audit; never posts findings. |
| `POST /reviews` | Advanced ordinary review, which may publish and apply configured authorized fixes. |

Reads use the status rate-limit policy; submissions, decisions, and publication use the submission policy.
Every cookie-authenticated POST also requires a valid antiforgery token.

The same-origin browser adapter obtains a request token from authenticated
`GET /dashboard/session` and sends `X-CSRF-TOKEN` for each mutation. Login and logout use
antiforgery form tokens. The session does not grant access to `POST /reviews/discover`,
`/resolutions`, or `/mcp`. MCP's existing query-key alternative
is not accepted for dashboard login. An explicitly supplied invalid API-key header is
not rescued by a valid cookie. API failures return HTTP status codes, not HTML redirects.

Example ordinary submission body (no browser-supplied head SHA is trusted):

```json
{
  "org": "my-org",
  "project": "my-project",
  "repositoryId": "my-repository-id",
  "prId": 1234,
  "forceReview": true
}
```

`forceReview` is optional and defaults to `false`. True bypasses **only** same-head
suppression for a manual ordinary review. Draft checks, shared PR claims, author/fix
permissions, head pinning, and push safeguards remain. It is rejected for other run kinds
or nonmanual submission. The advanced ordinary-review action sets it after confirmation.
Explicit manual read-only ReviewDraft runs independently allow draft PRs and already-reviewed
heads without `forceReview`; this does not change ordinary automatic-review or Resolve gates.
Draft reviews require `VerifyFindings:Enabled=true`.

Submission responses remain: 202 `{runId, statusUrl}`, 400 invalid request/antiforgery,
401 missing or invalid authentication, 409 another run owns the PR (conflicting `runId`
when available), 429 rate limited, and 503 queue full/unavailable. An accepted run can
subsequently skip, including when the PR is a draft. Rerun availability is advisory;
the submission-time claim check is authoritative under races.

The opt-in OpenAPI endpoint described in the [README](../README.md#configuration) includes
the optional submission flag and dashboard response schema; this reference additionally
documents the browser cookie/antiforgery boundary.

## Start, inspect, and decide

1. Choose **Start draft review** to open `/dashboard/start`. Search the retrieved PRs,
   including draft PRs and already-reviewed heads. An active PR claim prevents a second run.
2. Select a PR to start a read-only draft. Verification must be enabled. Accepted submission
   opens `/dashboard/runs/{runId}`; it does not mean analysis has completed.
3. Inspect **Findings**, with **Execution** and **Context** available for investigation.
   Compare candidate evidence, pipeline validation, lightweight verification, and publication state.
4. Save **Approve**, **Reject**, or **Defer**. Reject requires a reason. Approval does not post;
   rejection is internal and never closes a thread or posts a rebuttal. Decisions are scoped to
   this run's reviewed revision and do not silently suppress findings on later revisions.
5. Select approved, technically eligible, unpublished findings and confirm **Post selected findings**.
   The server checks approval versions, the PR claim, and the current head again. If the head changed,
   start a new draft. Empty selection is never interpreted as publish all in the technician API.

A human approval cannot override failed anchor validation or a rejected verifier outcome.
Deep repository investigation, objection-driven reinvestigation, and confidence assessment are
phase-two work. The current lightweight verifier is not a confidence score; an unverified finding
must not be displayed as confirmed. Ordinary reviews can retain findings when lightweight
verification fails; strict drafts fail instead when required verdicts are missing.

Publication can partially succeed. Inspect each finding's posted, already-posted, failed,
unknown, or not-attempted result. Unknown means reconciliation is needed, not that no external
write occurred. Use **Reconcile unknown findings** to inspect existing PR threads without
reposting. If no matching thread is observed, the outcome stays unknown; check the PR manually,
reconcile later, or start a new draft. The UI never automatically repeats a mutation.

The collapsed **Automatic publication (advanced)** action on the start page is separate. It
confirms an ordinary review that can publish and apply configured author-authorized fixes without
this draft approval workflow. Ordinary draft-PR and other safety gates remain in effect.

## Reading the run worklist

- **Grouping:** full PR identity (organization/project/repository/PR ID), then reviewed head,
  then runs. Numeric PR IDs from different scopes never merge.
- **Ordering:** revisions use the newest observed run timestamp, not lexical SHA order or a
  provider revision number. Runs are newest first. Unknown-head attempts have a separate group.
- **Needs attention:** active/failed runs and completed drafts with pending decisions or approved
  unpublished findings. The server filters attention before pagination. A completed review with
  findings is not an execution failure. Deferred decisions remain accessible through history.
- **Expansion:** attention groups/revisions open initially; older loaded history stays collapsed.
  Manual expansion choices survive polling and search/view changes.
- **Bounds:** load more up to 500 run identities across ten pages per view. Changing attention/all
  view starts a new bounded window. Search applies to loaded rows, not the entire database.
  Within a view, refreshed rows displaced by newer arrivals remain as explicitly labeled snapshots.
- **Counts:** candidates, accepted/rejected outcomes, and newly posted findings describe this run.
  Carried-forward dedupe identities are not new discoveries. Missing legacy evidence/counts remain
  unavailable rather than being shown as zero or reconstructed as fact.
- **Source metadata:** closed or inaccessible PRs do not remove durable run history. The current-head
  label is based on latest observed source metadata, not a publication-time guarantee.

Only successful ordinary reviews establish current-head coverage. Draft and Resolve runs do not;
failed reruns do not erase earlier successful ordinary coverage. The inventory API retains its
advisory coverage/eligibility fields, separate from the technician worklist's attention semantics.

## Freshness, warnings, and discovery

Inventory/source metadata uses a demand-driven, single-flight **30-second** cache shared
across dashboard users. Cold loads wait for inventory; after expiry, reads immediately reuse
last usable inventory while one shared background metadata refresh runs. Responses separately
expose `inventoryFetchedAt` and `generatedAt`, plus `inventoryStale`, `inventoryRefreshing`,
and `inventoryRefreshFailed`. Stale metadata is explicitly labeled in the UI, independently
of partial inventory. Failed refreshes retain usable inventory and a safe warning; stale
refresh retries wait 30 seconds to avoid one failing source call per client. Initial failures
are not cached. Source exceptions/credentials are never included in refresh warnings.

A cache-owned **two-minute timeout** bounds each fetch, including cold loads. This localized
budget allows organization-wide paged reads more time than the metadata TTL without blocking
operational updates once inventory exists. It is not an ADO latency SLA or an automatic-review
schedule. Fetch cancellation uses `TimeProvider` and is independent of individual HTTP waiters;
host stopping or cache disposal cancels shared work. Even a source ignoring cancellation cannot
keep the cache's pending refresh alive beyond that budget.

Operational queue/tracker/store state is merged on each read; the UI refreshes approximately
every **3 seconds while active**, or **25 seconds while idle**, without overlapping refreshes.
Accepted reruns appear queued immediately; this does not force a new Azure DevOps call.
These intervals are not an end-to-end freshness or latency SLA.

Partial inventory is marked incomplete, with safe `sourceWarnings`; summaries describe only
the retrieved rows. Missing source metadata can make eligibility Unknown. A total refresh
failure retains server-cached inventory with explicit stale/refresh-failure markers while
still merging fresh operational state. A cold failure has no inventory fallback: the inventory-picker request fails, while the history
query explicitly reports unavailable source metadata and retains durable run identities. A client
HTTP failure may retain its previous snapshot with a visible error indication. Cold inventory refreshes may make sequential cheap
linked-item-reference calls for PRs lacking inline metadata. Shared caching limits repeated
fetches but does not prove large-organization source latency is acceptable.

`DiscoverySweepWorker` remains the only periodic automatic-review mechanism. Checked-in
`config.toml` enables a 15-minute sweep and `config.Production.toml` an hourly sweep, both
with `AllowAllCreators=false` and configured creator IDs. Inspect the effective interval,
allowlist, and environment overrides for the deployment; do not blindly enable all creators.
Unset/null `Discovery:SweepInterval` disables periodic sweeps. Dashboard reads never run a
sweep, whether discovery is enabled or disabled. The authenticated manual discovery API
remains a separate action.

## Restart and release limitations

The worklist uses bounded global history pages with scalar counts projected in SQLite, without
loading finding payloads into list responses. Exact UTC ticks preserve history ordering across
sub-millisecond timestamps and timezone offsets. Tracker/queue overlays cannot replace durable
terminal outcomes with stale nonterminal state. Inventory only enriches the run list; it does not
filter out closed PRs. The separate inventory endpoint retains its bounded per-PR coverage queries.

New runs retain a bounded execution journal once an existing run shell has been persisted:
actual stage start/end, observed duration, outcome, safe failure code, and run/trace correlation.
Execution is separate from the loaded stage plan. Captured context includes rulebook/review-depth
provenance and bounded summaries, uncertainties, and acceptance-criteria evidence where available;
missing model provenance stays unavailable. Raw prompts, tool transcripts, configuration objects,
and exception messages are not diagnostic payloads.

A crash before the first durable shell can leave no journal. Legacy stage history is unavailable
rather than reconstructed. Skipped attempts remain tracker-only: skipped ReviewRun rows are never
persisted and can disappear after tracker eviction/restart without changing successful coverage.

Memory-queue requests are lost on restart. With `Persistence:QueueMode=Sqlite`, durable
requests can be reclaimed after claim expiry and **re-executed from the beginning**, not
resumed at their last stage. Persisted run shells are not stage checkpoints; stale shells
can be reaped as failures. Persisted results and queue rows support status read-through;
captured journals support investigation. Neither is a checkpoint for resuming execution.

Before production release, obtain both Ubuntu and Windows CI coverage evidence, exercise
real Azure DevOps paging/partial failures and representative organization scale, and validate
the selected HTTPS topology with a real browser. Include keyboard/focus/Escape behavior,
responsive layouts, screen-reader announcements, scroll/filter retention, session expiry,
logout/reconnect, and WebSocket closure. In-process/component and fetch-module tests do not
replace those checks. Existing batching tests and simplified indexed-join query-plan checks
are not a production load benchmark or proof of the complete windowed SQL query's plan.
