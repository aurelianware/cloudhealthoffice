using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace FhirService.Services.Identity;

/// <summary>
/// Registers CHO's SMART/OAuth trust model and binds it to the JWT bearer
/// handler.
///
/// Everything here resolves the ISSUER FIRST and derives the rest from that one
/// entry: its keys, its audiences, its algorithms, its claim mapping. The
/// alternative — a global audience list, a global key set, a global algorithm
/// list — quietly makes trust the UNION of every configured issuer's, so a
/// token from issuer A would be accepted bearing issuer B's audience and
/// verified against B's keys. With one trusted IdP that is invisible; with two
/// it is the difference between per-customer trust and one shared trust blob.
/// </summary>
public static class SmartTrustServiceCollectionExtensions
{
    public static IServiceCollection AddSmartTrust(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = new SmartTrustOptions();
        configuration.GetSection(SmartTrustOptions.SectionName).Bind(options);

        // Fail closed at startup, before the first request. A trust
        // misconfiguration discovered on the first 401 of a production morning
        // is one nobody attributes to configuration.
        options.Validate(environment.IsDevelopment());

        services.AddSingleton(options);
        services.AddSingleton(new TrustedIssuerRegistry(options));
        services.AddSingleton<CallerIdentityResolver>();
        services.AddSingleton<SmartSigningKeyRing>();
        services.AddSingleton<IIssuerMetadataFetcher, HttpIssuerMetadataFetcher>();

        // A dedicated client, so metadata retrieval cannot inherit a handler
        // configured for something else and a slow IdP cannot hold a request
        // thread indefinitely.
        services.AddHttpClient(HttpIssuerMetadataFetcher.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        // Configured through DI so the key ring is injected rather than
        // captured — the handler needs it inside a synchronous callback that
        // runs long after registration.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TrustedIssuerRegistry, SmartSigningKeyRing>(
                (jwt, registry, keyRing) => Configure(jwt, registry, keyRing, environment));

        services.AddAuthorization();
        return services;
    }

    private static void Configure(
        JwtBearerOptions jwt,
        TrustedIssuerRegistry registry,
        SmartSigningKeyRing keyRing,
        IHostEnvironment environment)
    {
        // Deliberately no Authority: setting one makes the handler run its own
        // single-issuer discovery and manage its own key cache, which is exactly
        // the per-issuer behaviour SmartSigningKeyRing owns.
        jwt.RequireHttpsMetadata = !environment.IsDevelopment();

        // The one definition of the rules — the same factory the tests assert
        // against, so there is no second copy to drift.
        jwt.TokenValidationParameters = SmartTokenValidation.CreateParameters(registry, keyRing);

        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var registryService = context.HttpContext.RequestServices
                    .GetRequiredService<TrustedIssuerRegistry>();
                var resolver = context.HttpContext.RequestServices
                    .GetRequiredService<CallerIdentityResolver>();

                var caller = resolver.Resolve(context.Principal);
                if (caller == null)
                {
                    // The principal validated, but its issuer is not one this
                    // registry knows — which happens when a host replaces the
                    // validation parameters (the test harness does exactly
                    // that). Authentication is NOT overturned here: the handler
                    // already applied whatever issuer policy the deployment
                    // configured, and second-guessing it would turn a legitimate
                    // reconfiguration into blanket 401s.
                    //
                    // What is withheld is IDENTITY. No AuthenticatedCaller means
                    // no verified provider NPI and no mapped tenant claim, so
                    // every check that would have been strengthened by them
                    // falls back to its prior, weaker-but-honest behaviour
                    // rather than being satisfied by an unverified one.
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("FhirService.Identity")
                        .LogDebug(
                            "Authenticated principal did not resolve to a configured trusted "
                            + "issuer; continuing without a verified caller identity.");
                    return Task.CompletedTask;
                }

                if (!SmartTokenValidation.AlgorithmIsAcceptedForIssuer(
                        registryService, context.SecurityToken, caller.Issuer))
                {
                    context.Fail($"Signing algorithm is not accepted for issuer '{caller.Issuer}'.");
                    return Task.CompletedTask;
                }

                context.HttpContext.Items[AuthenticatedCaller.HttpContextItemKey] = caller;
                return Task.CompletedTask;
            },

            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("FhirService.Identity");

                // Failure category only. Never the token, the header, or the
                // claims — a rejected token is still a live credential.
                logger.LogWarning(
                    "SMART authentication failed: {Category}", context.Exception.GetType().Name);

                return Task.CompletedTask;
            },
        };
    }
}
