# target path: reviewforge/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR through direct Azure DevOps REST calls. Build once, run per PR.
FROM node:24-bookworm-slim

# Pin tool versions for reproducible reviews. Bump deliberately.
ARG PI_VERSION=0.79.1

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git python3 python3-pip ripgrep \
 && rm -rf /var/lib/apt/lists/*

# Pin the third-party Python runtime deps. Keep this list small — most
# logic uses the standard library. ``jinja2`` powers the optional
# custom PR-comment template (see
# ``reviewforge.ado.comment_format.TemplateCommentFormatter``).
RUN pip install --no-cache-dir --break-system-packages "jinja2>=3.1"

# Global CLI: the Pi coding agent. Azure DevOps integration uses direct REST via Python.
RUN npm install -g --ignore-scripts \
      "@earendil-works/pi-coding-agent@${PI_VERSION}"

WORKDIR /app
COPY src/ ./src/
COPY prompts/ ./prompts/
COPY standards/ ./standards/

# Strip Windows CRLF line endings from Python files.
RUN find ./src -type f -name '*.py' -exec sed -i 's/\r$//' {} +

# The repo is cloned here by the Python runner; the package CLI orchestrates review.
ENV PYTHONPATH=/app/src
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
WORKDIR /workspace

ENTRYPOINT ["python3", "-m", "reviewforge"]
# Default subcommand when the image is run with no extra args. Overridden
# by ``podman run image <subcommand> ...`` to dispatch to other commands
# like ``post`` or ``discover``. Mirrors the no-argv default in
# cli.main() so the image is self-explanatory in isolation.
CMD ["review"]
