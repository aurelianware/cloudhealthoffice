using Azure.Identity;
using migration_wizard.Components;
using MigrationWizard.Models;
using MigrationWizard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add Azure Key Vault configuration if configured
var keyVaultUri = builder.Configuration["KeyVault:VaultUri"];
if (!string.IsNullOrEmpty(keyVaultUri) && keyVaultUri != "https://your-keyvault.vault.azure.net/")
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential());
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure migration services
ConfigureMigrationServices(builder.Services, builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static void ConfigureMigrationServices(IServiceCollection services, IConfiguration configuration)
{
    // backend system Open Access APIs configuration
    var trizettoConfig = new TriZettoOpenAccessConfig
    {
        EndpointUrl = configuration["BackendSystem:EndpointUrl"] ?? "https://backend-server.example.com/OpenAccess/Services",
        Username = configuration["BackendSystem:Username"] ?? "your-username",
        Password = configuration["BackendSystem:Password"] ?? "your-password",
        TenantId = configuration["BackendSystem:TenantId"] ?? "default-tenant",
        TimeoutSeconds = int.TryParse(configuration["BackendSystem:TimeoutSeconds"], out var timeout) ? timeout : 120,
        BypassCertificateValidation = bool.TryParse(configuration["BackendSystem:BypassCertificateValidation"], out var bypass) && bypass
    };
    services.AddSingleton(trizettoConfig);
    services.AddSingleton<TriZettoOpenAccessClient>();

    // Cosmos DB configuration
    var cosmosConfig = new CosmosDbConfig
    {
        Endpoint = configuration["CosmosDb:Endpoint"] ?? string.Empty,
        Key = configuration["CosmosDb:Key"] ?? string.Empty,
        DatabaseName = configuration["CosmosDb:DatabaseName"] ?? "cloudhealthoffice",
        MembersContainer = configuration["CosmosDb:MembersContainer"] ?? "Members",
        ProvidersContainer = configuration["CosmosDb:ProvidersContainer"] ?? "ProviderDirectory",
        BenefitPlansContainer = configuration["CosmosDb:BenefitPlansContainer"] ?? "BenefitPlans",
        DefaultThroughput = int.TryParse(configuration["CosmosDb:DefaultThroughput"], out var throughput) ? throughput : 400
    };
    services.AddSingleton(cosmosConfig);
    services.AddSingleton<CosmosDbExportService>();

    // API Management configuration
    var apimConfig = new ApiManagementConfig
    {
        ServiceName = configuration["ApiManagement:ServiceName"] ?? string.Empty,
        ResourceGroup = configuration["ApiManagement:ResourceGroup"] ?? string.Empty,
        SubscriptionId = configuration["ApiManagement:SubscriptionId"] ?? string.Empty,
        RoutingKeyName = configuration["ApiManagement:RoutingKeyName"] ?? "backend-routing",
        LegacyBackendId = configuration["ApiManagement:LegacyBackendId"] ?? "legacy-backend",
        CloudHealthOfficeBackendId = configuration["ApiManagement:CloudHealthOfficeBackendId"] ?? "cloudhealthoffice-backend"
    };
    services.AddSingleton(apimConfig);
    services.AddSingleton<ApiManagementCutoverService>();

    // Mapping report generator
    services.AddSingleton<MappingReportGenerator>();

    // Migration orchestrator - scoped to allow proper disposal
    services.AddScoped<MigrationOrchestrator>();
}
