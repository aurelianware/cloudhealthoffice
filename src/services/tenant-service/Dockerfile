FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Build context is the repo root so all paths below are relative to it
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/services/tenant-service/TenantService.csproj", "./"]
RUN dotnet restore "TenantService.csproj"
COPY src/services/tenant-service/ .
RUN dotnet build "TenantService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TenantService.csproj" -c Release -o /app/publish

FROM base AS final

# Install bash and curl for SFTP provisioning scripts
RUN apt-get update && apt-get install -y \
    bash \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install kubectl
RUN curl -LO "https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl" \
    && chmod +x kubectl \
    && mv kubectl /usr/local/bin/

WORKDIR /app
COPY --from=publish /app/publish .

# Copy provisioning script directly from repo root (requires build context = repo root)
COPY scripts/setup/provision-sftp-tenant.sh /app/scripts/provision-sftp-tenant.sh
RUN chmod +x /app/scripts/provision-sftp-tenant.sh

# Create non-root user (kubectl and sftp scripts run as this user)
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "TenantService.dll"]
