# ConnectionWatcher container: engine + SQLite + web UI in one process.
# Build:  docker build -t conwatch .           (current architecture)
#         .\Build-Images.ps1                   (amd64 + arm64, see script)
# Run:    see deploy/docker/compose.yaml (data volume + NET_RAW note)
#
# Multi-arch: the build stages are pinned to the BUILD machine's architecture
# and cross-compile for TARGETARCH, so an arm64 image (Synology/Pi) builds at
# native speed instead of under QEMU emulation. Only the runtime stage is the
# target architecture.

# --- Stage 1: build the JS islands with Vite (arch-independent output) ---
FROM --platform=$BUILDPLATFORM node:22-alpine AS client
WORKDIR /src/ClientApp
COPY Programs/ConWatch/ClientApp/package*.json ./
RUN npm ci
COPY Programs/ConWatch/ClientApp/ ./
RUN npm run build
# vite outDir is ../wwwroot/js -> /src/wwwroot/js

# --- Stage 2: publish the .NET app (client build skipped; bundles copied in) ---
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# CI passes the commit hash (the build context has no .git, so the version
# stamp would otherwise be bare); the SDK appends it as +<sha> to
# AssemblyInformationalVersion, which the header build stamp reads.
ARG GIT_SHA=
WORKDIR /src
COPY . .
COPY --from=client /src/wwwroot/js Programs/ConWatch/wwwroot/js
# -a maps docker arch names (amd64/arm64) to .NET RIDs; framework-dependent,
# the aspnet base image below supplies the matching runtime.
# ReadyToRun precompiles the app's IL so startup skips most JIT work —
# worth the fatter layer on the NAS's modest CPU.
RUN dotnet publish Programs/ConWatch -c Release -a $TARGETARCH -o /app \
    /p:SkipClientBuild=true /p:PublishReadyToRun=true \
    ${GIT_SHA:+/p:SourceRevisionId=$GIT_SHA}

# --- Stage 3: runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl is only for the container HEALTHCHECK.
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV Persistence__DbPath=/app/data/conwatch.db
VOLUME /app/data
EXPOSE 8080
# Older engines (Synology) run the FIRST probe only after --interval, and
# their UI gates on "healthy" — a long interval makes a 2-second app look
# unready for half a minute. 10s keeps that gate short at negligible cost.
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/v1/health || exit 1
ENTRYPOINT ["dotnet", "ConWatch.dll"]
