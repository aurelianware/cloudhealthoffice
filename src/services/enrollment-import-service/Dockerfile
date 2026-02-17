FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["EnrollmentImportService.csproj", "./"]
RUN dotnet restore "EnrollmentImportService.csproj"
COPY . .
RUN dotnet build "EnrollmentImportService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EnrollmentImportService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EnrollmentImportService.dll"]
