using System.Security.Claims;
using OpenIddict.Abstractions;
using SmartAuthService.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace SmartAuthService.Services;

/// <summary>Which kind of SMART caller a token is for.</summary>
public static class SmartCallerContext
{
    public const string Member = "member";
    public const string Provider = "provider";
    public const string Client = "client";
}

/// <summary>
/// Everything a SMART token says about tenancy and identity, all of it read
/// from server-side bindings. Nothing here is taken from the authorization
/// request, a header or a launch body.
/// </summary>
public sealed record SmartTokenContext(
    string Context,
    string TenantId,
    string Subject,
    string? Patient = null,
    string? Encounter = null,
    string? FhirUser = null,
    string? Npi = null,
    SmartIdentity? Identity = null);

/// <summary>A context, or the reason there is none. A refusal issues no token.</summary>
public sealed record SmartContextResolution(SmartTokenContext? Context, string? Refusal)
{
    public static SmartContextResolution Refuse(string reason) => new(null, reason);
    public static SmartContextResolution Grant(SmartTokenContext context) => new(context, null);
}

/// <summary>
/// Decides the tenant, subject and patient of every SMART token from the
/// identity bindings in <see cref="ISmartIdentityStore"/>. Fails closed: no
/// binding, a binding of the wrong kind, or a client registered to another
/// tenant all mean no token.
/// </summary>
public sealed class SmartTokenContextResolver
{
    /// <summary>Private principal claims: kept in codes and refresh tokens, never in an access or identity token.</summary>
    public const string ContextClaim = "cho_ctx";
    public const string IdentityIssuerClaim = "cho_idp_iss";
    public const string IdentitySubjectClaim = "cho_idp_sub";

    public const string TenantClaim = "tenant_id";
    public const string NpiClaim = "npi";

    private readonly ISmartIdentityStore _store;
    private readonly ILaunchContextStore _launches;

    public SmartTokenContextResolver(ISmartIdentityStore store, ILaunchContextStore launches)
    {
        _store = store;
        _launches = launches;
    }

