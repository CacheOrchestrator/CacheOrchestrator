# Playground sample image (build from repository root):
#   docker build -f samples/CacheOrchestrator.Sample/Dockerfile -t cacheorchestrator-playground .
#
# Content root is writable so the sample settings UI can save appsettings.json.
# compose down / recreate restores image + lab Production overlays.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY global.json ./
COPY eng/ReleaseProjects.props eng/ReleaseProjects.props
COPY PACKAGE_RELEASE_NOTES.md ./
COPY docs/assets/logo-small.png docs/assets/logo-small.png
COPY eng/PublicApi/ eng/PublicApi/

# Project graph first for better layer caching (v3 package split + Redis leaf packages).
COPY src/CacheOrchestrator.Analyzers/CacheOrchestrator.Analyzers.csproj src/CacheOrchestrator.Analyzers/
COPY src/CacheOrchestrator.Core/CacheOrchestrator.Core.csproj src/CacheOrchestrator.Core/
COPY src/CacheOrchestrator.FusionCache/CacheOrchestrator.FusionCache.csproj src/CacheOrchestrator.FusionCache/
COPY src/CacheOrchestrator.AspNetCore/CacheOrchestrator.AspNetCore.csproj src/CacheOrchestrator.AspNetCore/
COPY src/CacheOrchestrator.Edge/CacheOrchestrator.Edge.csproj src/CacheOrchestrator.Edge/
COPY src/CacheOrchestrator.Edge.Varnish/CacheOrchestrator.Edge.Varnish.csproj src/CacheOrchestrator.Edge.Varnish/
COPY src/CacheOrchestrator.Redis.Shared/CacheOrchestrator.Redis.Shared.csproj src/CacheOrchestrator.Redis.Shared/
COPY src/CacheOrchestrator.AspNetCore.Redis/CacheOrchestrator.AspNetCore.Redis.csproj src/CacheOrchestrator.AspNetCore.Redis/
COPY src/CacheOrchestrator.FusionCache.Redis/CacheOrchestrator.FusionCache.Redis.csproj src/CacheOrchestrator.FusionCache.Redis/
COPY src/CacheOrchestrator.Redis/CacheOrchestrator.Redis.csproj src/CacheOrchestrator.Redis/
COPY src/CacheOrchestrator.HttpBus/CacheOrchestrator.HttpBus.csproj src/CacheOrchestrator.HttpBus/
COPY samples/CacheOrchestrator.Sample/CacheOrchestrator.Sample.csproj samples/CacheOrchestrator.Sample/

RUN dotnet restore samples/CacheOrchestrator.Sample/CacheOrchestrator.Sample.csproj

COPY src/CacheOrchestrator.Analyzers/ src/CacheOrchestrator.Analyzers/
COPY src/CacheOrchestrator.Core/ src/CacheOrchestrator.Core/
COPY src/CacheOrchestrator.FusionCache/ src/CacheOrchestrator.FusionCache/
COPY src/CacheOrchestrator.AspNetCore/ src/CacheOrchestrator.AspNetCore/
COPY src/CacheOrchestrator.Edge/ src/CacheOrchestrator.Edge/
COPY src/CacheOrchestrator.Edge.Varnish/ src/CacheOrchestrator.Edge.Varnish/
COPY src/CacheOrchestrator.Redis.Shared/ src/CacheOrchestrator.Redis.Shared/
COPY src/CacheOrchestrator.AspNetCore.Redis/ src/CacheOrchestrator.AspNetCore.Redis/
COPY src/CacheOrchestrator.FusionCache.Redis/ src/CacheOrchestrator.FusionCache.Redis/
COPY src/CacheOrchestrator.Redis/ src/CacheOrchestrator.Redis/
COPY src/CacheOrchestrator.HttpBus/ src/CacheOrchestrator.HttpBus/
COPY samples/CacheOrchestrator.Sample/ samples/CacheOrchestrator.Sample/

RUN dotnet publish samples/CacheOrchestrator.Sample/CacheOrchestrator.Sample.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# APP_UID owns /app so the entrypoint can replace appsettings.json with a symlink to /shared.
# Data/ holds the local SQLite file for single-instance runs (labs 01–02 and 06 / plain container).
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY samples/CacheOrchestrator.Sample/docker-entrypoint.sh /app/docker-entrypoint.sh
RUN mkdir -p /app/Data \
    && chown -R $APP_UID:$APP_UID /app /app/docker-entrypoint.sh /app/Data \
    && chmod 755 /app/docker-entrypoint.sh
USER $APP_UID

ENTRYPOINT ["/app/docker-entrypoint.sh"]
