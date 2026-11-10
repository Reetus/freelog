# Build from the repo root:  docker build -f samples/DemoApp/Dockerfile -t freelog-demo .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FreeLog.Logging/ src/FreeLog.Logging/
COPY samples/DemoApp/ samples/DemoApp/
RUN dotnet publish samples/DemoApp/DemoApp.csproj -c Release -f net10.0 -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "DemoApp.dll"]
