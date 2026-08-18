# Multi-stage build (plan section 9.2). Build context is ./solution (docker-compose.yml at the
# repository root) -- every path below is relative to that root, not to this Dockerfile's own
# directory.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, project files only, so this layer caches across source-only changes.
# FirmaData.Build.slnf (plan section 9.1) lists all seven shipping projects and restores them
# together -- every .csproj is copied here even though this image only publishes FirmaData.Api,
# because `dotnet restore FirmaData.Build.slnf` restores the whole filter, not one project.
# `dotnet restore FirmaData.sln` is never used here: the full solution also lists the test
# projects, which are excluded from this context by .dockerignore, so that restore fails with
# MSB3202 (verified during Phase 0 -- see FirmaData.Build.slnf's own header).
COPY FirmaData.sln FirmaData.Build.slnf Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Backend/FirmaData.Domain/FirmaData.Domain.csproj src/Backend/FirmaData.Domain/
COPY src/Backend/FirmaData.Application/FirmaData.Application.csproj src/Backend/FirmaData.Application/
COPY src/Backend/FirmaData.Cvr/FirmaData.Cvr.csproj src/Backend/FirmaData.Cvr/
COPY src/Backend/FirmaData.Statbank/FirmaData.Statbank.csproj src/Backend/FirmaData.Statbank/
COPY src/Backend/FirmaData.Contracts/FirmaData.Contracts.csproj src/Backend/FirmaData.Contracts/
COPY src/Backend/FirmaData.Api/FirmaData.Api.csproj src/Backend/FirmaData.Api/
COPY src/Frontend/FirmaData.Web/FirmaData.Web.csproj src/Frontend/FirmaData.Web/
RUN dotnet restore FirmaData.Build.slnf

COPY src/ src/
RUN dotnet publish src/Backend/FirmaData.Api/FirmaData.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# .NET 10's aspnet image already runs as the non-root "app" user and listens on 8080 by default
# (ASPNETCORE_HTTP_PORTS, baked into the image) -- no extra USER/ENV needed for either.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is needed for docker-compose.yml's healthcheck (CMD curl against /health/ready) -- the
# base image doesn't ship it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FirmaData.Api.dll"]
