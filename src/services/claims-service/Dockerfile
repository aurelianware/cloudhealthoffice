# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy shared library first
COPY shared/CloudHealthOffice.Infrastructure/CloudHealthOffice.Infrastructure.csproj shared/CloudHealthOffice.Infrastructure/
# Copy service project
COPY claims-service/claims-service.csproj claims-service/
RUN dotnet restore claims-service/claims-service.csproj

# Copy all source and build
COPY shared/CloudHealthOffice.Infrastructure/ shared/CloudHealthOffice.Infrastructure/
COPY claims-service/ claims-service/
RUN dotnet build claims-service/claims-service.csproj -c Release -o /app/build
RUN dotnet publish claims-service/claims-service.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Copy published app
COPY --from=build /app/publish .

# Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser \
    && chown -R appuser:appuser /app
USER appuser

# Expose port
EXPOSE 8080

# Run the application
ENTRYPOINT ["dotnet", "claims-service.dll"]
