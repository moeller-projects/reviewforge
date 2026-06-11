# target path: pr-review-bot/Dockerfile
# Reviewer container: runs Pi read-only to produce findings, then posts them to
# the PR via @azure-devops/mcp. Build once, run per PR from the pipeline.
FROM node:24-bookworm-slim

# Pin tool versions for reproducible reviews. Bump deliberately.
ARG PI_VERSION=0.79.1
ARG ADO_MCP_VERSION=2.7.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends bash ca-certificates curl git jq ripgrep \
 && rm -rf /var/lib/apt/lists/*

# Global CLIs: the Pi coding agent and the official Azure DevOps MCP server.
RUN npm install -g --ignore-scripts \
      "@earendil-works/pi-coding-agent@${PI_VERSION}" \
      "@azure-devops/mcp@${ADO_MCP_VERSION}"

WORKDIR /app
# Poster dependency (MCP client SDK) installed into /app/node_modules.
COPY package.json ./
RUN npm install --omit=dev --ignore-scripts

COPY scripts/ ./scripts/
COPY prompts/ ./prompts/
COPY standards/ ./standards/
RUN chmod +x ./scripts/review.sh

# The repo is bind-mounted here by the pipeline; review.sh diffs and reviews it.
ENV WORKSPACE=/workspace
ENV PI_SKIP_VERSION_CHECK=1 PI_TELEMETRY=0
WORKDIR /workspace

ENTRYPOINT ["/app/scripts/review.sh"]
