namespace ClaimsService.Services.Resolution;

/// <summary>
/// member-service gave no definitive answer for a member (transport error,
/// timeout, non-404 failure status, unreadable body). Distinct from a
/// definitive 404, for which <see cref="IMemberResolver.GetMemberAsync"/>
/// returns null. The adjudication pipeline records it as
/// <c>ClaimAdjudicationContext.MemberLookup = Unavailable</c> and pends the
/// claim; it never adjudicates the member as eligible.
/// </summary>
public sealed class MemberUnavailableException : Exception
{
    public MemberUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
