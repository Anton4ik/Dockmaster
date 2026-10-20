# syntax=docker/dockerfile:1.6
# Build context: repository root.  docker build -f src/Dockmaster.Agent/Dockerfile -t dockmaster-agent .
# Multi-arch: docker buildx build --platform linux/amd64,linux/arm64 -f src/Dockmaster.Agent/Dockerfile .
# The .NET code is cross-compiled on the build host; only the final stage runs for the target platform.
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
COPY src/Dockmaster.Agent/Dockmaster.Agent.csproj src/Dockmaster.Agent/
RUN dotnet restore src/Dockmaster.Agent/Dockmaster.Agent.csproj -r $(cat /rid)
# only what the agent is built from: changes to the server or the UI keep this layer cached
COPY src/Dockmaster.Shared/ src/Dockmaster.Shared/
COPY src/Dockmaster.Agent/ src/Dockmaster.Agent/
ARG DOCKMASTER_VERSION=dev
RUN dotnet publish src/Dockmaster.Agent/Dockmaster.Agent.csproj -c Release -r $(cat /rid) --self-contained false -o /app --no-restore

# docker CLI + compose plugin come from the official image (multi-arch: resolved for the target platform)
FROM docker:cli AS dockercli

FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine
# wireguard-tools / wireguard-go / iproute2 / iptables / iperf3 / haproxy: the same image runs the mesh sidecar (see mesh/dm-mesh);
# iptables-legacy for hosts whose Docker still uses it (older kernels without nf_tables, e.g. Synology);
# iputils for a ping with Don't-Fragment (busybox's cannot), which measures the MTU of the path to each peer
RUN apk add --no-cache ca-certificates wireguard-tools wireguard-go iproute2 iptables iptables-legacy iperf3 haproxy iputils
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=dockercli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
COPY --chmod=755 src/Dockmaster.Agent/mesh/dm-mesh /usr/local/bin/dm-mesh
WORKDIR /app
COPY --from=build /app .
# data dir: bind-mount it at the same path on the host (-v /var/lib/dockmaster:/var/lib/dockmaster)
ENV DOCKMASTER_DATADIR=/var/lib/dockmaster
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Dockmaster.Agent.dll"]
