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
COPY src/services/claims-examiner-service/claims-examiner-service.csproj src/services/claims-examiner-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Events/CloudHealthOffice.Events.csproj src/services/shared/CloudHealthOffice.Events/
RUN dotnet restore src/services/claims-examiner-service/claims-examiner-service.csproj

# Copy source code and build
COPY src/services/claims-examiner-service/ src/services/claims-examiner-service/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
COPY src/services/shared/CloudHealthOffice.Events/ src/services/shared/CloudHealthOffice.Events/
RUN dotnet build src/services/claims-examiner-service/claims-examiner-service.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/services/claims-examiner-service/claims-examiner-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

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

ENTRYPOINT ["dotnet", "claims-examiner-service.dll"]
