## Context

Repository preparation fetches pull-request refs over the configured Azure DevOps remote. A reset during pack transfer currently causes each retry to repeat the same Git transport negotiation and transfer size.

## Goals / Non-Goals

**Goals:**
- Give retries a transport variant known to avoid HTTP/2 negotiation failures.
- Reduce pack data if the transport retry also disconnects.
- Keep the first attempt unchanged and preserve all fetch semantics.

**Non-Goals:**
- Change refs, authentication, or retry count.
- Hide a final fetch failure.

## Decision

The first attempt uses the existing fetch command. The second attempt prepends Git's per-command `http.version=HTTP/1.1` configuration. The final retry also adds `--filter=blob:none`, reducing initial pack transfer size while retaining commit and tree history. These settings are scoped to the subprocess and apply to initial, deepening, and unshallow fetches through the shared retry helper.

## Risks / Trade-offs

HTTP/1.1 may be slower than HTTP/2, but it is used only after a failed attempt. Blobless fetches may lazily retrieve required file contents later during diff generation; this trades one large pack transfer for smaller, demand-driven transfers. A persistent network or server failure still surfaces after the existing retry budget.
