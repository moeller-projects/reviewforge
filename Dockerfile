# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

# ---------- restore: project files only → dependency layer stays cached across code changes ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS restore
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ReviewForge.Core/ReviewForge.Core.csproj src/ReviewForge.Core/
COPY src/ReviewForge.Infrastructure/ReviewForge.Infrastructure.csproj src/ReviewForge.Infrastructure/
COPY src/ReviewForge.Service/ReviewForge.Service.csproj src/ReviewForge.Service/
RUN dotnet restore src/ReviewForge.Service/ReviewForge.Service.csproj

# ---------- publish: only the service closure; tests never enter the image ----------
FROM restore AS publish
COPY src/ src/
RUN dotnet publish src/ReviewForge.Service/ReviewForge.Service.csproj \
      --no-restore \
      -c Release \
      -o /app/publish \
      /p:UseAppHost=false

# ---------- runtime: alpine (musl), non-root, healthcheck, read-only-rootfs ready ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime

LABEL org.opencontainers.image.title="reviewforge" \
      org.opencontainers.image.description="Automated PR review as a service: ADO context, agent reasoning, findings, triage, vote" \
      org.opencontainers.image.licenses="Proprietary"

# Persistent state lives in /var/reviewforge (volume): repo checkouts, findings.jsonl, SQLite store.
# Codex OAuth lives in /home/app/.codex (volume) — mounted read-write because token rotation
# rewrites auth.json atomically (temp + move).
RUN mkdir -p /var/reviewforge/work /home/app/.codex \
 && chown -R app:app /var/reviewforge /home/app/.codex

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    ReviewForge__WorkDir=/var/reviewforge/work \
    ReviewForge__StoreConnectionString="Data Source=/var/reviewforge/reviewforge.db"

USER app
WORKDIR /app
COPY --from=publish --chown=app:app /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
  CMD wget -qO- http://127.0.0.1:8080/health >/dev/null 2>&1 || exit 1

ENTRYPOINT ["dotnet", "ReviewForge.Service.dll"]
