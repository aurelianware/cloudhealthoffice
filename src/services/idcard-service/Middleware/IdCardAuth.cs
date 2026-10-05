namespace IdCardService.Middleware;

/// <summary>Names for the provider (external caller) authentication used by <c>/scan</c>.</summary>
public static class IdCardAuth
{
    /// <summary>Authorization policy on the scan endpoint.</summary>
    public const string ProviderJwtPolicy = "ProviderJwt";

    /// <summary>Provider JWT scheme. Distinct from the CHO "Bearer" scheme.</summary>
    public const string ProviderJwtScheme = "ProviderJwt";

    /// <summary>Development-only provider scheme (<see cref="DevProviderAuthHandler"/>).</summary>
    public const string ProviderJwtDevScheme = "ProviderJwt-Dev";
}
