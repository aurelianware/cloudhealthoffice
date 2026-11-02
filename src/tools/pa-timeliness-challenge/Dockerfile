ARG REGISTRY=mcr.microsoft.com

FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo
# Central Package Management and the repository-wide warning gate.
COPY Directory.Packages.props ./
COPY Directory.Build.props ./
COPY src/Directory.Build.props src/
COPY src/tools/pa-timeliness-challenge/pa-timeliness-challenge.csproj src/tools/pa-timeliness-challenge/

RUN dotnet restore src/tools/pa-timeliness-challenge/pa-timeliness-challenge.csproj

COPY src/tools/pa-timeliness-challenge/ src/tools/pa-timeliness-challenge/

RUN dotnet publish src/tools/pa-timeliness-challenge/pa-timeliness-challenge.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM ${REGISTRY}/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Synthetic data only. The run writes its summary under /tmp and prints it to the log.
ENTRYPOINT ["dotnet", "pa-timeliness-challenge.dll"]
