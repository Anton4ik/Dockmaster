# syntax=docker/dockerfile:1.6
# Build context: repository root.  docker build -f src/Dockmaster.Server/Dockerfile -t dockmaster-server .

# ---- React UI ----
FROM node:22-alpine AS web
WORKDIR /web
COPY src/Dockmaster.Web/package.json src/Dockmaster.Web/package-lock.json ./
RUN --mount=type=cache,target=/root/.npm npm ci
COPY src/Dockmaster.Web/ ./
RUN npm run build -- --outDir /web/dist --emptyOutDir

# ---- .NET server ----
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY nuget.config Directory.Build.props global.json ./
COPY src/Dockmaster.Shared/Dockmaster.Shared.csproj src/Dockmaster.Shared/
COPY src/Dockmaster.Server/Dockmaster.Server.csproj src/Dockmaster.Server/
RUN --mount=type=cache,target=/root/.nuget/packages dotnet restore src/Dockmaster.Server/Dockmaster.Server.csproj
COPY src/Dockmaster.Shared/ src/Dockmaster.Shared/
COPY src/Dockmaster.Server/ src/Dockmaster.Server/
ARG DOCKMASTER_VERSION=dev
# No --no-restore: the restore layer can be cached while the package cache mount was pruned,
# so publish must be able to fetch missing packages again (NETSDK1064 otherwise).
RUN --mount=type=cache,target=/root/.nuget/packages dotnet publish src/Dockmaster.Server/Dockmaster.Server.csproj -c Release -o /app

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
