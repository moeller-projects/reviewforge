# target path: pr-review-bot/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR through direct Azure DevOps REST calls. Build once, run per PR.
FROM node:24-bookworm-slim

# Pin tool versions for reproducible reviews. Bump deliberately.
ARG PI_VERSION=0.79.1

RUN apt-get update \
 && apt-get install -y --no-install-recommends bash ca-certificates curl git jq python3 ripgrep \
 && rm -rf /var/lib/apt/lists/*

# Global CLI: the Pi coding agent. Azure DevOps integration uses direct REST via Python.
RUN npm install -g --ignore-scripts \
      "@earendil-works/pi-coding-agent@${PI_VERSION}"

WORKDIR /app
# Keep Node dependencies available for legacy utilities/tests during migration.
COPY package.json ./
RUN npm install --omit=dev --ignore-scripts

COPY scripts/ ./scripts/
COPY prompts/ ./prompts/
COPY standards/ ./standards/
RUN chmod +x ./scripts/review.sh ./scripts/ado_review.py

# The repo is bind-mounted here by the pipeline; review.sh diffs and reviews it.
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
WORKDIR /workspace

ENTRYPOINT ["/app/scripts/review.sh"]
