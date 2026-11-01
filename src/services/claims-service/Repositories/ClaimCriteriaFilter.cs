using ClaimsService.Models;
using MongoDB.Driver;

namespace ClaimsService.Repositories;

/// <summary>
/// The one Mongo filter for <see cref="ClaimCriteria"/>: Insights counts and
/// the claims list's drill-down both use it, so a figure and the list it
/// opens always agree.
/// </summary>
public static class ClaimCriteriaFilter
{
    /// <summary>Open-inventory age buckets in full days since receipt (null max: and older).</summary>
    public static readonly (int Min, int? Max)[] AgingBuckets = [(0, 15), (16, 30), (31, 45), (46, 60), (61, null)];

    public static FilterDefinition<Claim> Build(string tenantId, ClaimCriteria c)
    {
        var b = Builders<Claim>.Filter;
        var filter = b.Eq(x => x.TenantId, tenantId)
                     // Current versions only: a version superseded by an adjustment is history.
                     & b.Eq(x => x.SupersededAt, (DateTime?)null)
                     & b.Ne(x => x.VersionState, ClaimVersionState.Draft);

        if (!string.IsNullOrWhiteSpace(c.ClaimNumber))
            filter &= b.Eq(x => x.ClaimNumber, c.ClaimNumber.Trim());
        if (!string.IsNullOrWhiteSpace(c.AuthorizationNumber))
        {
            var auth = c.AuthorizationNumber.Trim();
            filter &= b.Or(
                b.Eq(x => x.PriorAuthorizationNumber, auth),
                b.ElemMatch(x => x.ClaimLines, Builders<ClaimLine>.Filter.Eq(l => l.PriorAuthorizationNumber, auth)));
        }
        if (!string.IsNullOrWhiteSpace(c.MemberId))
            filter &= b.Eq(x => x.MemberId, c.MemberId);
        if (!string.IsNullOrWhiteSpace(c.ProviderNpi))
            filter &= b.Or(b.Eq(x => x.BillingProviderNPI, c.ProviderNpi), b.Eq(x => x.RenderingProviderNPI, c.ProviderNpi));
        if (c.ServiceDateFrom.HasValue)
            filter &= b.Gte(x => x.ServiceDateFrom, c.ServiceDateFrom.Value);
        if (c.ServiceDateTo.HasValue)
            filter &= b.Lte(x => x.ServiceDateTo, c.ServiceDateTo.Value);
        if (c.Status.HasValue)
            filter &= b.Eq(x => x.Status, c.Status.Value);
        if (c.Open)
            filter &= b.In(x => x.Status, ClaimStatusSets.Open);
        if (c.Adjudicated)
            filter &= b.In(x => x.Status, ClaimStatusSets.Outcome);
        if (c.Denied)
            filter &= b.In(x => x.Status, ClaimStatusSets.Denied);
        if (c.ReceivedAfter.HasValue)
            filter &= b.Gt(x => x.SubmittedDate, c.ReceivedAfter.Value);
        if (c.ReceivedOnOrBefore.HasValue)
            filter &= b.Lte(x => x.SubmittedDate, c.ReceivedOnOrBefore.Value);
        if (c.AdjudicatedFrom.HasValue)
            filter &= b.Gte(x => x.AdjudicatedDate, c.AdjudicatedFrom.Value);
        if (c.AdjudicatedBefore.HasValue)
            filter &= b.Lt(x => x.AdjudicatedDate, c.AdjudicatedBefore.Value);
        if (!string.IsNullOrWhiteSpace(c.PendCode))
        {
            filter &= b.Eq(x => x.Status, ClaimStatus.Pended);
            filter &= c.PendCode == ClaimCriteria.UnspecifiedPendCode
                ? b.Or(b.Eq(x => x.PendDetails, null), b.Eq("PendDetails.PendCode", ""), b.Eq("PendDetails.PendCode", BsonNull()))
                : b.Eq("PendDetails.PendCode", c.PendCode);
        }
        if (!string.IsNullOrWhiteSpace(c.DenialReasonCode))
        {
            filter &= b.In(x => x.Status, ClaimStatusSets.Denied);
            filter &= c.DenialReasonCode == ClaimCriteria.UnspecifiedDenialCode
                ? b.Or(b.Eq(x => x.AdjudicationResult, null), b.Eq("AdjudicationResult.DenialReasonCode", ""), b.Eq("AdjudicationResult.DenialReasonCode", BsonNull()))
                : b.Eq("AdjudicationResult.DenialReasonCode", c.DenialReasonCode);
        }
        if (c.AutoAdjudicated is { } auto)
        {
            var noResolution = b.Or(b.Size(x => x.ExaminerResolutions, 0), b.Exists(x => x.ExaminerResolutions, false));
            filter &= auto ? noResolution : b.Not(noResolution);
        }
        if (c.LineOfBusiness.HasValue)
            filter &= b.Eq(x => x.LineOfBusiness, c.LineOfBusiness.Value);
        return filter;
    }

    private static MongoDB.Bson.BsonNull BsonNull() => MongoDB.Bson.BsonNull.Value;
}
