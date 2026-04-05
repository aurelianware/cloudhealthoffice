using CloudHealthOffice.Infrastructure.Extensions;
using ClaimsScrubbingService.Repositories;
using ClaimsScrubbingService.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Infrastructure ────────────────────────────────────────────────────────────

builder.Services.AddChoInfrastructure(builder.Configuration, options =>
{
    options.ServiceName        = "Claims Scrubbing Service";
    options.ServiceDescription = "Pre-adjudication claim validation and routing";
});

// ── Application services ──────────────────────────────────────────────────────

// Rule engine — singleton so rules are initialized once and shared
builder.Services.AddSingleton<IValidationRuleEngine, ValidationRuleEngine>();

// Repositories — scoped (one MongoDB connection per request)
builder.Services.AddScoped<IClaimAuditRepository, ClaimAuditRepository>();
builder.Services.AddScoped<IScrubRuleRepository, ScrubRuleRepository>();

// Main scrubber service — scoped
builder.Services.AddScoped<IClaimsScrubberService, ClaimsScrubberService>();

// ── Optional Kafka producer ───────────────────────────────────────────────────

var kafkaBootstrap = builder.Configuration["Kafka:BootstrapServers"];
if (!string.IsNullOrEmpty(kafkaBootstrap))
{
    // Register as both singleton service and hosted service for lifecycle management
    builder.Services.AddSingleton<KafkaProducerService>();
    builder.Services.AddSingleton<IKafkaProducerService>(sp => sp.GetRequiredService<KafkaProducerService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<KafkaProducerService>());
}

// ── Optional Blob archive service ─────────────────────────────────────────────

var storageConnection = builder.Configuration["Storage:ConnectionString"];
var storageAccount    = builder.Configuration["Storage:AccountName"];
if (!string.IsNullOrEmpty(storageConnection) || !string.IsNullOrEmpty(storageAccount))
{
    builder.Services.AddSingleton<IBlobArchiveService, BlobArchiveService>();
}

// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

app.UseChoInfrastructure(builder.Configuration);
app.UseMiddleware<CloudHealthOffice.Infrastructure.Middleware.ExceptionHandlingMiddleware>();
app.MapControllers();

// ── Load custom rules from MongoDB on startup ─────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    try
    {
        var ruleEngine      = scope.ServiceProvider.GetRequiredService<IValidationRuleEngine>();
        var scrubRuleRepo   = scope.ServiceProvider.GetRequiredService<IScrubRuleRepository>();
        var customRules     = await scrubRuleRepo.LoadCustomRulesAsync();

        foreach (var rule in customRules)
            ruleEngine.AddCustomRule(rule);

        app.Logger.LogInformation("Claims Scrubbing Service initialized with {Count} custom rule(s)", customRules.Count);
    }
    catch (Exception ex)
    {
        // Non-fatal — service starts without custom rules if MongoDB unreachable
        app.Logger.LogWarning(ex, "Could not load custom rules from MongoDB — starting with standard rules only");
    }
}

app.Run();
