# Service image. Build from the repository root:
#   docker build -f src/Wayfarer.Host/Dockerfile .
# Checked-in appsettings stay fail-closed. The shared compose injects peer URLs.
# 14.1.1: base images pinned by manifest-list digest (resolved 2026-10-27); Dependabot raises the updates.
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Wayfarer.Host/Wayfarer.Host.csproj -c Release -o /app/publish --no-self-contained /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee AS runtime
WORKDIR /app
COPY --from=build --chown=app:app /app/publish .
USER app
# X3: https only. The serving certificate is mounted at deploy time
# (Kestrel__Certificates__Default__Path and its password from the secret store).
# HTTP_PORTS is cleared so the base image's plaintext 8080 default is gone; the
# start-up baseline refuses any http listener outside Development.
ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_URLS=https://+:8443
EXPOSE 8443
ENTRYPOINT ["dotnet", "Wayfarer.Host.dll"]
