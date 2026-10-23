using Microsoft.OpenApi.Models;
using CloudHealthOffice.Infrastructure.Extensions;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Driver;
using TenantService.Services;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using TenantService.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

// Every {tenantId} route acts on the caller's own tenant only (platform:tenants
// excepted, and audited): see Security/TenantAccess.cs.
builder.Services.AddControllers(options => options.Filters.Add<RouteTenantFilter>())
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Tenant Management Service",
        Version = "v1",
        Description = "Multi-tenant SaaS tenant management for Cloud Health Office"
    });
});

// MongoDB — camelCase convention to match stored field names
var camelCasePack = new ConventionPack { new CamelCaseElementNameConvention() };
ConventionRegistry.Register("CamelCase", camelCasePack, _ => true);

// This service historically also accepted a Cosmos DB for MongoDB connection string under
// CosmosDb:ConnectionString. Promote it to the shared key so that keeps working.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"])
    && !string.IsNullOrEmpty(builder.Configuration["CosmosDb:ConnectionString"]))
{
    builder.Configuration["MongoDb:ConnectionString"] = builder.Configuration["CosmosDb:ConnectionString"];
}

builder.Services.AddChoDatabase(builder.Configuration);

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// that token. No defaults: every action names its permission (or
// [AllowAnonymous] for the signed Stripe webhook), so a new endpoint is closed
// until someone decides who may call it.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = null;
    auth.DefaultWritePermission = null;
});
builder.Services.AddAuthorization(authz =>
{
    // Reads open to any authenticated user or service; RouteTenantFilter
    // still confines them to the caller's own tenant.
    authz.AddPolicy(TenantPermissions.MemberPolicy, policy => policy.RequireAuthenticatedUser());
});
builder.Services.AddScoped<TenantAuditLog>();
builder.Services.AddSingleton<StripeWebhookVerifier>();

// Repositories and services
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<ITenantUserRepository, TenantUserRepository>();
builder.Services.AddScoped<ITenantRoleRepository, TenantRoleRepository>();
builder.Services.AddScoped<ITenantService, TenantManagementService>();
builder.Services.AddScoped<ITenantUserService, TenantUserManagementService>();
builder.Services.AddScoped<IStripeService, StripeService>();
builder.Services.AddScoped<ISftpProvisioningService, SftpProvisioningService>();
// Identity lookups for token-service (Controllers/InternalIdentityController.cs).
builder.Services.AddScoped<IIdentityDirectory, IdentityDirectory>();
// Subscription records (portal PlatformTenants and self-service signup) are written here only.
builder.Services.AddScoped<ISubscriptionStore, MongoSubscriptionStore>();
// Invitations (Controllers/InvitationsController.cs; redemption via the internal identity controller).
var invitationOptions = builder.Configuration.GetSection(InvitationOptions.SectionName).Get<InvitationOptions>()
                        ?? new InvitationOptions();
invitationOptions.Validate();
builder.Services.AddSingleton(invitationOptions);
builder.Services.AddScoped<IInvitationStore, MongoInvitationStore>();
builder.Services.AddScoped<InvitationService>();

// Plan configuration (docs/architecture/plan-configuration.md): the setting
// catalog, values by level, resolution and two-person approval. Changes are
// published on configuration.changed.v1 when Kafka is configured.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CloudHealthOffice.Events.Kafka.IChoEventProducer>(_ =>
    new CloudHealthOffice.Events.Kafka.ChoKafkaProducer(new CloudHealthOffice.Events.Kafka.ChoKafkaProducerSettings
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"],
        ClientId = builder.Configuration["Kafka:ClientId"] ?? "tenant-service",
        SaslUsername = builder.Configuration["Kafka:SaslUsername"],
        SaslPassword = builder.Configuration["Kafka:SaslPassword"],
    }));
