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

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN mkdir /data && chown app:app /data
COPY --from=build /app/publish/ ./

ENV ASPNETCORE_HTTP_PORTS=8080 \
    Realm__WorkDirectory=/data
EXPOSE 8080
VOLUME ["/data"]

USER app
ENTRYPOINT ["dotnet", "RotMGGameDataService.dll"]
CMD ["serve"]
