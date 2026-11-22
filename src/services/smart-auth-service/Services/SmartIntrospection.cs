using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace SmartAuthService.Services;

/// <summary>
/// Token introspection (RFC 7662, SMART App Launch 2.2 token introspection).
/// OpenIddict authenticates the caller, which must be a confidential client
/// registered with the introspection permission (a resource server; see
/// <c>introspection</c> on client registration), and answers inactive for a
/// token it did not issue or that has expired or been revoked.
///
/// OpenIddict answers a token only to its presenter or its audience, and
/// returns the SMART launch context (<c>patient</c>, <c>encounter</c>,
/// <c>fhirUser</c>) only to the audience. CHO's access tokens name
/// <see cref="ResourceServerClientId"/> as their audience, so the resource
/// server registers under that client id.
///
/// CHO adds one rule: a client sees only its own tenant's tokens. A token
/// issued for another tenant, or one without a tenant, is answered
/// <c>{"active": false}</c> exactly as an unknown token is, so introspection
/// never tells one tenant's resource server about another tenant's users.
/// </summary>
public static class SmartIntrospection
{
    /// <summary>The audience of CHO's access tokens, and the client id its resource server introspects as.</summary>
    public const string ResourceServerClientId = "fhir-api";

    public static OpenIddictServerBuilder AddSmartIntrospection(this OpenIddictServerBuilder options)
    {
        options.AddEventHandler(RequireSameTenant.Descriptor);
        return options;
    }

    /// <summary>Whether a caller of <paramref name="callerTenant"/> may see a token of <paramref name="tokenTenant"/>.</summary>
    public static bool SameTenant(string? callerTenant, string? tokenTenant)
        => !string.IsNullOrEmpty(callerTenant)
           && !string.IsNullOrEmpty(tokenTenant)
           && string.Equals(callerTenant, tokenTenant, StringComparison.Ordinal);

    public sealed class RequireSameTenant : IOpenIddictServerHandler<HandleIntrospectionRequestContext>
    {
        private readonly ISmartIdentityStore _store;

        public RequireSameTenant(ISmartIdentityStore store) => _store = store;

        public static OpenIddictServerHandlerDescriptor Descriptor { get; }
            = OpenIddictServerHandlerDescriptor.CreateBuilder<HandleIntrospectionRequestContext>()
                .UseScopedHandler<RequireSameTenant>()
                .SetOrder(OpenIddictServerHandlers.Introspection.AttachPrincipal.Descriptor.Order + 500)
                .SetType(OpenIddictServerHandlerType.Custom)
                .Build();

        public async ValueTask HandleAsync(HandleIntrospectionRequestContext context)
        {
            var tokenTenant = context.GenericTokenPrincipal?.GetClaim(SmartTokenContextResolver.TenantClaim);
            var clientId = context.Request.ClientId;
            var caller = string.IsNullOrEmpty(clientId)
                ? null
                : await _store.FindClientAsync(clientId, context.CancellationToken);

            if (!SameTenant(caller?.TenantId, tokenTenant))
            {
                // invalid_token is answered as {"active": false} (OpenIddict's
                // NormalizeErrorResponse), indistinguishable from an unknown token.
                context.Reject(
                    error: Errors.InvalidToken,
                    description: "The token is not active for this client.");
            }
        }
    }
}
