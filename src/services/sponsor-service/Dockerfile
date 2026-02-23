# Multi-stage build for Sponsor Service
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["sponsor-service.csproj", "./"]
RUN dotnet restore "sponsor-service.csproj"

# Copy source code and build
COPY . .
RUN dotnet build "sponsor-service.csproj" -c Release -o /app/build

# Publish
FROM build AS publish
RUN dotnet publish "sponsor-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Copy published app
COPY --from=publish /app/publish .

# Run as non-root user for security
USER $APP_UID

ENTRYPOINT ["dotnet", "sponsor-service.dll"]
