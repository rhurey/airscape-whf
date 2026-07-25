# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/AirscapeWhf/AirscapeWhf.csproj src/AirscapeWhf/
RUN dotnet restore src/AirscapeWhf/AirscapeWhf.csproj

COPY src/ src/
RUN dotnet publish src/AirscapeWhf/AirscapeWhf.csproj \
      -c Release \
      -o /app \
      --no-restore

# Chiseled: no shell, no package manager, non-root by default. This container talks
# to exactly one device on the LAN and serves one page; it has no business having
# anything else in it.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "AirscapeWhf.dll"]
