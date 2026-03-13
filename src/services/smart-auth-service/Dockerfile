FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

COPY src/services/smart-auth-service/smart-auth-service.csproj src/services/smart-auth-service/
RUN dotnet restore src/services/smart-auth-service/smart-auth-service.csproj

COPY src/services/smart-auth-service/ src/services/smart-auth-service/
RUN dotnet build src/services/smart-auth-service/smart-auth-service.csproj -c Release --no-restore

FROM build AS publish
RUN dotnet publish src/services/smart-auth-service/smart-auth-service.csproj -c Release -o /app/publish --no-build

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080 8081
COPY --from=publish /app/publish .
RUN groupadd -r appuser && useradd -r -g appuser appuser && chown -R appuser:appuser /app
USER appuser
ENTRYPOINT ["dotnet", "smart-auth-service.dll"]
