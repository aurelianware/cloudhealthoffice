namespace CloudHealthOffice.TokenService;

/// <summary>
/// Bound from the <c>TokenService</c> section. Governs which Entra tokens are
/// accepted and who counts as a platform administrator.
/// </summary>
public sealed class TokenServiceOptions
{
    public const string SectionName = "TokenService";

    /// <summary>
    /// Audiences an inbound Entra access token may carry: the CHO API app
    /// registration's App ID URI (v1 tokens) and client id (v2 tokens).
    /// </summary>
    public List<string> Audiences { get; set; } = new();

    /// <summary>Delegated scope the token's <c>scp</c> must contain.</summary>
    public string RequiredScope { get; set; } = "Cho.Token";

    /// <summary>
    /// Accepted Entra issuer forms. <c>{tid}</c> is replaced with the token's own
    /// <c>tid</c> claim, so a token's issuer must name the directory it claims to
    /// come from.
    /// </summary>
    public List<string> IssuerTemplates { get; set; } = new();

    /// <summary>
    /// CHO's own Entra directory. Only users from this directory holding the
    /// <see cref="PlatformAdminAppRole"/> app role are platform administrators.
    /// Empty disables platform administration.
    /// </summary>
    public string PlatformTenantId { get; set; } = string.Empty;

    /// <summary>App role (in the Entra token's <c>roles</c>) that marks a platform administrator.</summary>
    public string PlatformAdminAppRole { get; set; } = "PlatformAdmin";

    /// <summary>Lifetime of an issued CHO token.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    internal static readonly string[] DefaultIssuerTemplates =
    [
        "https://login.microsoftonline.com/{tid}/v2.0",
        "https://sts.windows.net/{tid}/",
    ];

    internal IReadOnlyList<string> EffectiveIssuerTemplates
        => IssuerTemplates.Count > 0 ? IssuerTemplates : DefaultIssuerTemplates;

    public void Validate()
    {
        if (Audiences.Count == 0 || Audiences.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"{SectionName}:Audiences must list at least one Entra audience.");
        if (string.IsNullOrWhiteSpace(RequiredScope))
            throw new InvalidOperationException($"{SectionName}:RequiredScope is required.");
        if (EffectiveIssuerTemplates.Any(t => !t.Contains("{tid}", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{SectionName}:IssuerTemplates entries must contain {{tid}} so the issuer is bound to the token's directory.");
        if (TokenLifetime <= TimeSpan.Zero || TokenLifetime > TimeSpan.FromHours(1))
            throw new InvalidOperationException($"{SectionName}:TokenLifetime must be between 0 and 1 hour.");
    }
}
