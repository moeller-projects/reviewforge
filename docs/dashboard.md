# PR review dashboard

The PR dashboard is part of the Service host, not the Aspire telemetry dashboard.
It reads organization-wide Azure DevOps inventory and submits ordinary manual reviews
through the existing API. Opening or refreshing it never enqueues automatic reviews.

## HTTPS and deployment

Dashboard login requires HTTPS **as seen by Kestrel**. The checked-in Compose mapping
`http://localhost:5080` → container HTTP port 8080 remains suitable for header-key API
clients on a protected network, but **cannot support dashboard login**: HTTP login
returns 400 and dashboard cookies are always Secure. There is no HTTP cookie fallback.

For local development, provision and trust a .NET development certificate (`dotnet
dev-certs https --trust`), then run the Service with a direct HTTPS listener, for example:

```bash
dotnet run --project src/ReviewForge.Service -- --urls https://localhost:7080
```

Configure the required environment credentials first as described in the [README](../README.md#run-it),
then visit `https://localhost:7080/dashboard/login`. These are setup instructions, not
evidence that a real browser/certificate smoke test has passed.

For deployment, use direct HTTPS to Kestrel with an operator-provisioned certificate,
or TLS pass-through that preserves HTTPS to Kestrel. Kestrel endpoint/certificate settings
can be supplied through environment variables such as
`Kestrel__Endpoints__Https__Url` and
`Kestrel__Endpoints__Https__Certificate__Path`; keep any certificate password in the
environment only. Provision a readable certificate and appropriate network access for
the non-root service account. A container-specific TLS listener, certificate mount,
and port mapping require deployment-owner configuration and validation; the shipped
Compose file does not provide them. Keep its read-only root filesystem and existing
hardening intact. This feature does not require a writable Data Protection key mount.

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
| `GET /reviews/dashboard` | Aggregate inventory, review summaries, eligibility, warnings, and operational state; status rate-limit policy. |
| `POST /reviews` | Submit an ordinary manual review; submit rate-limit policy; cookie authentication also requires a valid antiforgery token. |

The same-origin browser adapter obtains a request token from authenticated
`GET /dashboard/session` and sends `X-CSRF-TOKEN` for submission. Login and logout use
antiforgery form tokens. The session does not grant access to `GET /reviews/{runId}`,
`POST /reviews/discover`, `/resolutions`, or `/mcp`. MCP's existing query-key alternative
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
or nonmanual submission. Dashboard reruns set it to true after confirmation; discovery,
CLI, MCP, Resolve, and ReviewDraft do not implicitly opt in.

Submission responses remain: 202 `{runId, statusUrl}`, 400 invalid request/antiforgery,
401 missing or invalid authentication, 409 another run owns the PR (conflicting `runId`
when available), 429 rate limited, and 503 queue full/unavailable. An accepted run can
subsequently skip, including when the PR is a draft. Rerun availability is advisory;
the submission-time claim check is authoritative under races.

The opt-in OpenAPI endpoint described in the [README](../README.md#configuration) includes
the optional submission flag and dashboard response schema; this reference additionally
documents the browser cookie/antiforgery boundary.

## Reading the dashboard

- **Visibility:** all retrieved open PRs visible to the configured organization credentials,
  not just PRs eligible for automatic review. Project/repository filters narrow the display.
- **Basic eligibility:** Eligible/Ineligible/Unknown describes available draft, target,
  creator, linked-item, and same-head/comment facts. Missing inputs are Unknown, not a
  definitive exclusion. Eligible is not a guarantee of next-sweep scheduling: backoff,
  caps, claims, bot-head and comment checks still apply. Discovery-enabled status is separate.
- **Current-head coverage:** only a successful ordinary Review of the current head counts.
  A new head is Unreviewed until reviewed. Resolve and ReviewDraft are not review coverage.
  A failed or skipped rerun does not erase an earlier successful current-head review.
- **Latest PR attempt:** the latest ordinary-review attempt across all heads, including a
  manual review that fails during context fetch before its head is captured. The grid and
  details explicitly label unknown/different heads; they never imply current-head coverage.
  Details also retain the separate latest current-head attempt (`latestAttempt` in the API)
  and last successful current-head review. An old-head active run is not progress for the
  new head. Any run kind can hold the shared claim and block rerun.
- **Review findings:** stored review findings, including carried-forward findings and excluding
  command-fix audit rows. This is neither newly detected nor currently unresolved findings.
- **Progress:** uses the actual loaded stage list. Failure retains the faulting stage;
  skipped runs can finish below 100%; completed runs are 100%. No stages are resumed after restart.

Summary cards can overlap. Reviewed means successful current-head coverage; Needs attention
also includes failed latest current-head ordinary reviews and unknown-head rerun failures, so a failed rerun can appear in both. Known old-head failures remain visible as PR-level diagnostics but do not by themselves mark a successfully reviewed current head as needing attention.
Completed reviews with findings are not failures. Needs attention excludes active ordinary
reviews and excludes unreviewed drafts/known basic-filter-ineligible PRs. Unreviewed,
nondraft Unknown-eligibility PRs remain visible in Needs attention. Reviewing includes
ordinary-review blockers on old or unknown heads; other run-kind blockers appear in detail.

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
still merging fresh operational state. A cold failure has no inventory fallback and fails
the request; a client HTTP failure may retain its previous snapshot with an error indication. Cold inventory refreshes may make sequential cheap
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

Dashboard persistence reads rank current-head attempt/success, latest PR-level ordinary review,
and latest incomplete shell in SQL in batches of at most 100 PR identities. All scoped tracker
IDs are excluded before persisted ranking, so terminal corrections reveal older real successes.
Only selected per-PR attempt/success/blocker IDs request finding-count enrichment; retained
terminal IDs otherwise remain cheap state observations for accepted-rerun reconciliation. Neither
adapter loads finding JSON or materializes entire run history for the dashboard.

Live stage progress and skipped attempts exist only in the bounded in-memory tracker;
skipped ReviewRun rows are never persisted. A skipped rerun can disappear from history
after tracker eviction/restart, without changing prior durable successful coverage.

Memory-queue requests are lost on restart. With `Persistence:QueueMode=Sqlite`, durable
requests can be reclaimed after claim expiry and **re-executed from the beginning**, not
resumed at their last stage. Persisted run shells are not stage checkpoints; stale shells
can be reaped as failures. Persisted results and queue rows support status read-through,
but do not recreate the live stage timeline.

Before production release, obtain both Ubuntu and Windows CI coverage evidence, exercise
real Azure DevOps paging/partial failures and representative organization scale, and validate
the selected HTTPS topology with a real browser. Include keyboard/focus/Escape behavior,
responsive layouts, screen-reader announcements, scroll/filter retention, session expiry,
logout/reconnect, and WebSocket closure. In-process/component and fetch-module tests do not
replace those checks. Existing batching tests and simplified indexed-join query-plan checks
are not a production load benchmark or proof of the complete windowed SQL query's plan.
