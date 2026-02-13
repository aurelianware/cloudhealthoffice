FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["trading-partner-service.csproj", "./"]
RUN dotnet restore "trading-partner-service.csproj"
COPY . .
RUN dotnet build "trading-partner-service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "trading-partner-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "trading-partner-service.dll"]
