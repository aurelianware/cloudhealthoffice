# Build stage
# Build context must be the repo root so the CloudHealthOffice.Infrastructure
# project reference (../shared/...) is available during the build.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/claims-service/claims-service.csproj src/services/claims-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet restore src/services/claims-service/claims-service.csproj

# Copy source code and build
COPY src/services/claims-service/ src/services/claims-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet build src/services/claims-service/claims-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/claims-service/claims-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=publish /app/publish .

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

# Expose port
EXPOSE 8080

ENTRYPOINT ["dotnet", "claims-service.dll"]
