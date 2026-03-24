# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

COPY src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj src/portal/CloudHealthOffice.Portal/
RUN dotnet restore src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj

COPY src/portal/CloudHealthOffice.Portal/ src/portal/CloudHealthOffice.Portal/
RUN dotnet build src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/portal/CloudHealthOffice.Portal/CloudHealthOffice.Portal.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -r appuser && useradd -r -g appuser appuser
USER appuser

COPY --from=publish /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=10 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CloudHealthOffice.Portal.dll"]
