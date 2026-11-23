namespace ClaimsService.Services.Adjudication.Shadow;

/// <summary>
/// Where the core and CHO disagree about prior authorization on a claim: one
/// side denied the claim (or a line) for authorization and the other did not.
/// <list type="bullet">
///   <item><see cref="CoreDeniedChoPays"/>: the core denied for a missing or
///   invalid authorization, and CHO found a valid one and would pay. A
///   possible underpayment, and a provider appeal waiting to happen.</item>
///   <item><see cref="CorePaidChoDenies"/>: the core paid a service that, in
///   CHO's check, needed an authorization the claim did not have. A possible
///   unsupported payment.</item>
/// </list>
/// Both are leads for the plan's review, not determinations. CHO checks the
/// lines in its prior-authorization scope against the plan's own authorizations
/// (loaded through the authorization import), by the authorization number the
/// core's file carries.
/// </summary>
public static class ShadowAuthorizationMismatch
{
    public const string CoreDeniedChoPays = "Core denied for authorization, CHO would pay";
    public const string CorePaidChoDenies = "Core paid, CHO denies for authorization";

    /// <summary>
    /// CARCs that deny for authorization: 15 (authorization number missing,
    /// invalid or not for these services or this provider), 39 (denied when
    /// authorization was requested), 62 (absence of, or exceeded,
    /// authorization), 197 (authorization absent) and 198 (authorization
    /// exceeded).
    /// </summary>
    public static readonly IReadOnlySet<string> AuthorizationCarcs =
        new HashSet<string>(StringComparer.Ordinal) { "15", "39", "62", "197", "198" };

    public static bool IsAuthorizationCarc(string? code) =>
        !string.IsNullOrWhiteSpace(code) && AuthorizationCarcs.Contains(code.Trim());

    /// <summary>The claim's authorization disagreement, or null when there is none.</summary>
    public static string? For(ShadowAdjudicationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!Decided(result)) return null;

        var coreForAuth = IsAuthorizationCarc(result.CoreDenialCode)
                          || result.Lines.Any(l => IsAuthorizationCarc(l.CoreDenialCode));
        var choForAuth = IsAuthorizationCarc(result.ChoDenialCode)
                         || result.Lines.Any(l => IsAuthorizationCarc(l.ChoDenialCode));

        if (coreForAuth && !choForAuth && result.ChoOutcome == ShadowOutcome.Paid) return CoreDeniedChoPays;
        if (choForAuth && !coreForAuth && result.CoreOutcome == ShadowOutcome.Paid) return CorePaidChoDenies;
        return null;
    }

    /// <summary>One line's authorization disagreement, or null when there is none.</summary>
    public static string? For(ShadowAdjudicationResult result, ShadowLineComparison line)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(line);
        if (!Decided(result)) return null;

        var coreForAuth = IsAuthorizationCarc(line.CoreDenialCode);
        var choForAuth = IsAuthorizationCarc(line.ChoDenialCode);

        if (coreForAuth && line.ChoDenialCode is null) return CoreDeniedChoPays;
        if (choForAuth && line.CoreDenialCode is null) return CorePaidChoDenies;
        return null;
    }

    private static bool Decided(ShadowAdjudicationResult result) =>
        result.Kind is not (ShadowDiscrepancyKind.NotComparable or ShadowDiscrepancyKind.ChoUndecided or ShadowDiscrepancyKind.Match);
}
