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
COPY prompts/ ./prompts/
COPY standards/ ./standards/
RUN chmod +x ./scripts/main.py ./scripts/review.py ./scripts/ado_review.py

# The repo is cloned here by the Python runner; main.py orchestrates review.
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
WORKDIR /workspace

ENTRYPOINT ["/app/scripts/main.py"]