    /// <summary>
    /// Authorization endpoint: a signed-in person using an app.
    /// <paramref name="launchToken"/> is the request's <c>launch</c> parameter;
    /// it is consumed only once every other check has passed, and only for the
    /// provider's tenant and this client.
    /// </summary>
    public async Task<SmartContextResolution> ResolveInteractiveAsync(
        SmartIdentity identity,
        string clientId,
        IReadOnlyCollection<string> scopes,
        string? launchToken,
        CancellationToken ct = default)
    {
        var client = await _store.FindClientAsync(clientId, ct);
        if (client == null)
            return SmartContextResolution.Refuse("client_not_bound_to_tenant");

        if (scopes.Any(s => s.StartsWith("system/", StringComparison.Ordinal)))
            return SmartContextResolution.Refuse("system_scope_requires_client_credentials");

        var hasLaunch = !string.IsNullOrEmpty(launchToken);
        var providerShaped = hasLaunch
            || scopes.Contains(SmartScopes.Launch)
            || scopes.Any(s => s.StartsWith("user/", StringComparison.Ordinal));
        var patientShaped = scopes.Contains(SmartScopes.LaunchPatient)
            || scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal));

        SmartTokenContext context;
        if (providerShaped)
        {
            var provider = await _store.FindActiveProviderLinkAsync(identity, ct);
            if (provider == null)
                return SmartContextResolution.Refuse("no_provider_mapping");
            if (client.Kind != SmartClientKind.ProviderApp)
                return SmartContextResolution.Refuse("client_is_not_a_provider_app");
            if (!string.Equals(client.TenantId, provider.TenantId, StringComparison.Ordinal))
                return SmartContextResolution.Refuse("client_registered_to_another_tenant");

            LaunchContext? launch = null;
            if (hasLaunch)
            {
                // Atomic, single use, and only a launch of THIS tenant for THIS
                // client (and THIS provider, when the launch names one): any
                // other launch is neither usable nor burned.
                launch = await _launches.ConsumeAsync(
                    launchToken!, provider.TenantId, client.ClientId, provider.ProviderId, ct);
                if (launch == null)
                    return SmartContextResolution.Refuse("launch_unknown_used_expired_or_other_tenant");
            }

            var result = ProviderContext(provider, identity, client, launch, patientShaped);
            if (result.Context == null) return result;
            context = result.Context;
        }
        else
        {
            var member = await _store.FindActiveMemberLinkAsync(identity, ct);
            if (member != null)
            {
                if (client.Kind != SmartClientKind.PatientApp)
                    return SmartContextResolution.Refuse("client_is_not_a_patient_app");

                context = new SmartTokenContext(
                    SmartCallerContext.Member, member.TenantId, identity.Subject,
                    Patient: member.MemberId,
                    FhirUser: $"Patient/{member.MemberId}",
                    Identity: identity);
            }
            else if (!patientShaped && await _store.FindActiveProviderLinkAsync(identity, ct) is { } provider)
            {
                var result = ProviderContext(provider, identity, client, launch: null, patientShaped: false);
                if (result.Context == null) return result;
                context = result.Context;
            }
            else
            {
                return SmartContextResolution.Refuse("no_member_mapping");
            }
        }

        if (!string.Equals(client.TenantId, context.TenantId, StringComparison.Ordinal))
            return SmartContextResolution.Refuse("client_registered_to_another_tenant");

        return SmartContextResolution.Grant(context);
    }

    private static SmartContextResolution ProviderContext(
        ProviderUserLink provider, SmartIdentity identity, ClientTenantRegistration client,
        LaunchContext? launch, bool patientShaped)
    {
        if (client.Kind != SmartClientKind.ProviderApp)
            return SmartContextResolution.Refuse("client_is_not_a_provider_app");

        string? patient = null;
        string? encounter = null;
        if (launch != null)
        {
            // The launch was registered by an authenticated caller of ONE
            // tenant, for ONE client. It cannot carry a patient into another
            // tenant's provider session or into another app.
            if (!string.Equals(launch.TenantId, provider.TenantId, StringComparison.Ordinal))
                return SmartContextResolution.Refuse("launch_registered_in_another_tenant");
            if (!string.Equals(launch.ClientId, client.ClientId, StringComparison.Ordinal))
                return SmartContextResolution.Refuse("launch_registered_for_another_client");
            if (launch.PractitionerId != null
                && !string.Equals(launch.PractitionerId, provider.ProviderId, StringComparison.Ordinal))
                return SmartContextResolution.Refuse("launch_registered_for_another_practitioner");

            patient = launch.PatientId;
            encounter = launch.EncounterId;
        }

        // No patient picker: patient context for a provider comes only from an
        // EHR launch. A patient-scoped token without a patient would be refused
        // by the FHIR server anyway; refusing here says why.
        if (patientShaped && string.IsNullOrEmpty(patient))
            return SmartContextResolution.Refuse("patient_context_requires_ehr_launch");

        return SmartContextResolution.Grant(new SmartTokenContext(
            SmartCallerContext.Provider, provider.TenantId,
            // fhir-service's Provider Access attribution keys on the token's
            // sub, so for a provider user it is the mapped provider id.
            Subject: provider.ProviderId,
            Patient: patient,
            Encounter: encounter,
            FhirUser: $"Practitioner/{provider.ProviderId}",
            Npi: provider.Npi,
            Identity: identity));
    }

    /// <summary>Token endpoint, client_credentials: the registration's tenant, nothing else.</summary>
    public async Task<SmartContextResolution> ResolveClientCredentialsAsync(
        string clientId, IReadOnlyCollection<string> scopes, CancellationToken ct = default)
    {
        var client = await _store.FindClientAsync(clientId, ct);
        if (client == null)
            return SmartContextResolution.Refuse("client_not_bound_to_tenant");
        if (client.Kind != SmartClientKind.Backend)
            return SmartContextResolution.Refuse("client_is_not_a_backend_client");
        if (scopes.Any(s => s.StartsWith("patient/", StringComparison.Ordinal)
                            || s.StartsWith("user/", StringComparison.Ordinal)
                            || s.StartsWith("launch", StringComparison.Ordinal)))
            return SmartContextResolution.Refuse("client_credentials_take_system_scopes_only");

        return SmartContextResolution.Grant(
            new SmartTokenContext(SmartCallerContext.Client, client.TenantId, clientId));
    }

    /// <summary>
    /// Code and refresh exchanges: the binding the code was issued under must
    /// still exist, unchanged. A revoked link or a deleted client registration
    /// stops refresh at once rather than at refresh-token expiry.
    /// </summary>
    public async Task<string?> RevalidateAsync(ClaimsPrincipal principal, string? clientId, CancellationToken ct = default)
    {
        var context = principal.GetClaim(ContextClaim);
        var tenant = principal.GetClaim(TenantClaim);
        var subject = principal.GetClaim(Claims.Subject);
        if (string.IsNullOrEmpty(context) || string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(clientId))
            return "grant_has_no_tenant_binding";

        var client = await _store.FindClientAsync(clientId, ct);
        if (client == null || !string.Equals(client.TenantId, tenant, StringComparison.Ordinal))
            return "client_no_longer_bound_to_tenant";

        var issuer = principal.GetClaim(IdentityIssuerClaim);
        var idpSubject = principal.GetClaim(IdentitySubjectClaim);
        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(idpSubject))
            return "grant_has_no_identity";
        var identity = new SmartIdentity(issuer, idpSubject);

        switch (context)
        {
            case SmartCallerContext.Member:
                var member = await _store.FindActiveMemberLinkAsync(identity, ct);
                return member != null
                       && member.TenantId == tenant
                       && member.MemberId == principal.GetClaim(SmartClaims.Patient)
                    ? null
                    : "member_mapping_revoked_or_changed";

            case SmartCallerContext.Provider:
                var provider = await _store.FindActiveProviderLinkAsync(identity, ct);
                return provider != null
                       && provider.TenantId == tenant
                       && provider.ProviderId == subject
                    ? null
                    : "provider_mapping_revoked_or_changed";

            default:
                return "unexpected_grant_context";
        }
    }

    /// <summary>
    /// The SMART principal for <paramref name="context"/>. Destinations are
    /// explicit for every claim: the private binding claims go into the code
    /// and refresh token only, so refresh can re-check the binding.
    /// </summary>
    public static ClaimsPrincipal CreatePrincipal(SmartTokenContext context, IEnumerable<string> scopes)
    {
        var identity = new ClaimsIdentity(
            authenticationType: OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, context.Subject);
        identity.SetClaim(TenantClaim, context.TenantId);
        identity.SetClaim(ContextClaim, context.Context);
        if (context.Patient != null) identity.SetClaim(SmartClaims.Patient, context.Patient);
        if (context.Encounter != null) identity.SetClaim(SmartClaims.Encounter, context.Encounter);
        if (context.FhirUser != null) identity.SetClaim(SmartClaims.FhirUser, context.FhirUser);
        if (context.Npi != null) identity.SetClaim(NpiClaim, context.Npi);
        if (context.Identity is { } who)
        {
            identity.SetClaim(IdentityIssuerClaim, who.Issuer);
            identity.SetClaim(IdentitySubjectClaim, who.Subject);
        }

        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources("fhir-api");
        ApplyDestinations(principal);
        return principal;
    }

    public static void ApplyDestinations(ClaimsPrincipal principal)
        => principal.SetDestinations(static claim => claim.Type switch
        {
            Claims.Subject or TenantClaim or SmartClaims.FhirUser
                => [Destinations.AccessToken, Destinations.IdentityToken],
            SmartClaims.Patient or SmartClaims.Encounter or NpiClaim
                => [Destinations.AccessToken],
            Claims.Name => [Destinations.IdentityToken],
            // cho_ctx / cho_idp_* and anything else: no token destination.
            _ => [],
        });
}
