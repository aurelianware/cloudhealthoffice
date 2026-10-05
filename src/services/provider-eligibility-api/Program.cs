using CloudHealthOffice.Infrastructure.Configuration;
using CloudHealthOffice.Infrastructure.Gateways;
using CloudHealthOffice.Infrastructure.HealthChecks;
using CloudHealthOffice.Infrastructure.Json;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProviderEligibilityApi.Eligibility;
using ProviderEligibilityApi.Security;

// Provider eligibility API: the narrow, authenticated surface a provider
// application (CloudDentalOffice) and CHO callers use to run outbound 270/271
// checks through the configured healthcare gateway. It hosts nothing else — no payer-side
// responders, batch jobs, claims or demo routes — so it can run beside CDO
// with internal-only ingress and no database.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSecretProvider(builder.Configuration);
builder.Configuration.AddAzureKeyVaultConfiguration(builder.Configuration);

builder.Services.AddControllers().AddCloudHealthOfficeJsonOptions();

// ── Authentication ──────────────────────────────────────────────────
// CHO callers present a CHO token; the tenant and the actor come from it, and
// both endpoints need eligibility:check. Unannotated GETs default to
// eligibility:check; any other unannotated action is denied.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = ProviderEligibilityAuth.CheckPermission;
    auth.DefaultWritePermission = null;
});

// Provider applications hold no CHO token. They keep their API key, now a
// scheme of its own ("ProviderApiKey"), and each key is bound to one tenant.
builder.Services.AddOptions<ProviderApiOptions>()
    .Bind(builder.Configuration.GetSection(ProviderApiOptions.SectionName));
builder.Services.AddProviderEligibilityCallers();

builder.Services.TryAddSingleton(TimeProvider.System);

// Gateway resolver, Stedi adapter (when configured) and the payer directory.
// Claim lifecycle stores are registered by this call but never used here.
builder.Services.AddChoHealthcareGateways(builder.Configuration);

// Readiness waits for the payer directory; see PayerDirectoryReadiness.
builder.Services.AddSingleton<PayerDirectoryReadiness>();
builder.Services.AddHostedService<PayerDirectoryStartupRetryService>();
builder.Services.AddChoHealthChecks()
    .AddCheck<PayerDirectoryHealthCheck>("payer-directory", tags: ["ready"]);
builder.Services.AddChoObservability(builder.Configuration);

var app = builder.Build();

// Checked against the final configuration, after every source has applied.
ProductionGatewayGuard.EnsureNotMock(app.Configuration, app.Environment);

app.UseChoObservability();
app.UseChoAuthentication();
app.MapControllers();
app.MapChoHealthChecks();

app.Run();

public partial class Program { }
