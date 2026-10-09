# Service image. Build from the repository root:
#   docker build -f src/Wayfarer.Host/Dockerfile .
# Checked-in appsettings stay fail-closed. The shared compose injects peer URLs.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Wayfarer.Host/Wayfarer.Host.csproj -c Release -o /app/publish --no-self-contained /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Wayfarer.Host.dll"]
