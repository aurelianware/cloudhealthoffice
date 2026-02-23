# Multi-stage build for Coverage Service
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["coverage-service.csproj", "./"]
RUN dotnet restore "coverage-service.csproj"

COPY . .
RUN dotnet build "coverage-service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "coverage-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "coverage-service.dll"]
