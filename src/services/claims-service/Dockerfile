ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching).
# Directory.Build.props pins minimum versions for CVE-2024-43485
# (System.Formats.Asn1 RCE) and CVE-2024-21907 (Newtonsoft.Json).
# MSBuild auto-imports it by walking up from the csproj, so it must
# exist at /repo/src/ before `dotnet restore` runs — otherwise the
# resolved graph silently uses older, vulnerable package versions.
COPY src/Directory.Build.props src/
COPY src/services/claims-service/claims-service.csproj src/services/claims-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Events/CloudHealthOffice.Events.csproj src/services/shared/CloudHealthOffice.Events/
# 5.5 — BenefitCalculationStage calls IBenefitCalculationEngine via the
# HttpBenefitCalculationEngineClient shim, which transitively pulls in
# BenefitEngine + OperatingMode at compile time.
COPY src/engines/CloudHealthOffice.BenefitEngine/CloudHealthOffice.BenefitEngine.csproj src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.OperatingMode/CloudHealthOffice.OperatingMode.csproj src/engines/CloudHealthOffice.OperatingMode/
# 5.4 — ScrubbingStage drives every claim through the shared scrub
# engine class library at adjudication pipeline Order=100.
COPY src/engines/CloudHealthOffice.ClaimsScrubEngine/CloudHealthOffice.ClaimsScrubEngine.csproj src/engines/CloudHealthOffice.ClaimsScrubEngine/
# 5.7 — NcciEditsStage drives every claim through the NCCI engine
# class library at adjudication pipeline Order=400.
COPY src/engines/CloudHealthOffice.NcciEngine/CloudHealthOffice.NcciEngine.csproj src/engines/CloudHealthOffice.NcciEngine/
# 5.8 — CoordinationOfBenefitsStage uses the CobEngine class library
# for payer-order rule labelling at adjudication pipeline Order=500.
COPY src/engines/CloudHealthOffice.CobEngine/CloudHealthOffice.CobEngine.csproj src/engines/CloudHealthOffice.CobEngine/
RUN dotnet restore src/services/claims-service/claims-service.csproj

# Copy source code and build
COPY src/services/claims-service/ src/services/claims-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Events/ src/services/shared/CloudHealthOffice.Events/
COPY src/engines/CloudHealthOffice.BenefitEngine/ src/engines/CloudHealthOffice.BenefitEngine/
COPY src/engines/CloudHealthOffice.OperatingMode/ src/engines/CloudHealthOffice.OperatingMode/
COPY src/engines/CloudHealthOffice.ClaimsScrubEngine/ src/engines/CloudHealthOffice.ClaimsScrubEngine/
COPY src/engines/CloudHealthOffice.NcciEngine/ src/engines/CloudHealthOffice.NcciEngine/
COPY src/engines/CloudHealthOffice.CobEngine/ src/engines/CloudHealthOffice.CobEngine/
RUN dotnet build src/services/claims-service/claims-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/claims-service/claims-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

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

ENTRYPOINT ["dotnet", "claims-service.dll"]
