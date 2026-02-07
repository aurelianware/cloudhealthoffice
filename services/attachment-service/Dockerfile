FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["attachment-service.csproj", "./"]
RUN dotnet restore "attachment-service.csproj"
COPY . .
RUN dotnet build "attachment-service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "attachment-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "attachment-service.dll"]
