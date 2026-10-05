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

    /// <summary>Default scheme: routes a request to <see cref="Cho"/> or <see cref="Smart"/>.</summary>
    public const string Selector = "FhirCaller";

    /// <summary>
    /// Authentication types of the marker identities the two schemes add after a
    /// token validates. A token cannot add an identity, so these cannot be
    /// forged by claim content, unlike a claim would.
    /// </summary>
    internal const string ChoMarker = "fhir-caller:cho";
    internal const string SmartMarker = "fhir-caller:smart";

    /// <summary>A caller authenticated by the CHO scheme.</summary>
    public static bool IsCho(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
           && principal.Identities.Any(i => i.AuthenticationType == ChoMarker)
           && !principal.Identities.Any(i => i.AuthenticationType == SmartMarker);

    /// <summary>A caller authenticated by the SMART scheme.</summary>
    public static bool IsSmart(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
           && principal.Identities.Any(i => i.AuthenticationType == SmartMarker)
           && !principal.Identities.Any(i => i.AuthenticationType == ChoMarker);

    internal static void Mark(ClaimsPrincipal? principal, string marker, IEnumerable<Claim>? claims = null)
        => principal?.AddIdentity(new ClaimsIdentity(claims, marker));

    /// <summary>
    /// Registers the selector as the default scheme and marks CHO principals.
    /// Call after <c>AddChoAuthentication</c> and <c>AddSmartTrust</c>.
    /// </summary>
    public static IServiceCollection AddFhirCallerSchemes(
        this IServiceCollection services, IConfiguration configuration)
    {
        var cho = new ChoAuthOptions();
        configuration.GetSection(ChoAuthOptions.SectionName).Bind(cho);
        var smart = new SmartTrustOptions();
        configuration.GetSection(SmartTrustOptions.SectionName).Bind(smart);

        var choIssuers = cho.Issuers.Select(i => i.Issuer).ToHashSet(StringComparer.Ordinal);
        var overlap = smart.NormalizedIssuers().Select(i => i.Issuer).Where(choIssuers.Contains).ToList();
        if (overlap.Count > 0)
        {
            // One issuer in both lists would make the scheme a token lands in a
            // matter of routing order, and a CHO token could be read as SMART.
            throw new SmartTrustValidationException(
                "These issuers are trusted both as CHO issuers (ChoAuth:Issuers) and as SMART issuers "
                + $"(SmartAuth): {string.Join(", ", overlap)}. Each issuer must belong to exactly one scheme.");
        }

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = Selector;
                options.DefaultAuthenticateScheme = Selector;
                options.DefaultChallengeScheme = Selector;
                options.DefaultForbidScheme = Selector;
            })
            .AddPolicyScheme(Selector, "CHO or SMART bearer", policy =>
            {
                policy.ForwardDefaultSelector = context => SelectScheme(context, choIssuers);
            });

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
    /// anything else (including no token) to SMART. Routing never grants
    /// anything; the chosen scheme still validates signature, issuer,
    /// audience and lifetime.
    /// </summary>
    internal static string SelectScheme(HttpContext context, IReadOnlySet<string> choIssuers)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var raw = header["Bearer ".Length..].Trim();
            try
            {
                var handler = new JsonWebTokenHandler();
                if (handler.CanReadToken(raw) && choIssuers.Contains(handler.ReadJsonWebToken(raw).Issuer))
                    return Cho;
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
