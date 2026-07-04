ARG REGISTRY=mcr.microsoft.com

FROM ${REGISTRY}/dotnet/sdk:8.0 AS build
WORKDIR /repo

COPY src/Directory.Build.props src/
COPY src/tools/mcc-platform-validator/mcc-platform-validator.csproj src/tools/mcc-platform-validator/
COPY src/CloudHealthOffice.BenchmarkClaimGenerator/CloudHealthOffice.BenchmarkClaimGenerator.csproj src/CloudHealthOffice.BenchmarkClaimGenerator/

RUN dotnet restore src/tools/mcc-platform-validator/mcc-platform-validator.csproj

COPY src/tools/mcc-platform-validator/ src/tools/mcc-platform-validator/
COPY src/CloudHealthOffice.BenchmarkClaimGenerator/ src/CloudHealthOffice.BenchmarkClaimGenerator/

RUN dotnet publish src/tools/mcc-platform-validator/mcc-platform-validator.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM ${REGISTRY}/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "mcc-platform-validator.dll"]
