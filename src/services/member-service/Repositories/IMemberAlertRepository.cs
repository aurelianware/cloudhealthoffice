using MemberService.Models;

namespace MemberService.Repositories;

public interface IMemberAlertRepository
{
    Task<MemberAlert> CreateAsync(MemberAlert alert);
    Task<MemberAlert?> GetByIdAsync(string tenantId, string memberId, string alertId);

    /// <summary>
    /// List alerts for a member. When <paramref name="activeOnly"/> is true, only
    /// alerts whose effective window contains <paramref name="asOf"/> (default = now)
    /// are returned.
    /// </summary>
    Task<IReadOnlyList<MemberAlert>> ListByMemberAsync(
        string tenantId,
        string memberId,
        bool activeOnly,
        DateTime? asOf = null);

    /// <summary>Persist an end-dated alert. Caller must set <see cref="MemberAlert.EndDate"/> and <see cref="MemberAlert.EndedBy"/>.</summary>
    Task<MemberAlert> EndAsync(MemberAlert alert);
}
