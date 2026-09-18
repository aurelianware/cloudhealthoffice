ARG REGISTRY=mcr.microsoft.com
FROM ${REGISTRY}/dotnet/sdk:8.0-alpine AS build
WORKDIR /repo

# Copy csproj and restore (matches CI repo-root build context)
COPY src/services/CHO.TerminologyService/CHO.TerminologyService.csproj src/services/CHO.TerminologyService/
COPY src/services/shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet restore src/services/CHO.TerminologyService/CHO.TerminologyService.csproj

# Copy source and publish
COPY src/services/CHO.TerminologyService/ src/services/CHO.TerminologyService/
COPY src/services/shared/CloudHealthOffice.Infrastructure/ src/services/shared/CloudHealthOffice.Infrastructure/
RUN dotnet publish src/services/CHO.TerminologyService/CHO.TerminologyService.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM ${REGISTRY}/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Create directory for map file mounts
RUN mkdir -p /data/maps

COPY --from=build /app/publish .

RUN addgroup -S appuser && adduser -S appuser -G appuser \
    && chown -R appuser:appuser /app
USER appuser

ENV ASPNETCORE_URLS=http://+:5080

EXPOSE 5080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -qO- http://localhost:5080/health || exit 1

ENTRYPOINT ["dotnet", "CHO.TerminologyService.dll"]
