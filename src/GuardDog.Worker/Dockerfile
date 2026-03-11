# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
# .NET 10 uses .slnx (XML solution format) instead of .sln
COPY GuardDog.slnx .
COPY src/GuardDog.Core/GuardDog.Core.csproj           src/GuardDog.Core/
COPY src/GuardDog.Worker/GuardDog.Worker.csproj        src/GuardDog.Worker/
COPY src/GuardDog.SnapshotTool/GuardDog.SnapshotTool.csproj src/GuardDog.SnapshotTool/
COPY tests/GuardDog.Core.Tests/GuardDog.Core.Tests.csproj tests/GuardDog.Core.Tests/

RUN dotnet restore src/GuardDog.Worker/GuardDog.Worker.csproj

# Copy source and publish
COPY src/ src/
RUN dotnet publish src/GuardDog.Worker/GuardDog.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Phase 5 Security: Run as a non-root user
RUN addgroup --system guarddog && adduser --system --ingroup guarddog guarddog
USER guarddog

COPY --from=build /app/publish .

# Phase 2: Copy the Shadow Schema snapshot produced by GuardDog.SnapshotTool
# In CI, this file is downloaded as an artifact before docker build runs.
# If absent, the worker will log a warning and compare against an empty snapshot.
COPY --chown=guarddog:guarddog src/GuardDog.Worker/snapshot.json* ./

# Prometheus metrics port
EXPOSE 9090

# Phase 5: Connection string injected at runtime via environment variable
# Azure: set this to "Authentication=Active Directory Default;" (Managed Identity)
# AWS:   use IAM role — the SDK picks it up automatically, no secret needed
ENV GuardDog__ConnectionString=""
ENV GuardDog__Provider="SqlServer"
ENV GuardDog__CheckInterval="00:05:00"
ENV GuardDog__AlertThreshold="Warning"
ENV GuardDog__Slack__WebhookUrl=""
ENV GuardDog__Teams__WebhookUrl=""

ENTRYPOINT ["dotnet", "GuardDog.Worker.dll"]
