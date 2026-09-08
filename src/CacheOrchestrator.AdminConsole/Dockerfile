# CacheOrchestrator Admin Console
# Build from repository root:
#   docker build -f src/CacheOrchestrator.AdminConsole/Dockerfile -t cacheorchestrator-admin-console .
#
# Run (mount operator config + optional custom hints):
#   See deploy/admin/README.md

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY eng/ReleaseProjects.props eng/ReleaseProjects.props
COPY PACKAGE_RELEASE_NOTES.md ./
COPY docs/assets/logo-small.png docs/assets/logo-small.png
COPY eng/PublicApi/ eng/PublicApi/

# Project graph first for better layer caching (v3 package split).
COPY src/CacheOrchestrator.Analyzers/CacheOrchestrator.Analyzers.csproj src/CacheOrchestrator.Analyzers/
COPY src/CacheOrchestrator.Core/CacheOrchestrator.Core.csproj src/CacheOrchestrator.Core/
COPY src/CacheOrchestrator.FusionCache/CacheOrchestrator.FusionCache.csproj src/CacheOrchestrator.FusionCache/
COPY src/CacheOrchestrator.AspNetCore/CacheOrchestrator.AspNetCore.csproj src/CacheOrchestrator.AspNetCore/
COPY src/CacheOrchestrator.AdminConsole/CacheOrchestrator.AdminConsole.csproj src/CacheOrchestrator.AdminConsole/

RUN dotnet restore src/CacheOrchestrator.AdminConsole/CacheOrchestrator.AdminConsole.csproj

COPY src/CacheOrchestrator.Analyzers/ src/CacheOrchestrator.Analyzers/
COPY src/CacheOrchestrator.Core/ src/CacheOrchestrator.Core/
COPY src/CacheOrchestrator.FusionCache/ src/CacheOrchestrator.FusionCache/
COPY src/CacheOrchestrator.AspNetCore/ src/CacheOrchestrator.AspNetCore/
COPY src/CacheOrchestrator.AdminConsole/ src/CacheOrchestrator.AdminConsole/

# Optional: pass release version from CI (MinVer has no Git history in the image build context).
ARG VERSION=
RUN if [ -n "$VERSION" ]; then \
      dotnet publish src/CacheOrchestrator.AdminConsole/CacheOrchestrator.AdminConsole.csproj \
        -c Release -o /app/publish /p:UseAppHost=false \
        /p:MinVerVersionOverride=$VERSION; \
    else \
      dotnet publish src/CacheOrchestrator.AdminConsole/CacheOrchestrator.AdminConsole.csproj \
        -c Release -o /app/publish /p:UseAppHost=false; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

# Operator data: custom rule packs + Settings UI disable state (mount a volume here).
RUN mkdir -p /app/data/rules \
    && chown -R $APP_UID /app/data

EXPOSE 8080

COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
RUN mkdir -p /app/data/rules \
    && chown -R $APP_UID /app/data

USER $APP_UID

ENTRYPOINT ["dotnet", "CacheOrchestrator.AdminConsole.dll"]
