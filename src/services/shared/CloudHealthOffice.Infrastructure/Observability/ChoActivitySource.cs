using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CloudHealthOffice.Infrastructure.Observability;

/// <summary>
/// Central ActivitySource for Cloud Health Office business-level spans.
/// All custom tracing flows through this single source so services
/// only need to subscribe to "CloudHealthOffice" to capture everything.
/// </summary>
public static class ChoActivitySource
{
    public const string Name = "CloudHealthOffice";

    public static readonly ActivitySource Instance = new(Name, GetAssemblyVersion());

    /// <summary>
    /// Starts a new activity (span) with standard CHO tags.
    /// Returns null if no listener is registered — callers must null-check.
    /// </summary>
    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        string? tenantId = null,
        string? claimId = null,
        string? claimType = null,
        string? memberId = null)
    {
        var activity = Instance.StartActivity(name, kind);
        if (activity is null) return null;

        if (tenantId is not null)
            activity.SetTag("cho.tenant_id", tenantId);
        if (claimId is not null)
            activity.SetTag("cho.claim_id_hash", HashIdentifier(claimId));
        if (claimType is not null)
            activity.SetTag("cho.claim_type", claimType);
        if (memberId is not null)
            activity.SetTag("cho.member_id_hash", HashIdentifier(memberId));

        return activity;
    }

    /// <summary>
    /// One-way SHA-256 hash of an identifier to avoid leaking PHI into traces.
    /// Used for member IDs, claim IDs, and any other identifier that could
    /// be joined back to a member.
    /// </summary>
    public static string HashIdentifier(string identifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string GetAssemblyVersion()
    {
        return typeof(ChoActivitySource).Assembly
            .GetName().Version?.ToString() ?? "0.0.0";
    }
}
