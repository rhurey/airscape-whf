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

# Chiseled: no shell, no package manager, non-root by default. This container talks
# to exactly one device on the LAN and serves one page; it has no business having
# anything else in it.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=publish /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "AirscapeWhf.dll"]
