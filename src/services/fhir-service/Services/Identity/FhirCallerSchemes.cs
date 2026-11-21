using System.Security.Claims;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FhirService.Services.Identity;

/// <summary>
/// The two kinds of caller fhir-service serves, each under its own
/// authentication scheme.
///
/// <list type="bullet">
///   <item><b>CHO</b> (<see cref="Cho"/>, the shared "Bearer" scheme from
///   <c>AddChoAuthentication</c>): the portal and other CHO services. Tenant
///   from the token, CHO permissions decide.</item>
///   <item><b>SMART</b> (<see cref="Smart"/>): external FHIR clients with tokens
///   from a trusted SMART issuer (smart-auth-service or a customer IdP).
///   SMART scopes and the patient binding decide.</item>
/// </list>
///
/// <see cref="Selector"/> is the default scheme. It reads the token's
/// <c>iss</c> (unvalidated, for routing only) and forwards to exactly one of
/// the two schemes, which then validates the token fully. A token is therefore
/// authenticated by one scheme or not at all; the two never merge into one
/// principal. Startup refuses an issuer configured in both trust lists.
/// </summary>
public static class FhirCallerSchemes
{
    /// <summary>The shared CHO scheme registered by AddChoAuthentication.</summary>
    public const string Cho = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>SMART on FHIR tokens from the SMART trust registry.</summary>
    public const string Smart = "Smart";

    /// <summary>
    /// CDS Hooks client JWTs (CDS Hooks 2.0) from a configured EHR, on
    /// <c>/cds-services</c> only. See <see cref="CdsHooksClientHandler"/>.
    /// </summary>
    public const string CdsHooks = "CdsHooks";

    /// <summary>Default scheme: routes a request to <see cref="Cho"/>, <see cref="Smart"/> or <see cref="CdsHooks"/>.</summary>
    public const string Selector = "FhirCaller";

    /// <summary>
    /// Authentication types of the marker identities the two schemes add after a
    /// token validates. A token cannot add an identity, so these cannot be
    /// forged by claim content, unlike a claim would.
    /// </summary>
    internal const string ChoMarker = "fhir-caller:cho";
    internal const string SmartMarker = "fhir-caller:smart";
    internal const string CdsHooksMarker = "fhir-caller:cds-hooks";

    private static readonly string[] Markers = [ChoMarker, SmartMarker, CdsHooksMarker];

    /// <summary>A caller authenticated by the CHO scheme.</summary>
    public static bool IsCho(ClaimsPrincipal? principal) => HasOnly(principal, ChoMarker);

    /// <summary>A caller authenticated by the SMART scheme.</summary>
    public static bool IsSmart(ClaimsPrincipal? principal) => HasOnly(principal, SmartMarker);

    /// <summary>An EHR authenticated by its CDS Hooks client JWT.</summary>
    public static bool IsCdsHooksClient(ClaimsPrincipal? principal) => HasOnly(principal, CdsHooksMarker);

    /// <summary>A caller from outside CHO (SMART or CDS Hooks): its own token is never forwarded to CHO services.</summary>
    public static bool IsExternal(ClaimsPrincipal? principal) => IsSmart(principal) || IsCdsHooksClient(principal);

    private static bool HasOnly(ClaimsPrincipal? principal, string marker)
        => principal?.Identity?.IsAuthenticated == true
           && principal.Identities.Any(i => i.AuthenticationType == marker)
           && !principal.Identities.Any(i => i.AuthenticationType != marker && Markers.Contains(i.AuthenticationType));

    internal static void Mark(ClaimsPrincipal? principal, string marker, IEnumerable<Claim>? claims = null)
        => principal?.AddIdentity(new ClaimsIdentity(claims, marker));

