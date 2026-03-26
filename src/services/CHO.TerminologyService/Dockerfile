FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY CHO.TerminologyService.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Create directory for map file mounts
RUN mkdir -p /data/maps

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:5080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget -qO- http://localhost:5080/health || exit 1

ENTRYPOINT ["dotnet", "CHO.TerminologyService.dll"]
