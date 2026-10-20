# syntax=docker/dockerfile:1.6
# Build context: repository root.  docker build -f src/Dockmaster.Server/Dockerfile -t dockmaster-server .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f src/Dockmaster.Server/Dockerfile .
# The UI and the .NET code are built on the build host for the target architecture (no emulated compilers);
# only the final stage runs for the target platform.

# ---- React UI (platform-independent: built once, on the build host) ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS web
WORKDIR /web
COPY src/Dockmaster.Web/package.json src/Dockmaster.Web/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci
COPY src/Dockmaster.Web/ ./
RUN npm run build -- --outDir /web/dist --emptyOutDir

# ---- .NET server (cross-compiled on the build host) ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
# docker names the architectures amd64/arm64, .NET x64/arm64; the runtime image below is alpine, hence musl
RUN case "$TARGETARCH" in \
      amd64) echo linux-musl-x64 ;; \
      arm64) echo linux-musl-arm64 ;; \
      *) echo "unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac > /rid
WORKDIR /src
COPY nuget.config Directory.Build.props global.json ./
COPY src/Dockmaster.Shared/Dockmaster.Shared.csproj src/Dockmaster.Shared/
COPY src/Dockmaster.Server/Dockmaster.Server.csproj src/Dockmaster.Server/
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet restore src/Dockmaster.Server/Dockmaster.Server.csproj -r $(cat /rid)
COPY src/Dockmaster.Shared/ src/Dockmaster.Shared/
COPY src/Dockmaster.Server/ src/Dockmaster.Server/
ARG DOCKMASTER_VERSION=dev
# sharing=locked: the amd64 and arm64 stages restore at the same time and would trip over each other's
# half-extracted packages in a shared cache.
# No --no-restore: the restore layer can be cached while the package cache mount was pruned,
# so publish must be able to fetch missing packages again (NETSDK1064 otherwise).
RUN --mount=type=cache,target=/root/.nuget/packages,sharing=locked dotnet publish src/Dockmaster.Server/Dockmaster.Server.csproj -c Release -r $(cat /rid) --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
# tzdata: the schedules of jobs are in named time zones (Europe/Riga); alpine ships none
RUN apk add --no-cache tzdata
WORKDIR /app
COPY --from=build /app .
COPY --from=web /web/dist ./wwwroot
ENV DOCKMASTER_DATADIR=/data
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080 9443
VOLUME /data
ENTRYPOINT ["dotnet", "Dockmaster.Server.dll"]