    /// <summary>
    /// Registers the selector as the default scheme and marks CHO principals.
    /// Call after <c>AddChoAuthentication</c> and <c>AddSmartTrust</c>.
    /// </summary>
    public static IServiceCollection AddFhirCallerSchemes(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment? environment = null)
    {
        var cho = new ChoAuthOptions();
        configuration.GetSection(ChoAuthOptions.SectionName).Bind(cho);
        var smart = new SmartTrustOptions();
        configuration.GetSection(SmartTrustOptions.SectionName).Bind(smart);

        var cds = new CdsHooksOptions();
        configuration.GetSection(CdsHooksOptions.SectionName).Bind(cds);
        cds.Validate(environment?.IsDevelopment() == true);

        var choIssuers = cho.Issuers.Select(i => i.Issuer).ToHashSet(StringComparer.Ordinal);
        // The Demo issuer is checked as configured too: it is trusted in the
        // spelling smart-auth-service writes, but it names the same server.
        var overlap = smart.NormalizedIssuers().Select(i => i.Issuer)
            .Concat(smart.TrustedIssuers.Count == 0 && !string.IsNullOrWhiteSpace(smart.Issuer) ? [smart.Issuer!] : [])
            .Where(choIssuers.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (overlap.Count > 0)
        {
            // One issuer in both lists would make the scheme a token lands in a
            // matter of routing order, and a CHO token could be read as SMART.
            throw new SmartTrustValidationException(
                "These issuers are trusted both as CHO issuers (ChoAuth:Issuers) and as SMART issuers "
                + $"(SmartAuth): {string.Join(", ", overlap)}. Each issuer must belong to exactly one scheme.");
        }

        var cdsIssuers = cds.TrustedClients.Select(c => c.Issuer).ToHashSet(StringComparer.Ordinal);
        var cdsOverlap = cdsIssuers.Where(i => choIssuers.Contains(i)
                || smart.NormalizedIssuers().Any(s => string.Equals(s.Issuer, i, StringComparison.Ordinal)))
            .ToList();
        if (cdsOverlap.Count > 0)
        {
            throw new SmartTrustValidationException(
                "These issuers are trusted as CDS Hooks clients (CdsHooks:TrustedClients) and also as CHO or SMART "
                + $"issuers: {string.Join(", ", cdsOverlap)}. Each issuer must belong to exactly one scheme.");
        }

        services.Configure<CdsHooksOptions>(configuration.GetSection(CdsHooksOptions.SectionName));
        services.AddSingleton<CdsHooksClientKeys>();
        services.AddSingleton<CdsHooksReplayCache>();
        services.AddHttpClient(CdsHooksClientKeys.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = Selector;
                options.DefaultAuthenticateScheme = Selector;
                options.DefaultChallengeScheme = Selector;
                options.DefaultForbidScheme = Selector;
            })
            .AddPolicyScheme(Selector, "CHO, SMART or CDS Hooks bearer", policy =>
            {
                policy.ForwardDefaultSelector = context => SelectScheme(context, choIssuers, cdsIssuers);
            })
            .AddScheme<CdsHooksClientSchemeOptions, CdsHooksClientHandler>(CdsHooks, "CDS Hooks client JWT", _ => { });

        // Mark principals the CHO scheme validated. PostConfigure wraps the
        // shared handler's own events rather than replacing them.
        services.PostConfigure<JwtBearerOptions>(Cho, jwt =>
        {
            var inner = jwt.Events?.OnTokenValidated;
            jwt.Events ??= new JwtBearerEvents();
            jwt.Events.OnTokenValidated = async context =>
            {
                if (inner != null) await inner(context);
                if (context.Result?.Failure == null && context.Principal != null)
                    Mark(context.Principal, ChoMarker);
            };
            jwt.Events.OnChallenge = WriteChallengeAsync;
        });

        return services;
    }

    /// <summary>
    /// Routes by the token's <c>iss</c>: a CHO issuer goes to the CHO scheme,
    /// a configured CDS Client calling <c>/cds-services</c> to the CDS Hooks
    /// scheme, anything else (including no token) to SMART. Routing never grants
    /// anything; the chosen scheme still validates signature, issuer,
    /// audience and lifetime.
    /// </summary>
    internal static string SelectScheme(HttpContext context, IReadOnlySet<string> choIssuers)
        => SelectScheme(context, choIssuers, new HashSet<string>(StringComparer.Ordinal));

    internal static string SelectScheme(HttpContext context, IReadOnlySet<string> choIssuers, IReadOnlySet<string> cdsIssuers)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            try
            {
                var handler = new JsonWebTokenHandler();
                if (handler.CanReadToken(raw))
                {
                    var issuer = handler.ReadJsonWebToken(raw).Issuer;
                    if (choIssuers.Contains(issuer))
                        return Cho;
                    if (cdsIssuers.Contains(issuer) && context.Request.Path.StartsWithSegments("/cds-services"))
                        return CdsHooks;
                }
            }
            catch (ArgumentException)
            {
                // Unreadable: SMART validation will reject it.
            }
        }

        return Smart;
    }

    /// <summary>
    /// 401 with a FHIR OperationOutcome, the shape FHIR clients expect from
    /// every refusal under /fhir/r4.
    /// </summary>
    internal static async Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await FhirService.Middleware.FhirErrorResponse.WriteAsync(
            context.HttpContext, StatusCodes.Status401Unauthorized,
            Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
            Hl7.Fhir.Model.OperationOutcome.IssueType.Login,
            "Authentication required. Include a valid Bearer token.");
    }
}
