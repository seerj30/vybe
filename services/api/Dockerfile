# syntax=docker/dockerfile:1
# VYBE API image. Build from the repository root (the image also needs the content files):
#   docker build -f services/api/Dockerfile -t vybe-api .
# The API never migrates at start-up. Deploy steps run the same image with a command:
#   docker run --rm -e ConnectionStrings__Migrations=... vybe-api migrate
#   docker run --rm -e ConnectionStrings__Migrations=... vybe-api seed
# See docs/runbooks/deploy-and-rollback.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
# Restore first, so a source change does not invalidate the package layer.
COPY services/api/Directory.Build.props services/api/.editorconfig services/api/
COPY services/api/src/Vybe.Api/Vybe.Api.csproj services/api/src/Vybe.Api/
COPY services/api/src/Vybe.Data/Vybe.Data.csproj services/api/src/Vybe.Data/
COPY services/api/src/Vybe.GameRules/Vybe.GameRules.csproj services/api/src/Vybe.GameRules/
RUN dotnet restore services/api/src/Vybe.Api/Vybe.Api.csproj
COPY services/api/src services/api/src
RUN dotnet publish services/api/src/Vybe.Api/Vybe.Api.csproj -c Release -o /app --no-restore -p:UseAppHost=false

# Chiseled: no shell, no package manager, non-root user "app" (UID 1654); "extra" adds ICU and tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build /app .
# Rulesets, catalog and quests (ADR 0003 #6): read at start-up and by the seed command.
COPY assets/manifests content/assets/manifests
COPY packages/game-rules/fixtures/rulesets content/packages/game-rules/fixtures/rulesets
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Content__Root=/app/content \
    # No diagnostics IPC socket in /tmp: the container can run with a read-only root filesystem.
    DOTNET_EnableDiagnostics=0
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Vybe.Api.dll"]
