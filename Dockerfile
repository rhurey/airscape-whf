# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY AirscapeWhf.sln ./
COPY src/AirscapeWhf/AirscapeWhf.csproj src/AirscapeWhf/
COPY tests/AirscapeWhf.Tests/AirscapeWhf.Tests.csproj tests/AirscapeWhf.Tests/
RUN dotnet restore AirscapeWhf.sln

COPY src/ src/
COPY tests/ tests/

# `docker build --target test .` runs the suite; the default build runs it too,
# because publish depends on this stage.
FROM build AS test
RUN dotnet test AirscapeWhf.sln -c Release --no-restore \
 && touch /tested

FROM build AS publish
COPY --from=test /tested /tested
RUN dotnet publish src/AirscapeWhf/AirscapeWhf.csproj \
      -c Release \
      -o /app \
      --no-restore

# Chiseled: no shell, no package manager, non-root (UID 1654) by default. This
# container talks to exactly one device on the LAN and serves one page; it has no
# business having anything else in it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime

LABEL org.opencontainers.image.title="airscape-whf" \
      org.opencontainers.image.description="Web UI, REST API and MCP server for AirScape Gen 2 whole house fans" \
      org.opencontainers.image.source="https://github.com/rhurey/airscape-whf" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY --from=publish /app .

# Listeners are configured in code (Http:Port, Https:Port), so clear the base
# image's ASPNETCORE_HTTP_PORTS rather than have Kestrel warn about overriding it.
ENV ASPNETCORE_HTTP_PORTS= \
    DOTNET_EnableDiagnostics=0 \
    AIRSCAPE_CONFIG_DIR=/config \
    Cooldown__Path=/data/cooldown.json

# /config: appsettings.json, certs/, mcp-token (mount read-only)
# /data:   cooldown.json (mount read-write, owned by UID 1654)
VOLUME ["/config", "/data"]
EXPOSE 8080 8443

# No shell or curl in a chiseled image, so the app checks itself.
HEALTHCHECK --interval=60s --timeout=5s --start-period=15s --retries=3 \
    CMD ["dotnet", "AirscapeWhf.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "AirscapeWhf.dll"]
