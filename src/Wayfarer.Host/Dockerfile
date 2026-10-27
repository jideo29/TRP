# Service image. Build context is a directory already produced by
# `dotnet publish` (it must contain Wayfarer.Host.dll at its root):
#   dotnet publish src/Wayfarer.Host/Wayfarer.Host.csproj -c Release -o <dir> --no-self-contained /p:UseAppHost=false
#   docker build -f src/Wayfarer.Host/Dockerfile -t wayfarer-host <dir>
# Checked-in appsettings stay fail-closed. The shared compose injects peer URLs.
# CI publishes on the runner and passes that directory as the context. Run
# 40413482856 cancelled the image job at 30 minutes while `dotnet publish`
# inside the sandbox dind was still at "Determining projects to restore...".
# The same run restored on the runner in seconds, so this file does not
# restore or compile. 14.1.1: the runtime base is pinned by manifest-list digest
# (resolved 2026-10-27); Dependabot proposes digest updates.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
WORKDIR /app
COPY --chown=app:app . .
# Fail the build when the context is the repository instead of the publish output.
RUN test -f Wayfarer.Host.dll
USER app
# X3: https only. The serving certificate is mounted at deploy time
# (Kestrel__Certificates__Default__Path and its password from the secret store).
# HTTP_PORTS is cleared so the base image's plaintext 8080 default is gone; the
# start-up baseline refuses any http listener outside Development.
ENV ASPNETCORE_HTTP_PORTS= \
    ASPNETCORE_URLS=https://+:8443
EXPOSE 8443
ENTRYPOINT ["dotnet", "Wayfarer.Host.dll"]
