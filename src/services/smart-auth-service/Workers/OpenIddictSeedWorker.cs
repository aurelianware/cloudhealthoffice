using OpenIddict.Abstractions;
using SmartAuthService.Controllers;
using SmartAuthService.Models;
using SmartAuthService.Services;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Workers;

/// <summary>
/// Runs on startup and idempotently creates the SMART scopes (every
/// environment) and, on a Development host only, the demo client
/// registrations and demo identity bindings for <c>demo-tenant</c>.
///
/// Outside Development, clients are registered by a tenant administrator
/// through <c>/api/admin/smart/clients</c> and identities are bound through
/// enrolment codes; nothing here grants a tenant to anyone.
/// </summary>
public class OpenIddictSeedWorker : IHostedService
{
    public const string DemoTenant = "demo-tenant";

    private readonly IServiceProvider _sp;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OpenIddictSeedWorker> _logger;

    public OpenIddictSeedWorker(IServiceProvider sp, IHostEnvironment environment, ILogger<OpenIddictSeedWorker> logger)
    {
        _sp = sp;
        _environment = environment;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Retry with backoff — MongoDB may not be DNS-resolvable immediately in Docker
        const int maxRetries = 5;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var scope = _sp.CreateAsyncScope();
                await SeedScopesAsync(scope, ct);
                if (_environment.IsDevelopment())
                {
                    await SeedDevelopmentClientsAsync(scope, ct);
                    await SeedDevelopmentBindingsAsync(scope, ct);
                }
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromSeconds(attempt * 5);
                _logger.LogWarning(ex, "OpenIddict seed attempt {Attempt}/{Max} failed — retrying in {Delay}s",
                    attempt, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // ── Scopes ────────────────────────────────────────────────────────────────

    private async Task SeedScopesAsync(AsyncServiceScope scope, CancellationToken ct)
    {
        var mgr = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();

        foreach (var (name, display) in SmartScopes.Catalog)
        {
            if (await mgr.FindByNameAsync(name, ct) is null)
            {
                await mgr.CreateAsync(new OpenIddictScopeDescriptor
                {
                    Name = name,
                    DisplayName = display,
                    Resources = { "fhir-api" }   // token audience
                }, ct);

                _logger.LogInformation("Seeded SMART scope: {Scope}", name);
            }
        }
    }

    // ── Development: demo client registrations ────────────────────────────────

    private async Task SeedDevelopmentClientsAsync(AsyncServiceScope scope, CancellationToken ct)
    {
        var mgr = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        // ── Public SMART patient app (standalone launch) ──────────────────────
        if (await mgr.FindByClientIdAsync("smart-patient-app", ct) is null)
        {
            await mgr.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "smart-patient-app",
                ClientType = ClientTypes.Public,
                DisplayName = "CHO SMART Patient App",
                RedirectUris =
                {
                    new Uri("https://app.cloudhealthoffice.com/callback"),
                    new Uri("http://localhost:4200/callback")  // dev
                },
                PostLogoutRedirectUris =
                {
                    new Uri("https://app.cloudhealthoffice.com/signout"),
                    new Uri("http://localhost:4200/signout")
                },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Prefixes.Scope + Scopes.OpenId,
                    Permissions.Prefixes.Scope + SmartScopes.FhirUser,
                    Permissions.Prefixes.Scope + SmartScopes.LaunchPatient,
                    Permissions.Prefixes.Scope + SmartScopes.PatientWildcardRead,
                    Permissions.Prefixes.Scope + SmartScopes.PatientEobRead,
                    Permissions.Prefixes.Scope + SmartScopes.PatientCoverageRead,
                    Permissions.Prefixes.Scope + SmartScopes.PatientPatientRead,
                    Permissions.Prefixes.Scope + SmartScopes.PatientEncounterRead,
                    Permissions.Prefixes.Scope + SmartScopes.PatientClaimRead,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            }, ct);

            _logger.LogInformation("Seeded client: smart-patient-app");
        }

        // ── Confidential EHR app (EHR launch, provider access) ───────────────
        if (await mgr.FindByClientIdAsync("cho-ehr-app", ct) is null)
        {
            await mgr.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "cho-ehr-app",
                ClientSecret = "ehr-app-secret-change-in-prod",
                ClientType = ClientTypes.Confidential,
                DisplayName = "CHO EHR Application",
                RedirectUris =
                {
                    new Uri("https://portal.cloudhealthoffice.com/smart/callback"),
                    new Uri("http://localhost:5000/smart/callback")
                },
                Permissions =
                {
                    Permissions.Endpoints.Authorization,
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.AuthorizationCode,
                    Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code,
                    Permissions.Prefixes.Scope + Scopes.OpenId,
                    Permissions.Prefixes.Scope + SmartScopes.Launch,
                    Permissions.Prefixes.Scope + SmartScopes.LaunchPatient,
                    Permissions.Prefixes.Scope + SmartScopes.LaunchEncounter,
                    Permissions.Prefixes.Scope + SmartScopes.UserWildcardRead,
                    Permissions.Prefixes.Scope + SmartScopes.FhirUser,
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange }
            }, ct);

