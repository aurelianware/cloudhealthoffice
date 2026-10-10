using Microsoft.OpenApi.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CloudHealthOffice.Infrastructure.Extensions;
using MongoDB.Driver;
using ArService.Repositories;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);
// Secret provider (Azure Key Vault / none)
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "AR Service API",
        Version = "v1",
        Description = "Accounts Receivable module — GL accounts, balances, cash posting, " +
                     "adjustments, and batch posting rules. Financial backbone that Premium Billing " +
                     "posts into and that FFS/Capitation payment engines draw GL entries from."
    });
});

builder.Services.AddHttpContextAccessor();

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "finance:read";
    auth.DefaultWritePermission = "finance:write";
});

// Database Configuration — MongoDB
var databaseProvider = builder.Services.AddChoDatabase(builder.Configuration);

if (databaseProvider == ChoDatabaseProvider.MongoDb)
{
    builder.Services.AddScoped<IGlAccountRepository, MongoGlAccountRepository>();
    builder.Services.AddScoped<IArBalanceRepository, MongoArBalanceRepository>();
    builder.Services.AddScoped<ICashPostingRepository, MongoCashPostingRepository>();
    builder.Services.AddScoped<IArAdjustmentRepository, MongoArAdjustmentRepository>();
    builder.Services.AddScoped<IArBatchRuleRepository, MongoArBatchRuleRepository>();
    // Tells the legacy reconciliation tool this database is served by a build that
    // credits on apply and refuses unreconciled legacy postings (see ArServiceCapabilities).
    builder.Services.AddHostedService<ArService.Ledger.ArServiceCapabilitiesWriter>();

    // General ledger: double-entry journal posted from GL source events (payment-service),
    // per-tenant account mapping validated at startup, parking instead of dropping, ERP
    // extract and claims payable reconciliation. Off unless GlPosting:Enabled.
    // See docs/operations/GL-POSTING-RUNBOOK.md.
    builder.Services.AddOptions<ArService.Gl.GlPostingOptions>()
        .Bind(builder.Configuration.GetSection(ArService.Gl.GlPostingOptions.SectionName))
        .ValidateOnStart();
    builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<ArService.Gl.GlPostingOptions>, ArService.Gl.GlPostingOptionsValidator>();
    builder.Services.AddScoped<ArService.Gl.IGlJournalRepository>(sp => new ArService.Gl.MongoGlJournalRepository(sp.GetRequiredService<IMongoDatabase>()));
    builder.Services.AddScoped<ArService.Gl.IGlSourceEventRepository>(sp => new ArService.Gl.MongoGlSourceEventRepository(sp.GetRequiredService<IMongoDatabase>()));
    builder.Services.AddScoped<ArService.Gl.IGlPeriodRepository>(sp => new ArService.Gl.MongoGlPeriodRepository(sp.GetRequiredService<IMongoDatabase>()));
    builder.Services.AddScoped<ArService.Gl.IGlChartLookup>(sp => new ArService.Gl.MongoGlChartLookup(sp.GetRequiredService<IMongoDatabase>()));
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddScoped<ArService.Gl.GlPostingService>();
    Console.WriteLine("Using MongoDB repository");
}
else
{
    throw new InvalidOperationException("MongoDb:ConnectionString must be configured for ar-service");
}

builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
});

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "AR Service API v1");
        c.RoutePrefix = string.Empty;
    });
}

app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.UseHttpsRedirection();
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
