using System.Text.Json;
using ClaimsService.Models;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Models;

/// <summary>
/// The fingerprint of a claim's pends — what the examiner viewed (PR #1278
/// round-3 verification, M4) — changes with any pend, reason or finding, and
/// is part of the API's JSON. Follow-up 5: it does not depend on the order
/// the pends were stored in, nor on when the claim was (re-)pended, so a
/// failed approval that re-pends the same set keeps it.
/// </summary>
public class PendFingerprintTests
{
    private static PendDetails Pend() => new()
    {
        PendCode = "DUPLICATE",
        PendReason = "possible duplicate",
        PendedAt = new DateTime(2026, 4, 19, 10, 0, 0, 123, DateTimeKind.Utc),
    };

    [Fact]
    public void AnyChange_ChangesTheFingerprint()
    {
        var baseline = Pend().Fingerprint;

        var added = Pend();
        added.AdditionalPendReasons.Add("MEDREVIEW: Billing: manual review required.");
        var reason = Pend();
        reason.PendReason = "Line 1 duplicates CLM-2.";
        var finding = Pend();
        finding.DuplicateFindings.Add(new DuplicateFindingSnapshot
        {
            DuplicateType = "Exact", RuleId = "DUP-1", LineNumber = 1, MatchedClaimId = "CLM-2", MatchedLineNumber = 1,
        });
        var otherMatch = Pend();
        otherMatch.DuplicateFindings.Add(new DuplicateFindingSnapshot
        {
            DuplicateType = "Exact", RuleId = "DUP-1", LineNumber = 1, MatchedClaimId = "CLM-3", MatchedLineNumber = 1,
        });

        Assert.NotEqual(baseline, added.Fingerprint);
        Assert.NotEqual(baseline, reason.Fingerprint);
        Assert.NotEqual(baseline, finding.Fingerprint);
        Assert.NotEqual(finding.Fingerprint, otherMatch.Fingerprint);
    }

    /// <summary>
    /// Follow-up 5: a re-run that re-pends the same pends (a failed, transient
    /// approval) sets a new PendedAt and may store the reasons in another
    /// order — the routing pend can even come from another stage. Same set,
    /// same fingerprint.
    /// </summary>
    [Fact]
    public void SamePendSet_SameFingerprint_WhateverTheOrderOrPendTime()
    {
        var reviewed = new PendDetails
        {
            PendCode = "COB",
            PendReason = "cob-payer-order-mismatch",
            PendedAt = new DateTime(2026, 4, 19, 10, 0, 0, DateTimeKind.Utc),
            AdditionalPendReasons = ["NCCI: bundled pair 99214/99213", "MEDREVIEW: manual review"],
            EditFailures =
            [
                new() { EditType = "PTP", RuleId = "N1", Column1Code = "99214", Column2Code = "99213", AffectedLineNumbers = [2, 1] },
                new() { EditType = "MUE", RuleId = "M1", AffectedLineNumbers = [3], UnitsBilled = 4, MueMaxUnits = 2 },
            ],
        };
        var afterFailedApproval = new PendDetails
        {
            PendCode = "NCCI",
            PendReason = "bundled pair 99214/99213",
            PendedAt = reviewed.PendedAt.AddMinutes(7),
            AdditionalPendReasons = ["MEDREVIEW: manual review", "COB: cob-payer-order-mismatch"],
            EditFailures =
            [
                new() { EditType = "MUE", RuleId = "M1", AffectedLineNumbers = [3], UnitsBilled = 4, MueMaxUnits = 2 },
                new() { EditType = "PTP", RuleId = "N1", Column1Code = "99214", Column2Code = "99213", AffectedLineNumbers = [1, 2] },
            ],
        };

        Assert.Equal(reviewed.Fingerprint, afterFailedApproval.Fingerprint);
        Assert.StartsWith("v2-", reviewed.Fingerprint);
    }

    [Fact]
    public void NoPend_IsEmpty_AndTheApiJsonCarriesIt()
    {
        Assert.Equal(string.Empty, PendDetails.ComputeFingerprint(null));
        var json = JsonSerializer.Serialize(Pend(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains($"\"fingerprint\":\"{Pend().Fingerprint}\"", json);
    }
}
