# Multi-stage build for Member Service
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["member-service.csproj", "./"]
RUN dotnet restore "member-service.csproj"

COPY . .
RUN dotnet build "member-service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "member-service.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

COPY --from=publish /app/publish .

USER $APP_UID

ENTRYPOINT ["dotnet", "member-service.dll"]
