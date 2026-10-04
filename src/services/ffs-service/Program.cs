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

// ── Authentication ──────────────────────────────────────────────────
// Every caller presents a CHO token; the tenant and the acting user come from
// it (ICurrentActor / HttpContext.GetTenantId()), never from a header, query
// string, body or route. FFS rate configs are payment terms of a provider
// contract (the sibling of capitation-service's rate configs): reads need
// payments:read, writes need payments:run. Any controller added here without
// [RequirePermission] gets these defaults.
builder.Services.AddChoAuthentication(builder.Configuration, builder.Environment, auth =>
{
    auth.DefaultReadPermission = "payments:read";
    auth.DefaultWritePermission = "payments:run";
});

builder.Services.AddChoObservability(builder.Configuration);
var app = builder.Build();
app.UseChoObservability();
// Authentication, then tenant from the validated token, then authorization.
app.UseChoAuthentication();
app.MapControllers();
app.Run();

// Required for WebApplicationFactory<Program> in integration tests
public partial class Program { }
