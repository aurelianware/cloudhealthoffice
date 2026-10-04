using Microsoft.Azure.Cosmos;
using CloudHealthOffice.Infrastructure.Extensions;
using SponsorService.Repositories;
using MongoDB.Driver;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.FieldProtection;
using SponsorService.Security;
using SponsorService.Services;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers(options => options.Filters.Add<FieldProtectionExceptionFilter>())
    .AddCloudHealthOfficeJsonOptions()
    // Responses never carry the full billing account number (last 4 only).
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Insert(0, new MaskedBillingInfoJsonConverter()));

// ── Encryption at rest ─────────────────────────────────────────────
// Bank routing/account numbers and the billing account number are stored
// encrypted (ASP.NET Data Protection; key ring in Azure Blob Storage wrapped
// by a Key Vault key, shared by every pod; a local key ring in Development).
// Without FieldProtection:KeyRing outside Development, writes of those fields
// fail (503) instead of storing plaintext. See docs/security/bank-account-data.md.
var keyRing = builder.Services.AddChoFieldProtection(builder.Configuration, builder.Environment, "sponsor-service");
Console.WriteLine($"Field protection key ring: {keyRing}");

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it. Sponsors (employer groups) are enrollment data: reads need
// enrollment:read, writes need enrollment:process. The compact member view
// also admits members:read (see SponsorsController.GetMemberView); billing
// reads admit billing:read, and the status-only PUT {group}/status admits
// finance:write (see SponsorsController.ChangeSponsorStatus).
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "enrollment:read";
    auth.DefaultWritePermission = "enrollment:process";
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new() 
    { 
        Title = "Cloud Health Office - Sponsor Service API", 
        Version = "v1",
        Description = "Manages employer/group sponsor data populated by X12 834 Enrollment transactions"
    });
});

// Database Configuration (Cosmos DB or MongoDB)
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    // Use MongoDB
    builder.Services.AddScoped<SponsorRepositoryMongo>();
    builder.Services.AddScoped<ISponsorRepository>(sp => new ProtectedSponsorRepository(
        sp.GetRequiredService<SponsorRepositoryMongo>(),
        sp.GetRequiredService<IFieldProtector>(),
        sp.GetRequiredService<ILogger<ProtectedSponsorRepository>>()));
    builder.Services.AddScoped<ISponsorBankAccountRepository, MongoSponsorBankAccountRepository>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    // Use Cosmos DB (Default)
    builder.Services.AddSingleton<CosmosClient>(sp =>
    {
        var configuration = sp.GetRequiredService<IConfiguration>();
        var endpoint = configuration["CosmosDb:Endpoint"] 
            ?? throw new InvalidOperationException("CosmosDb:Endpoint configuration missing");
        var key = configuration["CosmosDb:Key"] 
            ?? throw new InvalidOperationException("CosmosDb:Key configuration missing");
        
        return new CosmosClient(endpoint, key, new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        });
    });

    builder.Services.AddScoped<ISponsorRepository>(sp =>
    {
        var cosmosClient = sp.GetRequiredService<CosmosClient>();
        var configuration = sp.GetRequiredService<IConfiguration>();
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "CloudHealthOffice";
        return new ProtectedSponsorRepository(
            new SponsorRepository(cosmosClient, databaseName),
            sp.GetRequiredService<IFieldProtector>(),
            sp.GetRequiredService<ILogger<ProtectedSponsorRepository>>());
    });
    builder.Services.AddScoped<ISponsorBankAccountRepository, CosmosSponsorBankAccountRepository>();
    Console.WriteLine("Using Cosmos DB repository");
}

// Sponsor bank accounts under dual control (see SponsorBankAccountsController).
builder.Services.AddScoped<ISponsorBankAccountService, SponsorBankAccountService>();

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

// Health checks (MongoDB or Cosmos DB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
    options.CosmosDbConnectionString = builder.Configuration["CosmosDb:ConnectionString"];
    options.CosmosDbEndpoint = builder.Configuration["CosmosDb:Endpoint"];
    options.CosmosDbKey = builder.Configuration["CosmosDb:Key"];
});

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware pipeline
// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