            _logger.LogInformation("Seeded client: cho-ehr-app");
        }

        // ── Backend system client (payer-to-payer / bulk data) ────────────────
        if (await mgr.FindByClientIdAsync("cho-payer-system", ct) is null)
        {
            await mgr.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "cho-payer-system",
                ClientSecret = "system-secret-change-in-prod",
                ClientType = ClientTypes.Confidential,
                DisplayName = "CHO Payer System (Backend)",
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + SmartScopes.SystemWildcardRead,
                    Permissions.Prefixes.Scope + SmartScopes.SystemEobRead,
                    Permissions.Prefixes.Scope + SmartScopes.SystemCoverageRead,
                    Permissions.Prefixes.Scope + SmartScopes.SystemPatientRead,
                },
            }, ct);

            _logger.LogInformation("Seeded client: cho-payer-system");
        }
    }

    // ── Development: demo identity bindings (demo-tenant) ─────────────────────

    /// <summary>
    /// Development login users <c>demo-member</c> (member pat-001) and
    /// <c>demo-provider</c> (provider-001), and the three demo clients, all in
    /// demo-tenant. Any other development username has no binding and gets no
    /// token until it redeems an enrolment code.
    /// </summary>
    private static async Task SeedDevelopmentBindingsAsync(AsyncServiceScope scope, CancellationToken ct)
    {
        var store = scope.ServiceProvider.GetRequiredService<ISmartIdentityStore>();
        var now = DateTimeOffset.UtcNow;
        const string seededBy = "development-seed";

        await store.SeedDevelopmentBindingsAsync(
            members:
            [
                new MemberLink
                {
                    Id = "dev-member-link", TenantId = DemoTenant,
                    Issuer = DevelopmentLogin.Issuer, Subject = "demo-member",
                    MemberId = "pat-001", CreatedBy = seededBy, CreatedAt = now,
                },
            ],
            providers:
            [
                new ProviderUserLink
                {
                    Id = "dev-provider-link", TenantId = DemoTenant,
                    Issuer = DevelopmentLogin.Issuer, Subject = "demo-provider",
                    ProviderId = "provider-001", Npi = "1234567893", CreatedBy = seededBy, CreatedAt = now,
                },
            ],
            clients:
            [
                new ClientTenantRegistration { ClientId = "smart-patient-app", TenantId = DemoTenant, Kind = SmartClientKind.PatientApp, DisplayName = "CHO SMART Patient App", CreatedBy = seededBy, CreatedAt = now },
                new ClientTenantRegistration { ClientId = "cho-ehr-app", TenantId = DemoTenant, Kind = SmartClientKind.ProviderApp, DisplayName = "CHO EHR Application", CreatedBy = seededBy, CreatedAt = now },
                new ClientTenantRegistration { ClientId = "cho-payer-system", TenantId = DemoTenant, Kind = SmartClientKind.Backend, DisplayName = "CHO Payer System (Backend)", CreatedBy = seededBy, CreatedAt = now },
            ],
            ct);
    }
}
