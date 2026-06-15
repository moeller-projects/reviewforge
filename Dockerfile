# target path: pr-review-bot/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR through direct Azure DevOps REST calls. Build once, run per PR.
FROM node:24-bookworm-slim

# Pin tool versions for reproducible reviews. Bump deliberately.
ARG PI_VERSION=0.79.1

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates git python3 ripgrep \
 && rm -rf /var/lib/apt/lists/*

# Global CLI: the Pi coding agent. Azure DevOps integration uses direct REST via Python.
RUN npm install -g --ignore-scripts \
      "@earendil-works/pi-coding-agent@${PI_VERSION}"

WORKDIR /app
COPY scripts/ ./scripts/
COPY src/ ./src/
COPY prompts/ ./prompts/
COPY standards/ ./standards/

# Strip Windows CRLF line endings from executable scripts. Editors on
# Windows hosts can write CRLF into .py files even when the repo's
# .gitattributes specifies eol=lf. The shebang on scripts/main.py is
# parsed by the kernel (not Python), so a trailing \r turns
# ``python3`` into ``python3\r`` and the container fails at exec time
# with exit 127. Idempotent on already-LF files.
RUN find ./scripts ./src -type f \( -name '*.py' -o -name '*.sh' \) -exec sed -i 's/\r$//' {} +

RUN chmod +x ./scripts/main.py ./scripts/review.py ./scripts/ado_review.py

# The repo is cloned here by the Python runner; main.py orchestrates review.
ENV PYTHONPATH=/app/src
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
WORKDIR /workspace

ENTRYPOINT ["/app/scripts/main.py"]
# Default subcommand when the image is run with no extra args. Overridden
# by ``podman run image <subcommand> ...`` to dispatch to other commands
# like ``post`` or ``discover``. Mirrors the no-argv default in
# cli.main() so the image is self-explanatory in isolation.
CMD ["review"]
