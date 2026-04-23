using AppealsService.Models;

namespace AppealsService.Repositories;

/// <summary>
/// Computes <see cref="AppealsSummary"/> from a flat list of appeals.
/// Extracted so the Cosmos and Mongo repositories share the aggregation
/// logic verbatim. Preserves the portal's observed 4-bucket wire shape
/// (<c>OpenAppeals</c>, <c>UrgentExpedited</c>, <c>DueThisWeek</c>,
/// <c>OverturnedRate</c>) across the status-enum consolidation by
/// recomputing over <c>Status + ClosureReasonCode</c>.
/// </summary>
internal static class SummaryBuilder
{
    public static AppealsSummary Build(IReadOnlyList<Appeal> appeals)
    {
        var now = DateTime.UtcNow;
        var weekFromNow = now.AddDays(7);

        var summary = new AppealsSummary
        {
            TotalAppeals = appeals.Count,
            InReview = appeals.Count(a => a.Status == AppealStatus.InReview),
            Approved = appeals.Count(a => a.Status == AppealStatus.Closed
                                       && a.ClosureReasonCode == AppealClosureReasonCode.Approved),
            Denied = appeals.Count(a => a.Status == AppealStatus.Closed
                                     && a.ClosureReasonCode == AppealClosureReasonCode.Denied),
            PartialApprovals = appeals.Count(a => a.Status == AppealStatus.Closed
                                               && a.ClosureReasonCode == AppealClosureReasonCode.PartialApproval),
            Withdrawn = appeals.Count(a => a.Status == AppealStatus.Closed
                                        && a.ClosureReasonCode == AppealClosureReasonCode.Withdrawn),
            TotalAppealedAmount = appeals.Sum(a => a.AppealedAmount),
            TotalApprovedAmount = appeals
                .Where(a => a.Decision != null && a.Decision.ApprovedAmount.HasValue)
                .Sum(a => a.Decision!.ApprovedAmount!.Value),
            OpenAppeals = appeals.Count(a => a.Status == AppealStatus.Submitted
                                           || a.Status == AppealStatus.InReview
                                           || a.Status == AppealStatus.PendingInfo),
            UrgentExpedited = appeals.Count(a => a.IsUrgent
                                              && (a.Status == AppealStatus.Submitted
                                                  || a.Status == AppealStatus.InReview
                                                  || a.Status == AppealStatus.PendingInfo)),
            DueThisWeek = appeals.Count(a => (a.Status == AppealStatus.Submitted
                                               || a.Status == AppealStatus.InReview
                                               || a.Status == AppealStatus.PendingInfo)
                                           && a.TargetResponseDate.HasValue
                                           && a.TargetResponseDate.Value <= weekFromNow)
        };

        // Decision time: for closed appeals with a DecisionDate, from SubmittedDate.
        var decidedAppeals = appeals.Where(a => a.DecisionDate.HasValue && a.Status == AppealStatus.Closed).ToList();
        if (decidedAppeals.Count > 0)
        {
            summary.AverageDecisionTimeDays = decidedAppeals
                .Average(a => (a.DecisionDate!.Value - a.SubmittedDate).TotalDays);
        }

        // ApprovalRate: fraction of closed-with-decision that were Approved or PartialApproval.
        var closedWithDecision = appeals.Count(a => a.Status == AppealStatus.Closed
                                                  && a.ClosureReasonCode.HasValue
                                                  && (a.ClosureReasonCode == AppealClosureReasonCode.Approved
                                                      || a.ClosureReasonCode == AppealClosureReasonCode.Denied
                                                      || a.ClosureReasonCode == AppealClosureReasonCode.PartialApproval));
        if (closedWithDecision > 0)
        {
            summary.ApprovalRate = (double)(summary.Approved + summary.PartialApprovals) / closedWithDecision * 100;
        }

        // OverturnedRate (portal-observed): fraction of all closed appeals whose
        // reason code is Approved or PartialApproval. Computed over ALL closed
        // appeals (including Withdrawn, AdminError, etc.) because portal's
        // definition is "fraction of terminated appeals whose original denial
        // was overturned" — denominator is every closed appeal.
        var totalClosed = appeals.Count(a => a.Status == AppealStatus.Closed);
        if (totalClosed > 0)
        {
            summary.OverturnedRate = (double)(summary.Approved + summary.PartialApprovals) / totalClosed * 100;
        }

        foreach (var a in appeals)
        {
            summary.AppealsByStatus.TryGetValue(a.Status, out var s);
            summary.AppealsByStatus[a.Status] = s + 1;
            summary.AppealsByLevel.TryGetValue(a.AppealLevel, out var l);
            summary.AppealsByLevel[a.AppealLevel] = l + 1;
        }

        return summary;
    }
}
