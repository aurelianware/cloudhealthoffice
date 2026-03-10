# Build stage
# Build context must be the repo root so the CloudHealthOffice.BenefitEngine
# project reference (../../engines/...) is available during the build.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/benefit-plan-service/benefit-plan-service.csproj src/services/benefit-plan-service/
COPY src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine.csproj src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.FeeScheduleEngine/CloudHealthOffice.FeeScheduleEngine.csproj src/engines/CloudHealthOffice.FeeScheduleEngine/
COPY src/engines/CloudHealthOffice.NcciEngine/CloudHealthOffice.NcciEngine.csproj src/engines/CloudHealthOffice.NcciEngine/
RUN dotnet restore src/services/benefit-plan-service/benefit-plan-service.csproj

# Copy source code and build
COPY src/services/benefit-plan-service/ src/services/benefit-plan-service/
COPY src/engines/CloudHealthOffice.BenefitEngine/ src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.FeeScheduleEngine/ src/engines/CloudHealthOffice.FeeScheduleEngine/
COPY src/engines/CloudHealthOffice.NcciEngine/ src/engines/CloudHealthOffice.NcciEngine/
RUN dotnet build src/services/benefit-plan-service/benefit-plan-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/benefit-plan-service/benefit-plan-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Copy published app
COPY --from=publish /app/publish .

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "benefit-plan-service.dll"]
