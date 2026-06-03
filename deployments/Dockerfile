FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy configuration files
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/

ARG PROJECT

# Restore dependencies
RUN dotnet restore "src/${PROJECT}/${PROJECT}.csproj"

# Publish
RUN dotnet publish "src/${PROJECT}/${PROJECT}.csproj" \
    -c Release \
    -o /app \
    --no-restore \
    /p:UseAppHost=false \
    /p:DebugType=none \
    /p:DebugSymbols=false \
    /p:TreatWarningsAsErrors=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ARG PROJECT
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1 \
    DOTNET_TieredPGO=1
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 1000 hope \
    && useradd  --system --uid 1000 --gid 1000 --home /app --shell /usr/sbin/nologin hope
COPY --from=build --chown=hope:hope /app ./
EXPOSE 8080
ENV ENTRY_DLL=${PROJECT}.dll
USER 1000:1000
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/healthz/live || exit 1
ENTRYPOINT ["sh", "-c", "exec dotnet $ENTRY_DLL"]
