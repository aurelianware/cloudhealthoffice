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

// One-off migration (operator CLI): dotnet sponsor-service.dll --encrypt-bank-accounts
// [--tenant <id>] [--dry-run]. Stores sponsor bank numbers written before encryption,
// or before record binding (enc:v1), as enc:v2, in the MongoDB store.
if (args.Contains(SponsorService.Migrations.EncryptSponsorBankAccounts.Switch))
{
    Environment.ExitCode = await SponsorService.Migrations.EncryptSponsorBankAccounts.RunAsync(
        args, builder.Configuration, builder.Environment);
    return;
}

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

// Database Configuration — MongoDB is the only sponsor store (ADR 006);
// the database client is registered by the shared lib.
if (string.IsNullOrEmpty(builder.Configuration["MongoDb:ConnectionString"]))
{
    throw new InvalidOperationException(
        "Sponsor Service requires a database. Configure MongoDb:ConnectionString.");
}
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Sponsor Service supports only MongoDB. Remove Database:Provider or set it to MongoDb.");
}

builder.Services.AddScoped<SponsorRepositoryMongo>();
builder.Services.AddScoped<ISponsorRepository>(sp => new ProtectedSponsorRepository(
    sp.GetRequiredService<SponsorRepositoryMongo>(),
    sp.GetRequiredService<IFieldProtector>(),
    sp.GetRequiredService<ILogger<ProtectedSponsorRepository>>()));
builder.Services.AddScoped<ISponsorBankAccountRepository, MongoSponsorBankAccountRepository>();

// Sponsor bank accounts under dual control (see SponsorBankAccountsController).
builder.Services.AddScoped<ISponsorBankAccountService, SponsorBankAccountService>();

// Sponsor policies and contract terms (plan-configuration.md §9b, step 3).
builder.Services.AddScoped<SponsorService.Contracts.IContractStore, SponsorService.Contracts.ContractStoreMongo>();
builder.Services.AddScoped<SponsorService.Contracts.ContractService>();
builder.Services.AddSingleton(TimeProvider.System);

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

// Health checks (MongoDB)
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
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
