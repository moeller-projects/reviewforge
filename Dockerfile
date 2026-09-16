# syntax=docker/dockerfile:1
# target path: reviewforge/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR through direct Azure DevOps REST calls. Build once, run per PR.
#
# Two stages: `build` holds the toolchains (uv, npm) and their caches; the
# runtime stage ships only what a review run needs -- no uv, no npm, non-root.
# Requires BuildKit (default since Docker Engine 23.0) for cache/bind mounts.

# ---------------------------------------------------------------------------
# build: resolve and install everything; nothing here reaches production
# ---------------------------------------------------------------------------
FROM node:24-bookworm-slim AS build

# Versions are required build arguments supplied by versions.env via
# `python -m reviewforge.ops build`; Docker cannot evaluate that file in ARG defaults.
ARG PI_VERSION
ARG UV_VERSION
RUN test -n "$PI_VERSION" && test -n "$UV_VERSION"

# python3 is needed by uv pip install below; ca-certificates for PyPI TLS.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates python3 \
 && rm -rf /var/lib/apt/lists/*

# Pin uv by copying the official binary (no installer script, no curl).
COPY --from=ghcr.io/astral-sh/uv:${UV_VERSION} /uv /usr/local/bin/uv

# Global CLI: the Pi coding agent, installed into a dedicated prefix so the
# whole tree (package + hoisted deps + bin symlink) is one copyable unit.
# npm's cache lives in a BuildKit mount -- never written to any layer.
RUN --mount=type=cache,target=/root/.npm \
    npm install -g --prefix /opt/pi --ignore-scripts --no-audit --no-fund \
      "@earendil-works/pi-coding-agent@${PI_VERSION}"

WORKDIR /app

# Locked Python dependencies, installed into a venv we can copy wholesale.
# Manifests are bind-mounted (never a layer), the uv cache is a mount, so a
# one-line uv.lock change re-downloads one wheel instead of all of them.
# UV_PYTHON_DOWNLOADS=0 keeps the venv on the system interpreter, which the
# runtime stage has as well. UV_COMPILE_BYTECODE=1 precompiles for faster
# container startup (one container is started per PR).
ENV UV_LINK_MODE=copy \
    UV_PYTHON_DOWNLOADS=0
RUN --mount=type=cache,target=/root/.cache/uv \
    --mount=type=bind,source=uv.lock,target=uv.lock \
    --mount=type=bind,source=pyproject.toml,target=pyproject.toml \
    uv venv /app/.venv \
 && uv export --format requirements-txt --no-dev --no-emit-project --extra crg -o /tmp/req.txt \
 && UV_COMPILE_BYTECODE=1 uv pip install --python /app/.venv/bin/python -r /tmp/req.txt \
 && rm /tmp/req.txt

# ---------------------------------------------------------------------------
# runtime: only what a review run needs
# ---------------------------------------------------------------------------
FROM node:24-bookworm-slim

LABEL org.opencontainers.image.title="reviewforge" \
      org.opencontainers.image.description="Azure DevOps PR review runner (Pi read-only + direct REST posting)"

# Runtime tools only -- same interpreter version as the build stage (same base).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git python3 ripgrep \
 && rm -rf /var/lib/apt/lists/* \
 # npm/corepack are build-time tools; Pi is copied in below, so strip them
 # from the runtime image (smaller surface, ~15 MB).
 && rm -rf /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/corepack \
 && rm -f /usr/local/bin/npm /usr/local/bin/npx /usr/local/bin/corepack

# Non-root runtime: the container clones with credentials and executes an
# LLM-driven agent -- it should not run as root. Named volumes
# (reviewforge-artifacts, reviewforge-crg-cache) inherit these ownerships.
# HOME is pinned so Pi resolves config under /home/review, matching the
# auth.json bind mount below.
# /home/review/.pi/agent is pre-created so the auth.json bind mount lands in
# a directory Pi can actually read and write sessions to.
RUN useradd --uid 10001 --create-home review \
 && mkdir -p /workspace/artifacts /workspace/crg-cache /home/review/.pi/agent \
 && chown -R review:review /workspace /home/review/.pi

# Pi CLI (self-contained prefix from the build stage) and the Python venv.
COPY --from=build /opt/pi /opt/pi
COPY --from=build /app/.venv /app/.venv
RUN ln -sf /opt/pi/bin/pi /usr/local/bin/pi

# Application sources (CRLF normalization is handled by .gitattributes on the
# host, not in the image).
COPY --chown=review:review src/ /app/src/
COPY --chown=review:review prompts/ /app/prompts/
COPY --chown=review:review standards/ /app/standards/

ENV PYTHONPATH=/app/src \
    PATH="/app/.venv/bin:/opt/pi/bin:$PATH" \
    HOME=/home/review \
    WORKSPACE=/workspace \
    PI_SKIP_VERSION_CHECK=1 \
    PI_TELEMETRY=0 \
    PYTHONUNBUFFERED=1

USER review
WORKDIR /workspace

# Build-time smoke assertion: a missing package, broken venv, or broken Pi
# symlink fails the BUILD here instead of degrading silently in production
# (this check would have caught the CRG package absence in minutes).
RUN python3 -c "import reviewforge, code_review_graph" \
 && pi --version \
 && git --version && rg --version \
 && test -f /app/prompts/fast-review-system.md \
 && test -f /app/prompts/native-review-system.md \
 && test -d /home/review/.pi/agent

ENTRYPOINT ["/app/.venv/bin/python", "-m", "reviewforge"]
# Default subcommand when the image is run with no extra args. Overridden by
# `podman run image <subcommand> ...` (e.g. post, discover). Mirrors the
# no-argv default in cli.main().
CMD ["review"]
