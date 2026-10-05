using CloudHealthOffice.Infrastructure.Security;

namespace CloudHealthOffice.ProviderVerificationService;

/// <summary>Permissions this service enforces (defined in <c>ChoRolePermissions</c>).</summary>
public static class ProviderVerificationPermissions
{
    /// <summary>Lookups, single-provider verification and integrity scores.</summary>
    public const string Read = "providers:read";

    /// <summary>Verification actions that change no credentialing state (batch verification).</summary>
    public const string Write = "providers:write";

    /// <summary>Default for unannotated writes: anything that changes credentialing state.</summary>
    public const string Credential = "providers:credential";
}

public static class ExternalSourceHttpClientExtensions
{
    /// <summary>
    /// Removes <see cref="ChoOutboundTokenHandler"/> (added to every factory
    /// client by <c>AddChoAuthentication</c>) from a client that calls an
    /// external data source (NPPES, OIG/LEIE, SAM.gov, PECOS, Open Payments,
    /// FSMB, state boards). The shared handler already refuses external hosts;
    /// this keeps a CHO token off these clients even if a base URL is
    /// configured with a host the handler would treat as internal.
    /// </summary>
    public static IHttpClientBuilder WithoutChoTokens(this IHttpClientBuilder builder)
        => builder.ConfigureAdditionalHttpMessageHandlers((handlers, _) =>
        {
            for (var i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i] is ChoOutboundTokenHandler)
                    handlers.RemoveAt(i);
            }
        });
}
