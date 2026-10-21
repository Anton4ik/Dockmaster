# The image the integration tests run in: the .NET SDK with the docker CLI and compose, like the agent's own image.
#   docker build -t dm-it-runner -f tests/Dockmaster.IntegrationTests/runner.Dockerfile tests/Dockmaster.IntegrationTests
FROM docker:cli AS dockercli
FROM mcr.microsoft.com/dotnet/sdk:9.0
COPY --from=dockercli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=dockercli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
