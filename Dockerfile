# ConnectionWatcher container: engine + SQLite + web UI in one process.
# Build:  docker build -t conwatch .
# Run:    see deploy/docker/compose.yaml (data volume + NET_RAW note)

# --- Stage 1: build the JS islands with Vite ---
FROM node:22-alpine AS client
WORKDIR /src/ClientApp
COPY Programs/ConWatch/ClientApp/package*.json ./
RUN npm ci
COPY Programs/ConWatch/ClientApp/ ./
RUN npm run build
# vite outDir is ../wwwroot/js -> /src/wwwroot/js

# --- Stage 2: publish the .NET app (client build skipped; bundles copied in) ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=client /src/wwwroot/js Programs/ConWatch/wwwroot/js
RUN dotnet publish Programs/ConWatch -c Release -o /app /p:SkipClientBuild=true

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
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/api/v1/health || exit 1
ENTRYPOINT ["dotnet", "ConWatch.dll"]
