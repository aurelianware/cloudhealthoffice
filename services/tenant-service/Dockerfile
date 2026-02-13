FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["TenantService.csproj", "./"]
RUN dotnet restore "TenantService.csproj"
COPY . .
RUN dotnet build "TenantService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TenantService.csproj" -c Release -o /app/publish

FROM base AS final

# Install bash and kubectl for SFTP provisioning scripts
RUN apt-get update && apt-get install -y \
    bash \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Install kubectl
RUN curl -LO "https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl" \
    && chmod +x kubectl \
    && mv kubectl /usr/local/bin/

WORKDIR /app
COPY --from=publish /app/publish .

# Copy provisioning scripts
COPY --from=build /src/../../../scripts/provision-sftp-tenant.sh /app/scripts/
RUN chmod +x /app/scripts/provision-sftp-tenant.sh

ENTRYPOINT ["dotnet", "TenantService.dll"]
