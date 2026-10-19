using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Data;

namespace AuthorizationService.Backends;

public sealed class AuthorizationTransitionException(AuthorizationStatus from, AuthorizationStatus to)
    : ConcurrencyConflictException($"Authorization transition from {from} to {to} is not permitted.");

/// <summary>Ordinary review lifecycle. Reopening/adverse-decision overturns require a separate workflow.</summary>
public static class AuthorizationLifecyclePolicy
{
    public static void Validate(Authorization authorization, AuthorizationStatus target, DateTime? nowUtc = null)
    {
        var current = authorization.Status;
        var allowed = Enum.IsDefined(current) && Enum.IsDefined(target) && (current == target || current switch
        {
            AuthorizationStatus.Submitted => target is AuthorizationStatus.InReview or AuthorizationStatus.Pended
                or AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied or AuthorizationStatus.Cancelled,
            AuthorizationStatus.InReview => target is AuthorizationStatus.Pended or AuthorizationStatus.Approved
                or AuthorizationStatus.Modified or AuthorizationStatus.Denied or AuthorizationStatus.Cancelled,
            AuthorizationStatus.Pended => target is AuthorizationStatus.InReview or AuthorizationStatus.Approved
                or AuthorizationStatus.Modified or AuthorizationStatus.Denied or AuthorizationStatus.Cancelled,
            AuthorizationStatus.Approved or AuthorizationStatus.Modified => target is AuthorizationStatus.Cancelled or AuthorizationStatus.Expired,
            _ => false,
        });
        if (!allowed || (target == AuthorizationStatus.Expired && current != target
            && !IsExpired(authorization, nowUtc ?? DateTime.UtcNow)))
            throw new AuthorizationTransitionException(current, target);
    }

    public static AuthorizationStatus ClinicalDecisionStatus(Authorization auth)
    {
        static bool IsClinical(AuthorizationStatus status) => status is AuthorizationStatus.Approved or AuthorizationStatus.Modified or AuthorizationStatus.Denied;
        if (IsClinical(auth.Status) || auth.Status is not (AuthorizationStatus.Expired or AuthorizationStatus.Cancelled))
            return auth.Status;
        var recorded = (auth.StatusHistory ?? new()).Where(h => IsClinical(h.Status))
            .MaxBy(h => h.ChangedAt);
        if (recorded is not null) return recorded.Status;
        return auth.ReviewDecision switch
        {
            "A1" => AuthorizationStatus.Approved,
            "A2" => AuthorizationStatus.Modified,
            "A3" when auth.Status == AuthorizationStatus.Cancelled => AuthorizationStatus.Denied,
            _ => auth.Status,
        };
    }

    /// <summary>Approved end and expiration dates are inclusive UTC calendar dates.</summary>
    public static bool IsExpired(Authorization authorization, DateTime nowUtc) =>
        authorization.Status is AuthorizationStatus.Approved or AuthorizationStatus.Modified &&
        ((authorization.ApprovedServiceDateTo.HasValue && authorization.ApprovedServiceDateTo.Value.Date < nowUtc.Date)
         || (authorization.ExpirationDate.HasValue && authorization.ExpirationDate.Value.Date < nowUtc.Date));
}
