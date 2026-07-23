# target path: reviewforge/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR through direct Azure DevOps REST calls. Build once, run per PR.
FROM node:24-bookworm-slim

# Versions are required build arguments supplied by versions.env via
# `python -m reviewforge.ops build`; Docker cannot evaluate that file in ARG defaults.
ARG PI_VERSION
ARG UV_VERSION

# Runtime tools only. curl is no longer needed because uv is copied in below.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git python3 ripgrep \
 && rm -rf /var/lib/apt/lists/*

RUN test -n "$PI_VERSION" && test -n "$UV_VERSION"

# Pin uv by copying the official binary into the image. This avoids a network
# fetch, the installer script layer, and the need for curl at build time.
COPY --from=ghcr.io/astral-sh/uv:${UV_VERSION} /uv /usr/local/bin/uv

# Global CLI: the Pi coding agent. Azure DevOps integration uses direct REST via Python.
RUN npm install -g --ignore-scripts --no-audit --no-fund \
      "@earendil-works/pi-coding-agent@${PI_VERSION}" \
 && npm cache clean --force

WORKDIR /app

# Install the package's locked Python dependencies from the source of truth.
# Copying pyproject.toml and uv.lock before the source code lets Docker reuse
# this layer when only application files change.
COPY pyproject.toml uv.lock ./
RUN uv export --format requirements-txt --no-dev --no-emit-project > requirements.txt \
 && uv pip install --system --no-cache --break-system-packages -r requirements.txt \
 && rm requirements.txt

# The repo is cloned here by the Python runner; the package CLI orchestrates review.
# CRLF normalization is handled by .gitattributes on the host, not in the image.
COPY src/ ./src/
COPY prompts/ ./prompts/
COPY standards/ ./standards/

ENV PYTHONPATH=/app/src
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0

WORKDIR /workspace

ENTRYPOINT ["uv", "run", "--no-project", "python3", "-m", "reviewforge"]
# Default subcommand when the image is run with no extra args. Overridden
# by ``podman run image <subcommand> ...`` to dispatch to other commands
# like ``post`` or ``discover``. Mirrors the no-argv default in
# cli.main() so the image is self-explanatory in isolation.
CMD ["review"]
