# Build context must be the repo root so the CloudHealthOffice.DocumentStore
# project reference (../../engines/...) is available during the build.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy csproj files and restore (preserves layer caching)
COPY src/services/attachment-service/attachment-service.csproj src/services/attachment-service/
COPY src/engines/CloudHealthOffice.DocumentStore/CloudHealthOffice.DocumentStore.csproj src/engines/CloudHealthOffice.DocumentStore/
RUN dotnet restore src/services/attachment-service/attachment-service.csproj

# Copy source code and build
COPY src/services/attachment-service/ src/services/attachment-service/
COPY src/engines/CloudHealthOffice.DocumentStore/ src/engines/CloudHealthOffice.DocumentStore/
RUN dotnet build src/services/attachment-service/attachment-service.csproj -c Release --no-restore

# Publish stage
FROM build AS publish
RUN dotnet publish src/services/attachment-service/attachment-service.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore --no-build

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080

# Copy published app
COPY --from=publish /app/publish .

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

ENTRYPOINT ["dotnet", "attachment-service.dll"]
