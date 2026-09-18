ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/tenant-service/TenantService.csproj src/services/tenant-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/engines/CloudHealthOffice.OperatingMode/CloudHealthOffice.OperatingMode.csproj src/engines/CloudHealthOffice.OperatingMode/
RUN dotnet restore src/services/tenant-service/TenantService.csproj

# Copy source code and build
COPY src/services/tenant-service/ src/services/tenant-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/engines/CloudHealthOffice.OperatingMode/ src/engines/CloudHealthOffice.OperatingMode/
RUN dotnet build src/services/tenant-service/TenantService.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/tenant-service/TenantService.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM ${REGISTRY}/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y --no-install-recommends bash curl && rm -rf /var/lib/apt/lists/*

# Install kubectl
RUN curl -LO "https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl" \
    && chmod +x kubectl \
    && mv kubectl /usr/local/bin/

COPY --from=publish /app/publish .

# Copy provisioning script directly from repo root (requires build context = repo root)
COPY scripts/setup/provision-sftp-tenant.sh /app/scripts/provision-sftp-tenant.sh
RUN chmod +x /app/scripts/provision-sftp-tenant.sh

# Create non-root user with home directory for kubectl config (~/.kube)
RUN groupadd -r appuser && useradd -r -m -d /home/appuser -g appuser appuser \
    && chown -R appuser:appuser /app
ENV HOME=/home/appuser
USER appuser

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "TenantService.dll"]
