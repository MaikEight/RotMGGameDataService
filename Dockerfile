FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

COPY global.json Directory.Build.props ./
COPY vendor/RotMGAssetExtractor/ vendor/RotMGAssetExtractor/
COPY src/RotMGGameDataService/ src/RotMGGameDataService/

RUN dotnet restore src/RotMGGameDataService/RotMGGameDataService.csproj \
    /p:TargetFramework=net8.0
RUN dotnet publish src/RotMGGameDataService/RotMGGameDataService.csproj \
    --configuration Release \
    --framework net8.0 \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# The work directory has to be staged here: the chiseled runtime image ships no
# shell, so RUN is unavailable in the final stage.
RUN mkdir -p /staging/data

# Chiseled: no shell, no package manager, and it already runs as UID 1654 with
# DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true, which matches the project's
# InvariantGlobalization setting.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled AS runtime
WORKDIR /app

COPY --from=build --chown=1654:1654 /staging/data /data
COPY --from=build /app/publish/ ./

ENV Realm__WorkDirectory=/data
EXPOSE 8080

ENTRYPOINT ["/usr/bin/dotnet", "RotMGGameDataService.dll"]
CMD ["serve"]
