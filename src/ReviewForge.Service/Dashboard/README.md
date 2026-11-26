# Browser dashboard hosting

## Login and session

Open `https://<service>/`: it redirects to `/dashboard`. Without a valid session, dashboard
page navigation redirects to `/dashboard/login`. Submit an existing environment-configured
API key to continue to `/dashboard`. API and circuit auth failures remain 401. The login form
is a regular server POST, not an interactive component: the key never enters component
state, hydration, a URL, browser storage, or the HTTP adapter. Failed credentials produce
a generic response. Login POSTs have a five-attempt/minute/IP fixed-window budget.

The `__Host-ReviewForge` cookie is HttpOnly, Secure, SameSite=Strict, path `/`, with no
Domain or persistent expiry. Sessions expire eight hours after login, never slide, and
contain only a random session ID and a generic identity. Closing the browser is not a
server-side revocation mechanism; use the antiforgery-protected logout form. Logout
revokes the session ID immediately, including replayed cookies. The session registry is
process-local and bounded at 10,000 live sessions; capacity exhaustion returns 503.

Cookie validation and the session registry protect every HTTP read/action. The interactive
server hub requires authorization for page, negotiate, and reconnect and uses .NET 11
`ConfigureConnection.CloseOnAuthenticationExpiration=true`. The circuit authentication
provider revalidates every 30 seconds for logout/expiry; the HTTP action boundary refuses
revoked/expired sessions immediately, even before the UI notices.

## HTTP boundary for later dashboard components

Inject scoped `DashboardBrowserClient`, not `DashboardQueryService`, `RunSubmissionService`,
or queue services. It provides:

- `GetDashboardAsync(CancellationToken)` -> `BrowserResult<DashboardResponse>`
- `SubmitReviewAsync(SubmitReviewRequest, CancellationToken)` -> `BrowserResult<SubmitReviewResponse>`

`BrowserOutcome` distinguishes success/accepted, invalid input, unauthenticated/forbidden,
conflict, rate limiting, unavailable queue/service, network failure, and cancellation.
The adapter imports `dashboard-http.js`, permits only the two same-origin routes, sends
session credentials, obtains a fresh CSRF token from protected `GET /dashboard/session`
for submissions, and aborts fetch on cancellation. It never receives or resends an API key.
Error response bodies are not exposed to components. Do not call JS interop during prerender.

Only `GET /reviews/dashboard` and `POST /reviews` opt into `DashboardSessionAccess`
metadata. Cookie POSTs validate antiforgery at the API-key filter before submission.
An explicitly presented header key must itself be valid; a cookie cannot rescue a bad key.
All existing endpoint rate limits, validation, claims, queue capacity and ForceReview
semantics remain in force. Header-key clients need no CSRF token. Discovery, individual
run status, resolutions and MCP remain header-key-only (MCP's existing query-key mode is
unchanged). API auth failures remain JSON/status errors, never HTML login redirects.

Framework, Radzen and adapter assets are public, manifest-listed, and contain no secrets
or personalized data. Personalized responses retain no-store and nosniff headers. The
static-asset development catch-all is disabled to preserve unknown API route semantics.

## Deployment requirements

- **HTTPS is required as observed by ASP.NET Core.** The secure cookie policy is not
  weakened in development or tests. Login over HTTP returns 400. Use HTTPS at Kestrel,
  including a trusted TLS upstream hop if a proxy is present. Compose serves HTTPS on
  port 9090 and requires read-only PFX/public-CA mounts plus an environment-supplied password;
  see [certificate setup](../../../docs/dashboard.md#docker-certificates).
- This host does **not** enable forwarded-header processing or trust arbitrary
  `X-Forwarded-Proto`/`X-Forwarded-For` values. TLS termination with plain HTTP to Kestrel
  therefore does not enable login. A future deployment change must explicitly configure
  trusted proxy/network addresses before enabling forwarded headers; never trust all
  peers. Login rate limiting currently sees the connection peer IP, so a proxy shares
  its login budget across clients.
- Data Protection deliberately uses an **ephemeral provider**. It writes no keys into
  the read-only container home/rootfs. Restart invalidates cookies, antiforgery tokens,
  pending interactive reconnects, and the process-local session registry. Users must
  reload the login form and log in again. This is an intentional single-instance design,
  not durable or multi-replica session support. No persistent key mount is required.
- Keep request-body logging disabled for login. Do not add API-key claims, credential
  echoes, query-key login, insecure-cookie fallbacks, browser storage, or a direct DI
  shortcut around the HTTP API boundary.

Verification: .NET service tests exercise real HTTPS host requests and bUnit exercises
Radzen shell/interop outcomes. `bun test ./tests/ReviewForge.Service.Tests/dashboard-http.test.mjs`
checks the actual fetch module's allowlist, credentials, CSRF and abort behavior without
credentials or a browser. Real proxy/browser/WebSocket deployment smoke testing remains
a deployment-owner responsibility; no external credential is needed for the fake-host tests.
