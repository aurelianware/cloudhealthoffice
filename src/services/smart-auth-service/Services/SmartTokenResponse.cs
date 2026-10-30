using OpenIddict.Abstractions;
using OpenIddict.Server;

namespace SmartAuthService.Services;

/// <summary>
/// The token response always carries <c>scope</c>. RFC 6749 lets a server
/// leave it out when it granted exactly what was asked, and OpenIddict does,
/// but SMART App Launch and SMART Backend Services both require it in every
/// access token response (Inferno's bulk data suite checks it).
///
/// <para>
/// It also carries the launch context SMART App Launch returns beside the
/// token: <c>patient</c> and <c>encounter</c>, when the token is bound to them.
/// The access token holds the same claims, but an app is not meant to read its
/// access token, and without these parameters it cannot learn whose record it
/// was given.
/// </para>
/// </summary>
public static class SmartTokenResponse
{
    public static OpenIddictServerHandlerDescriptor GrantedScopeHandler { get; } =
        OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
            .UseInlineHandler(context =>
            {
                AttachGrantedScope(context.EndpointType, context.Response, context.Principal);
                AttachLaunchContext(context.EndpointType, context.Response, context.Principal);
                return default;
            })
            .SetOrder(OpenIddictServerHandlers.AttachSignInParameters.Descriptor.Order + 1)
            .SetType(OpenIddictServerHandlerType.Custom)
            .Build();

    internal static void AttachGrantedScope(
        OpenIddictServerEndpointType endpoint, OpenIddictResponse response, System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (endpoint != OpenIddictServerEndpointType.Token
            || string.IsNullOrEmpty(response.AccessToken)
            || !string.IsNullOrEmpty(response.Scope)
            || principal is null)
        {
            return;
        }

        var scopes = principal.GetScopes();
        if (!scopes.IsDefaultOrEmpty)
            response.Scope = string.Join(' ', scopes);
    }

    internal static void AttachLaunchContext(
        OpenIddictServerEndpointType endpoint, OpenIddictResponse response, System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (endpoint != OpenIddictServerEndpointType.Token
            || string.IsNullOrEmpty(response.AccessToken)
            || principal is null)
        {
            return;
        }

        foreach (var name in new[] { Models.SmartClaims.Patient, Models.SmartClaims.Encounter })
        {
            if (principal.GetClaim(name) is { Length: > 0 } value && response.GetParameter(name) is null)
                response.SetParameter(name, value);
        }
    }
}
