ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj and restore (preserves layer caching)
COPY src/services/CloudHealthOffice.PricingApi/CloudHealthOffice.PricingApi.csproj src/services/CloudHealthOffice.PricingApi/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet restore src/services/CloudHealthOffice.PricingApi/CloudHealthOffice.PricingApi.csproj

# Copy source code and build
COPY src/services/CloudHealthOffice.PricingApi/ src/services/CloudHealthOffice.PricingApi/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet build src/services/CloudHealthOffice.PricingApi/CloudHealthOffice.PricingApi.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/CloudHealthOffice.PricingApi/CloudHealthOffice.PricingApi.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM ${REGISTRY}/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

LABEL org.opencontainers.image.title="CloudHealthOffice Pricing API"
LABEL org.opencontainers.image.description="Vendor-neutral claims repricing API"
LABEL org.opencontainers.image.vendor="Aurelianware, Inc."
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CloudHealthOffice.PricingApi.dll"]
