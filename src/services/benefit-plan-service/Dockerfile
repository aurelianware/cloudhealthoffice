ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/benefit-plan-service/benefit-plan-service.csproj src/services/benefit-plan-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine.csproj src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.OperatingMode/CloudHealthOffice.OperatingMode.csproj src/engines/CloudHealthOffice.OperatingMode/
COPY src/engines/CloudHealthOffice.FeeScheduleEngine/CloudHealthOffice.FeeScheduleEngine.csproj src/engines/CloudHealthOffice.FeeScheduleEngine/
COPY src/engines/CloudHealthOffice.ReferenceData/CloudHealthOffice.ReferenceData.csproj src/engines/CloudHealthOffice.ReferenceData/
COPY src/engines/CloudHealthOffice.ClaimsScrubEngine/CloudHealthOffice.ClaimsScrubEngine.csproj src/engines/CloudHealthOffice.ClaimsScrubEngine/
COPY src/engines/CloudHealthOffice.NcciEngine/CloudHealthOffice.NcciEngine.csproj src/engines/CloudHealthOffice.NcciEngine/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/CloudHealthOffice.ProviderEnrollmentService.csproj src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/CloudHealthOffice.PriorAuthRuleEngine.csproj src/engines/CloudHealthOffice.PriorAuthRuleEngine/
RUN dotnet restore src/services/benefit-plan-service/benefit-plan-service.csproj

# Copy source code and build
COPY src/services/benefit-plan-service/ src/services/benefit-plan-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/engines/CloudHealthOffice.BenefitEngine/ src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.OperatingMode/ src/engines/CloudHealthOffice.OperatingMode/
COPY src/engines/CloudHealthOffice.FeeScheduleEngine/ src/engines/CloudHealthOffice.FeeScheduleEngine/
COPY src/engines/CloudHealthOffice.ReferenceData/ src/engines/CloudHealthOffice.ReferenceData/
COPY src/engines/CloudHealthOffice.ClaimsScrubEngine/ src/engines/CloudHealthOffice.ClaimsScrubEngine/
COPY src/engines/CloudHealthOffice.NcciEngine/ src/engines/CloudHealthOffice.NcciEngine/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/ src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/ src/engines/CloudHealthOffice.PriorAuthRuleEngine/
# benefit-plan-service.csproj declares <Content Include="..\..\..\schemas\..."> for
# the SystemDefaultMappingSeeder seed bundle (BP 5.6) and the AcaLimitsProvider OOP
# limits file (BP 5.7); MSBuild copies them to the build output via
# CopyToOutputDirectory="PreserveNewest". Without these COPY lines the build fails
# with MSB3030 "Could not copy the file ... because it was not found."
COPY schemas/service-category-mappings/ schemas/service-category-mappings/
COPY schemas/aca-oop-limits/ schemas/aca-oop-limits/
RUN dotnet build src/services/benefit-plan-service/benefit-plan-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/benefit-plan-service/benefit-plan-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

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

ENTRYPOINT ["dotnet", "benefit-plan-service.dll"]
