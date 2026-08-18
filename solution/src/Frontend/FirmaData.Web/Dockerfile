# Multi-stage build (plan section 9.2). Build context is ./solution (docker-compose.yml at the
# repository root) -- every path below is relative to that root, not to this Dockerfile's own
# directory. Mirrors FirmaData.Api/Dockerfile's restore layer exactly (same FirmaData.Build.slnf,
# same seven .csproj files), since restoring the filter always restores all of it -- only the
# publish target below differs.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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
RUN dotnet publish src/Frontend/FirmaData.Web/FirmaData.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

# .NET 10's aspnet image already runs as the non-root "app" user and listens on 8080 by default
# (ASPNETCORE_HTTP_PORTS, baked into the image).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "FirmaData.Web.dll"]
