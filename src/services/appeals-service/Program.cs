using CloudHealthOffice.Infrastructure.Contacts;
using AppealsService.HostedServices;
using CloudHealthOffice.Infrastructure.Extensions;
using AppealsService.Middleware;
using AppealsService.Repositories;
using AppealsService.Services;
using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Messaging;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Conventions;

// ── BSON convention: enums stored as strings ─────────────────────────
// Register BEFORE any IMongoClient is constructed. Ensures enum values on
// Appeal (Status, ClosureReasonCode, AppealType, etc.) are persisted as
// human-readable strings rather than integers. Also future-proofs against
// enum-value reordering: adding a new status value doesn't silently
// re-map existing data.
MongoDB.Bson.Serialization.Conventions.ConventionRegistry.Register(
    "appeals-enums-as-strings",
    new ConventionPack { new EnumRepresentationConvention(BsonType.String) },
    _ => true);

var builder = WebApplication.CreateBuilder(args);

// Secret provider (Azure Key Vault / none) — same bootstrap shape as
// consent-service / personal-rep-service.
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers()
    .AddCloudHealthOfficeJsonOptions();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Cloud Health Office - Appeals Service API",
        Version = "v1",
        Description = "Claim appeals processing with 275 attachment support. "
                    + "State-machine lifecycle, append-only audit trail, field-level encryption."
    });
});

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come
// from that token.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "appeals:read";
    auth.DefaultWritePermission = "appeals:write";
});

// ── Appeal events: transactional outbox + Kafka relay ─────────────────
// Every appeal change writes its Kafka event into the appeal document's
// outbox in the same write (see AppealOutbox / docs/architecture/
// appeals-event-outbox.md). AppealOutboxDispatcher publishes pending
// entries through the Kafka transport with retry, backoff and dead-letter.
// Kafka:BootstrapServers unset = publishing disabled by configuration:
// events stay pending (or are marked skipped with
// AppealOutbox:SkipWhenKafkaDisabled=true). No startup ordering matters:
// nothing publishes before the transport has started.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(AppealOutboxOptions.SectionName).Get<AppealOutboxOptions>()
    ?? new AppealOutboxOptions());
builder.Services.AddSingleton<AppealEventPublisher>();
builder.Services.AddSingleton<IAppealEventTransport>(sp => sp.GetRequiredService<AppealEventPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppealEventPublisher>());
builder.Services.AddSingleton<AppealOutboxDispatcher>();
builder.Services.AddSingleton<IAppealOutboxDispatcher>(sp => sp.GetRequiredService<AppealOutboxDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AppealOutboxDispatcher>());

// ── Database Configuration ───────────────────────────────────────────
// MongoDB is the only appeal store (ADR 006); AddChoDatabase registers the client.
if (builder.Services.AddChoDatabase(builder.Configuration) != ChoDatabaseProvider.MongoDb)
{
    throw new InvalidOperationException(
        "Appeals Service requires a database. Configure MongoDb:ConnectionString.");
}

builder.Services.AddSingleton<IAppealEventRepository, AppealEventRepositoryMongo>();
builder.Services.AddSingleton<IAppealEventSink>(sp => (IAppealEventSink)sp.GetRequiredService<IAppealEventRepository>());
builder.Services.AddSingleton<AppealRepositoryMongo>();
builder.Services.AddSingleton<IAppealRepository>(sp => sp.GetRequiredService<AppealRepositoryMongo>());
builder.Services.AddSingleton<IAppealOutboxStore>(sp => sp.GetRequiredService<AppealRepositoryMongo>());
// Texas HHSC reporting codes per case (UMCM 5.24.5 / 5.24.6 text files).
builder.Services.AddSingleton<ITexasAgCodesStore, TexasAgCodesStoreMongo>();

// Registration order matters for IHostedService.StartAsync sequencing:
// migration runs first so the index initializer sees a consistent
// schema and duplicate-AppealNumber warnings fire before the unique
// index build.
builder.Services.AddHostedService<AppealStatusMigrationHostedService>();
builder.Services.AddHostedService<AppealIndexInitializer>();

// ── Appeal body encryption ───────────────────────────────────────────
// Non-dev startup guard: no AppealEncryption section = startup error.
// Only IsDevelopment() falls back to the no-op encryptor.
var appealEncryptionSection = builder.Configuration.GetSection(AppealEncryptionOptions.SectionName);
if (appealEncryptionSection.Exists())
{
    var options = appealEncryptionSection.Get<AppealEncryptionOptions>() ?? new AppealEncryptionOptions();
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<IAppealFieldEncryptor>(sp =>
        new AppealFieldEncryptor(
            sp.GetRequiredService<RotatingKeyProvider>(),
            sp.GetRequiredService<ILogger<AppealFieldEncryptor>>(),
            options));

    Console.WriteLine(
        $"[appeals-service] IAppealFieldEncryptor = KeyVault (rotating) — current: {options.CurrentKeyVersion}; " +
        $"accepted: [{string.Join(", ", options.AcceptedKeyVersions)}]");
}
else
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "AppealEncryption must be configured in non-development environments. " +
            "Publish the appeal-body-encryption-key-v1 secret and set AppealEncryption:KeySecretPrefix / CurrentKeyVersion.");
    }
    builder.Services.AddSingleton(new AppealEncryptionOptions());
    builder.Services.AddSingleton<IAppealFieldEncryptor, NoOpAppealFieldEncryptor>();
    Console.WriteLine("[dev] IAppealFieldEncryptor = NoOp (appeal body fields stored plaintext). Configure AppealEncryption to enable.");
}