builder.Services.AddSingleton<TenantService.Configuration.IConfigurationChangePublisher, TenantService.Configuration.KafkaConfigurationChangePublisher>();
builder.Services.AddScoped<TenantService.Configuration.IConfigurationStore, TenantService.Configuration.MongoConfigurationStore>();
builder.Services.AddSingleton<CloudHealthOffice.PlanConfiguration.ISettingCatalog, CloudHealthOffice.PlanConfiguration.BuiltInSettingCatalog>();
// sponsor-service, for a sponsor's line of business when a sponsor-level write
// is checked against that line's floors (best-effort; see FloorChecks).
// AddChoAuthentication puts ChoOutboundTokenHandler on factory clients.
builder.Services.AddHttpClient(TenantService.Configuration.HttpSponsorLineOfBusinessLookup.HttpClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:SponsorService"] ?? "http://sponsor-service").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddScoped<TenantService.Configuration.ISponsorLineOfBusinessLookup, TenantService.Configuration.HttpSponsorLineOfBusinessLookup>();
builder.Services.AddScoped<TenantService.Configuration.ConfigurationService>();
// Narrowing an override list to one branch reads programs from the hierarchy
// records, plans from benefit-plan-service (plan-configuration.md §12.6) and a
// sponsor's policies and classes from sponsor-service (§12.9); all are
// registered below.
builder.Services.AddScoped<TenantService.Configuration.IHierarchyDescendants, TenantService.Configuration.HierarchyDescendants>();
builder.Services.AddHostedService<TenantService.Configuration.ConfigurationIndexInitializer>();

// Carrier and program records (plan-configuration.md §12 step 2). benefit-plan-service
// is called only to follow a plan's program for GET api/v1/programs/by-benefit-plan.
builder.Services.AddScoped<TenantService.Hierarchy.IHierarchyStore, TenantService.Hierarchy.MongoHierarchyStore>();
builder.Services.AddHttpClient(TenantService.Hierarchy.HttpBenefitPlanProgramLookup.HttpClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:BenefitPlanService"] ?? "http://benefit-plan-service").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddScoped<TenantService.Hierarchy.IBenefitPlanProgramLookup, TenantService.Hierarchy.HttpBenefitPlanProgramLookup>();

// The sponsor contract (plan-configuration.md §9b), read only to follow a
// sponsor down to its policies and their classes when narrowing an override
// list. tenant-service never writes a contract.
builder.Services.AddHttpClient(TenantService.Hierarchy.HttpSponsorPolicyLookup.HttpClientName, client =>
{
    client.BaseAddress = new Uri((builder.Configuration["Services:SponsorService"] ?? "http://sponsor-service").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});
builder.Services.AddScoped<TenantService.Hierarchy.ISponsorPolicyLookup, TenantService.Hierarchy.HttpSponsorPolicyLookup>();
builder.Services.AddScoped<TenantService.Hierarchy.HierarchyService>();
builder.Services.AddHostedService<TenantService.Hierarchy.HierarchyIndexInitializer>();

// Health checks
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"]
        ?? builder.Configuration["CosmosDb:ConnectionString"];
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

// Seed admin TenantUser and standard roles on startup.
// MongoDB collections are auto-created on first write — no provisioning needed.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // Seed bootstrap admin
    try
    {
        var userRepo = scope.ServiceProvider.GetRequiredService<ITenantUserRepository>();
        // Both are required; there is no default tenant.
        var seedTenantId = config["SeedAdmin:TenantId"];
        var seedEmail = config["SeedAdmin:Email"] ?? "";

        if (!string.IsNullOrEmpty(seedEmail) && string.IsNullOrEmpty(seedTenantId))
        {
            logger.LogWarning("SeedAdmin:Email is set without SeedAdmin:TenantId; no admin user seeded.");
        }
        else if (!string.IsNullOrEmpty(seedEmail) && seedTenantId != null)
        {
            var existing = await userRepo.GetByEmailAsync(seedTenantId, seedEmail);
            if (existing == null)
            {
                var adminUser = new TenantService.Models.TenantUser
                {
                    TenantId = seedTenantId,
                    Email = seedEmail,
                    EmailNormalized = seedEmail.ToLowerInvariant(),
                    DisplayName = config["SeedAdmin:DisplayName"] ?? seedEmail.Split('@')[0],
                    FirstName = config["SeedAdmin:FirstName"] ?? seedEmail.Split('@')[0],
                    LastName = config["SeedAdmin:LastName"] ?? "",
                    Roles = new List<string> { "TenantAdmin" },
                    Department = "Administration",
                    Status = "Active",
                    CreatedBy = "tenant-service:seed",
                    UpdatedBy = "tenant-service:seed"
                };
                await userRepo.CreateAsync(adminUser);
                logger.LogInformation("Seeded admin TenantUser {Email} for tenant {TenantId}", seedEmail, seedTenantId);
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed admin TenantUser.");
    }

    // Seed standard RBAC roles
    try
    {
        var roleRepository = scope.ServiceProvider.GetRequiredService<ITenantRoleRepository>();
        await roleRepository.SeedStandardRolesAsync();
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to seed standard roles on startup.");
    }
}

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();

// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();

app.MapControllers();

// Health check endpoints
app.MapChoHealthChecks();

app.Run();
