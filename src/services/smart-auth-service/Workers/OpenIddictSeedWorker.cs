using OpenIddict.Abstractions;
using SmartAuthService.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Workers;

/// <summary>
/// Runs on startup and idempotently creates OpenIddict scopes and demo client
/// registrations.  In production, clients are managed via the admin API or a
/// migration tool — the seed here registers only the built-in SMART scopes and
/// two test applications used in integration tests.
/// </summary>
public class OpenIddictSeedWorker : IHostedService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OpenIddictSeedWorker> _logger;

    public OpenIddictSeedWorker(IServiceProvider sp, ILogger<OpenIddictSeedWorker> logger)
    {
        _sp = sp;
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
                await SeedClientsAsync(scope, ct);
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

        var smartScopes = new[]
        {
            (SmartScopes.FhirUser,          "FHIR user identity"),
            (SmartScopes.Launch,            "EHR launch"),
            (SmartScopes.LaunchPatient,     "Patient context on launch"),
            (SmartScopes.LaunchEncounter,   "Encounter context on launch"),
            (SmartScopes.PatientWildcardRead,    "Read all patient-level resources"),
            (SmartScopes.UserWildcardRead,       "Read all user-level resources"),
            (SmartScopes.SystemWildcardRead,     "Read all system-level resources"),
            (SmartScopes.PatientPatientRead,     "Patient: read Patient"),
            (SmartScopes.PatientCoverageRead,    "Patient: read Coverage"),
            (SmartScopes.PatientEobRead,         "Patient: read ExplanationOfBenefit"),
            (SmartScopes.PatientEncounterRead,   "Patient: read Encounter"),
            (SmartScopes.PatientClaimRead,       "Patient: read Claim"),
            (SmartScopes.UserPatientRead,        "User: read Patient"),
            (SmartScopes.UserCoverageRead,       "User: read Coverage"),
            (SmartScopes.UserEobRead,            "User: read ExplanationOfBenefit"),
            (SmartScopes.UserEncounterRead,      "User: read Encounter"),
            (SmartScopes.UserClaimRead,          "User: read Claim"),
            (SmartScopes.SystemPatientRead,      "System: read Patient"),
            (SmartScopes.SystemCoverageRead,     "System: read Coverage"),
            (SmartScopes.SystemEobRead,          "System: read ExplanationOfBenefit"),
            (SmartScopes.SystemEncounterRead,    "System: read Encounter"),
            (SmartScopes.SystemClaimRead,        "System: read Claim"),
        };

        foreach (var (name, display) in smartScopes)
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

    // ── Client registrations ──────────────────────────────────────────────────

    private async Task SeedClientsAsync(AsyncServiceScope scope, CancellationToken ct)
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
}
