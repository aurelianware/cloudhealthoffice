ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo
# Central Package Management: every PackageReference resolves its version from
# Directory.Packages.props, so restore fails without it in the build context.
COPY Directory.Packages.props ./
# Required by src/Directory.Build.props: retain the repository-wide warning gate.
COPY Directory.Build.props ./
# Carries the CVE-motivated pins applied to every project under src/.
COPY src/Directory.Build.props src/

# Copy csproj files and restore (preserves layer caching)
COPY src/services/fhir-service/fhir-service.csproj src/services/fhir-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Appeals.Contracts/CloudHealthOffice.Appeals.Contracts.csproj src/services/shared/CloudHealthOffice.Appeals.Contracts/
COPY src/services/shared/CloudHealthOffice.Consent.Contracts/CloudHealthOffice.Consent.Contracts.csproj src/services/shared/CloudHealthOffice.Consent.Contracts/
COPY src/services/shared/CloudHealthOffice.Events/CloudHealthOffice.Events.csproj src/services/shared/CloudHealthOffice.Events/
COPY src/services/shared/CloudHealthOffice.PlanConfiguration/CloudHealthOffice.PlanConfiguration.csproj src/services/shared/CloudHealthOffice.PlanConfiguration/
COPY src/engines/CloudHealthOffice.ManagedFhirStore/CloudHealthOffice.ManagedFhirStore.csproj src/engines/CloudHealthOffice.ManagedFhirStore/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/CloudHealthOffice.ProviderEnrollmentService.csproj src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/CloudHealthOffice.PriorAuthRuleEngine.csproj src/engines/CloudHealthOffice.PriorAuthRuleEngine/
COPY src/engines/CloudHealthOffice.DocumentStore/CloudHealthOffice.DocumentStore.csproj src/engines/CloudHealthOffice.DocumentStore/
RUN dotnet restore src/services/fhir-service/fhir-service.csproj

# Copy source code and build
COPY src/services/fhir-service/ src/services/fhir-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Appeals.Contracts/ src/services/shared/CloudHealthOffice.Appeals.Contracts/
COPY src/services/shared/CloudHealthOffice.Consent.Contracts/ src/services/shared/CloudHealthOffice.Consent.Contracts/
COPY src/services/shared/CloudHealthOffice.Events/ src/services/shared/CloudHealthOffice.Events/
COPY src/services/shared/CloudHealthOffice.PlanConfiguration/ src/services/shared/CloudHealthOffice.PlanConfiguration/
COPY src/engines/CloudHealthOffice.ManagedFhirStore/ src/engines/CloudHealthOffice.ManagedFhirStore/
COPY src/engines/CloudHealthOffice.ProviderEnrollmentService/ src/engines/CloudHealthOffice.ProviderEnrollmentService/
COPY src/engines/CloudHealthOffice.PriorAuthRuleEngine/ src/engines/CloudHealthOffice.PriorAuthRuleEngine/
COPY src/engines/CloudHealthOffice.DocumentStore/ src/engines/CloudHealthOffice.DocumentStore/
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
