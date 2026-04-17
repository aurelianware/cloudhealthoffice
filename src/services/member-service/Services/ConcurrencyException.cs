namespace MemberService.Services;

/// <summary>
/// Thrown when the event publisher exhausts its version-conflict retries.
/// Callers may treat this as a transient failure (retry at request level).
/// </summary>
public sealed class ConcurrencyException : Exception
{
    public string TenantId { get; }
    public string MemberId { get; }
    public int Attempts { get; }

    public ConcurrencyException(string tenantId, string memberId, int attempts, Exception? inner = null)
        : base($"Concurrent version conflicts for {tenantId}:{memberId} after {attempts} attempts.", inner)
    {
        TenantId = tenantId;
        MemberId = memberId;
        Attempts = attempts;
    }
}
