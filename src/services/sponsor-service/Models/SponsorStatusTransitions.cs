using System.Collections.Generic;
using System.Linq;

namespace SponsorService.Models;

/// <summary>
/// The status changes the status endpoint
/// (<c>PUT /api/v1/sponsors/{groupNumber}/status</c>) may make. It exists for
/// premium delinquency: Finance suspends a sponsor that has not paid and
/// reinstates it once it has. Nothing else:
/// <list type="bullet">
///   <item>Active → Suspended (delinquency) and Suspended → Active (reinstatement).</item>
///   <item>Setting the status a sponsor already has is accepted and changes nothing,
///   so a retried suspension succeeds.</item>
///   <item>Terminated is terminal here. Termination (which also sets a termination
///   date) stays with <c>DELETE /sponsors/{group}</c>, and activating a
///   PendingActivation sponsor with the full <c>PUT /sponsors/{group}</c>; both
///   need enrollment:process.</item>
/// </list>
/// </summary>
public static class SponsorStatusTransitions
{
    private static readonly IReadOnlyDictionary<SponsorStatus, SponsorStatus[]> Allowed =
        new Dictionary<SponsorStatus, SponsorStatus[]>
        {
            [SponsorStatus.Active] = [SponsorStatus.Suspended],
            [SponsorStatus.Suspended] = [SponsorStatus.Active],
            [SponsorStatus.PendingActivation] = [],
            [SponsorStatus.Terminated] = [],
        };

    /// <summary>The statuses the endpoint may move a sponsor to from <paramref name="from"/>.</summary>
    public static IReadOnlyList<SponsorStatus> AllowedFrom(SponsorStatus from)
        => Allowed.TryGetValue(from, out var to) ? to : [];

    /// <summary>Whether the endpoint may move a sponsor from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsAllowed(SponsorStatus from, SponsorStatus to) => AllowedFrom(from).Contains(to);
}
