# Build context is the repository root, so paths below are repo-relative.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore before copying the rest of the source: this layer is cached until the
# project file itself changes, so ordinary code edits skip the package download.
COPY src/Gateway/Gateway.csproj src/Gateway/
RUN dotnet restore src/Gateway/Gateway.csproj

COPY src/Gateway/ src/Gateway/
RUN dotnet publish src/Gateway/Gateway.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is not in the runtime image, and the compose healthcheck needs it.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The SQLite file lives inside the container: ephemeral by design for this assignment.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Runs as a non-root user, which the aspnet image provides.
USER $APP_UID

ENTRYPOINT ["dotnet", "Gateway.dll"]