// ── Holiday calendar for working-day clocks ──────────────────────────
// U.S. federal holidays by default; AppealHolidays adds per-tenant and
// per-state holidays. Resolved right after Build() so a malformed entry
// fails startup rather than the first deadline computation.
builder.Services.AddSingleton<IAppealHolidayCalendarProvider>(sp =>
    new ConfiguredAppealHolidayCalendarProvider(
        sp.GetRequiredService<IConfiguration>().GetSection(AppealHolidayOptions.SectionName).Get<AppealHolidayOptions>()
        ?? new AppealHolidayOptions()));

// ── State rule packs ─────────────────────────────────────────────────
// Embedded, versioned State appeal and grievance rules, validated against
// the federal floor. Resolved right after Build() so a bad pack fails
// startup. Read-only today: GET api/appeals/rule-packs.
builder.Services.AddSingleton<AppealsService.Services.RulePacks.IRulePackCatalog>(_ =>
    AppealsService.Services.RulePacks.RulePackCatalog.FromEmbeddedResources());

// ── Kafka consumer (X12 275 attachment ingress) ──────────────────────
// Subscribes to the attachments-in topic, routes appeal-context 275s to
// the existing AppendAttachmentAsync path. Degraded-mode-silent if
// Kafka:BootstrapServers is unset. Its AppealAttachmentAdded event goes
// through the outbox like every other change.
builder.Services.AddSingleton<Attachment275EnvelopeMapper>();
builder.Services.AddSingleton<IAttachment275DeadLetterSink, LoggingAttachment275DeadLetterSink>();
builder.Services.AddHostedService<Attachment275ConsumerHostedService>();

// IMessageBus — registered for future consumers; no-op cost today.
builder.Services.AddChoMessaging(builder.Configuration, builder.Environment);

// No CORS: this service is called server-to-server only (the portal is
// Blazor Server), so browsers on other origins get no CORS grant.

// Health checks: Mongo via the shared bootstrap, plus the
// local appeal-encryption-key readiness check.
builder.Services.AddChoHealthChecks(options =>
{
    options.MongoDbConnectionString = builder.Configuration["MongoDb:ConnectionString"];
})
.AddCheck<AppealEncryptionKeyHealthCheck>(
    "appeal-encryption-key",
    failureStatus: HealthStatus.Unhealthy,
    tags: new[] { "ready" });

builder.Services.AddChoObservability(builder.Configuration);

// Who read which appeal, its notes and its documents (HIPAA access
// accounting): GET api/appeals/{id}/access-log.
CloudHealthOffice.Infrastructure.Audit.RecordAccessAuditExtensions.AddRecordAccessAudit(builder.Services);

builder.Services.AddContactIntakeReader(builder.Configuration);
builder.Services.AddSingleton<IMemberServiceCaseStore, MemberServiceCaseStore>();
builder.Services.AddHttpClient("ContactAppealAuthorization", client => {
    client.BaseAddress = new Uri((builder.Configuration["Services:AuthorizationServiceUrl"]
        ?? "http://authorization-service.cloudhealthoffice/").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IMemberCaseSubjectValidator, HttpMemberCaseSubjectValidator>();
// CMS-0057-F: appeal outcomes go onto the authorization's reporting ledger.
builder.Services.AddSingleton<IAppealDecisionReporter, HttpAppealDecisionReporter>();
builder.Services.AddSingleton<AppealDecisionReporting>();
builder.Services.AddHostedService<AppealsService.HostedServices.AppealDecisionReportingSweep>();

var app = builder.Build();

_ = app.Services.GetRequiredService<IAppealHolidayCalendarProvider>();
_ = app.Services.GetRequiredService<AppealsService.Services.RulePacks.IRulePackCatalog>();

app.UseChoObservability();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Appeals Service API v1");
    });
}

app.UseChoAuthentication();
app.MapControllers();
CloudHealthOffice.Infrastructure.Audit.RecordAccessAuditExtensions.MapRecordAccessLog(app, "api/appeals", "Appeal");
app.MapChoHealthChecks();

app.Run();

// Required so WebApplicationFactory<Program> works in the test project.
public partial class Program { }
