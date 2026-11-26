# PR review dashboard

The PR dashboard is part of the Service host, not the Aspire telemetry dashboard.
It reads organization-wide Azure DevOps inventory and submits ordinary manual reviews
through the existing API. Opening or refreshing it never enqueues automatic reviews.

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
