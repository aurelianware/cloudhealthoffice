FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Labels for container registry
LABEL org.opencontainers.image.title="CloudHealthOffice Pricing API"
LABEL org.opencontainers.image.description="Vendor-neutral claims repricing API"
LABEL org.opencontainers.image.vendor="Aurelianware, Inc."
LABEL org.opencontainers.image.source="https://github.com/aurelianware/cloudhealthoffice"

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CloudHealthOffice.PricingApi.dll"]
