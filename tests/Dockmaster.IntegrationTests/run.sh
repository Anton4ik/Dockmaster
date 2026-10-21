#!/bin/sh
# Runs the integration tests against this host's Docker engine, from a checkout of the repository:
#   tests/Dockmaster.IntegrationTests/run.sh [test filter]
# Use a host where test containers may come and go (they are all named dmit…, and removed at the end); it also prunes
# dangling images. Outputs of the agent (container page, imported compose...) are written to ./it-out, masked, to be
# compared between two versions.
set -e
SRC=$(cd "$(dirname "$0")/../.." && pwd)
docker build -q -t dm-it-runner -f "$SRC/tests/Dockmaster.IntegrationTests/runner.Dockerfile" "$SRC/tests/Dockmaster.IntegrationTests" >/dev/null
# the agent's data dir is mounted at the same path on the host, as compose needs it
mkdir -p /var/lib/dm-it "$SRC/it-out"
docker run --rm -e DOCKMASTER_IT=1 -e DOCKMASTER_IT_OUT=/out -e DOCKMASTER_IT_LOG="${DMLOG:-}" \
  -v /var/run/docker.sock:/var/run/docker.sock -v /var/lib/dm-it:/var/lib/dm-it -v /sys/fs/cgroup:/host/cgroup:ro \
  -v "$SRC":/src -v "$SRC/it-out":/out -v dm-it-nuget:/root/.nuget/packages -w /src \
  dm-it-runner dotnet test tests/Dockmaster.IntegrationTests --logger "console;verbosity=normal" ${1:+--filter "$1"}
