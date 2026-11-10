# Build from the repo root:  docker build -f src/FreeLog.Relay/Dockerfile -t freelog-relay .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FreeLog.Relay/FreeLog.Relay.csproj src/FreeLog.Relay/
RUN dotnet restore src/FreeLog.Relay/FreeLog.Relay.csproj
COPY src/FreeLog.Relay/ src/FreeLog.Relay/
RUN dotnet publish src/FreeLog.Relay/FreeLog.Relay.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "FreeLog.Relay.dll"]
