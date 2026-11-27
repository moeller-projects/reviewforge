# syntax=docker/dockerfile:1

# Pinned by digest (resolved 2026-11-25); Dependabot's docker ecosystem proposes bumps.
ARG DOTNET_VERSION=11.0
ARG SDK_DIGEST=sha256:52556c0fb9d0f609ae9aeab9efeeba96f4acc2f7038f7696f2235ccbdbb8e290
ARG ASPNET_DIGEST=sha256:d39b72ad1534fa0faf52b488bffb0762df7ba269d120f0338713d4dda2a41bf5

# ---------- restore: project files only → dependency layer stays cached across code changes ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine@${SDK_DIGEST} AS restore
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ReviewForge.Core/ReviewForge.Core.csproj src/ReviewForge.Core/packages.lock.json src/ReviewForge.Core/
COPY src/ReviewForge.Infrastructure/ReviewForge.Infrastructure.csproj src/ReviewForge.Infrastructure/packages.lock.json src/ReviewForge.Infrastructure/
COPY src/ReviewForge.Service/ReviewForge.Service.csproj src/ReviewForge.Service/packages.lock.json src/ReviewForge.Service/
RUN dotnet restore src/ReviewForge.Service/ReviewForge.Service.csproj --locked-mode

# ---------- publish: only the service closure; tests never enter the image ----------
FROM restore AS publish
COPY src/ src/
RUN dotnet publish src/ReviewForge.Service/ReviewForge.Service.csproj \
      --no-restore \
      -c Release \
      -o /app/publish \
      /p:UseAppHost=false

# ---------- runtime: alpine (musl), non-root, healthcheck, read-only-rootfs ready ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine@${ASPNET_DIGEST} AS runtime

LABEL org.opencontainers.image.title="reviewforge" \
      org.opencontainers.image.description="Automated PR review as a service: ADO context, agent reasoning, findings, triage, vote" \
      org.opencontainers.image.licenses="Proprietary"

# Persistent state lives in /var/reviewforge (volume): repo checkouts, per-run findings/{runId}.jsonl, SQLite store.
# Codex OAuth lives in /home/app/.codex (volume) — mounted read-write because token rotation
# rewrites auth.json atomically (temp + move).
RUN apk add --no-cache curl \
 && mkdir -p /var/reviewforge/work /home/app/.codex \
 && chown -R app:app /var/reviewforge /home/app/.codex

# Unprefixed Urls overrides config.toml after the host reapplies environment configuration.
# Certificates are runtime-only read-only mounts, never copied into the image.
ENV Urls=https://+:9090 \
    Kestrel__Certificates__Default__Path=/https/reviewforge.pfx \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    Workspace__WorkDir=/var/reviewforge/work \
    Persistence__StoreConnectionString="Data Source=/var/reviewforge/reviewforge.db"

USER app
WORKDIR /app
COPY --from=publish --chown=app:app /app/publish .

EXPOSE 9090

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD curl --fail --silent --show-error --cacert /https/ca.pem https://localhost:9090/health >/dev/null 2>&1 || exit 1

ENTRYPOINT ["dotnet", "ReviewForge.Service.dll"]
