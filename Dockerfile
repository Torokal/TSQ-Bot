# TSQ Bot — production container image (docs/RAILWAY_DEPLOYMENT.md).
# Build with the SDK pinned by global.json; run on the .NET runtime only (generic host worker, no ASP.NET, no HTTP port).
# No secrets here: tokens are provided at runtime as environment variables (TOROSQUAD_*), never as ARG/ENV in this file.

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src

# Restore first for layer caching (central package versions + lock files, locked mode = reproducible).
# .editorconfig carries the analyzer settings: without it the Release build (warnings = errors) uses different rules.
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/ToroSquad.Core/ToroSquad.Core.csproj src/ToroSquad.Core/packages.lock.json src/ToroSquad.Core/
COPY src/ToroSquad.Infrastructure/ToroSquad.Infrastructure.csproj src/ToroSquad.Infrastructure/packages.lock.json src/ToroSquad.Infrastructure/
COPY src/ToroSquad.Discord/ToroSquad.Discord.csproj src/ToroSquad.Discord/packages.lock.json src/ToroSquad.Discord/
COPY src/ToroSquad.Modules.Esports/ToroSquad.Modules.Esports.csproj src/ToroSquad.Modules.Esports/packages.lock.json src/ToroSquad.Modules.Esports/
COPY src/ToroSquad.Modules.Example/ToroSquad.Modules.Example.csproj src/ToroSquad.Modules.Example/packages.lock.json src/ToroSquad.Modules.Example/
COPY src/ToroSquad.Bot/ToroSquad.Bot.csproj src/ToroSquad.Bot/packages.lock.json src/ToroSquad.Bot/
RUN dotnet restore src/ToroSquad.Bot/ToroSquad.Bot.csproj --locked-mode

COPY src/ src/

# The build context has no .git (see .dockerignore): the commit comes from Railway's build variable when available
# (embedded into the version as +sha) and otherwise from RAILWAY_GIT_COMMIT_SHA at runtime.
ARG RAILWAY_GIT_COMMIT_SHA=""
RUN TOROSQUAD_SOURCE_REVISION="$RAILWAY_GIT_COMMIT_SHA" dotnet publish src/ToroSquad.Bot/ToroSquad.Bot.csproj \
        -c Release -o /app/publish --no-restore \
        -p:EnableSourceControlManagerQueries=false -p:EnableSourceLink=false -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Production settings only. The SQLite database lives on the persistent volume mounted at /data (Railway volume);
# the bot refuses to start on Railway if no volume is attached or the database would be outside it.
ENV DOTNET_ENVIRONMENT=Production \
    TOROSQUAD_Bot__DataDirectory=/data

# Non-root by default (the image's "app" user). Railway mounts volumes as root: set RAILWAY_RUN_UID=0 on the service.
USER $APP_UID

# `run` is the default verb: validates configuration and storage, checks database integrity, applies migrations, then
# starts Discord and the workers. SIGTERM stops the host gracefully (Discord, pollers, outbox).
ENTRYPOINT ["dotnet", "ToroSquad.Bot.dll"]
