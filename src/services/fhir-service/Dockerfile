ARG REGISTRY=clouhealthoffice.azurecr.io
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/fhir-service/fhir-service.csproj src/services/fhir-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Appeals.Contracts/CloudHealthOffice.Appeals.Contracts.csproj src/services/shared/CloudHealthOffice.Appeals.Contracts/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/CloudHealthOffice.ProviderEnrollmentService.csproj src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/CloudHealthOffice.PriorAuthRuleEngine.csproj src/engines/CloudHealthOffice.PriorAuthRuleEngine/
RUN dotnet restore src/services/fhir-service/fhir-service.csproj

# Copy source code and build
COPY src/services/fhir-service/ src/services/fhir-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Appeals.Contracts/ src/services/shared/CloudHealthOffice.Appeals.Contracts/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/ src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/ src/engines/CloudHealthOffice.PriorAuthRuleEngine/
# CHO-authored FHIR profile artifacts — embedded into fhir-service.dll at
# build time via the <EmbeddedResource Include="...docs/fhir/profiles/*.json">
# item in fhir-service.csproj (logical-name prefix `FhirArtifacts.`).
# ChoFhirArtifactRegistry reads them at startup via GetManifestResourceStream.
COPY docs/fhir/profiles/ docs/fhir/profiles/
RUN dotnet build src/services/fhir-service/fhir-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/fhir-service/fhir-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

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

ENTRYPOINT ["dotnet", "fhir-service.dll"]
