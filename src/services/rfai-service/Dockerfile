ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/rfai-service/rfai-service.csproj src/services/rfai-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet restore src/services/rfai-service/rfai-service.csproj

# Copy source code and build
COPY src/services/rfai-service/ src/services/rfai-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet build src/services/rfai-service/rfai-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/rfai-service/rfai-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM ${REGISTRY}/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "rfai-service.dll"]
