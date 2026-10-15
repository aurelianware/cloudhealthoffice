namespace CloudHealthOffice.Portal.Services;

/// <summary>Where a claim line stands, as a reviewer reads it.</summary>
public enum LineOutcome
{
    /// <summary>Not adjudicated yet (the claim is still in process or held before benefits).</summary>
    NotAdjudicated,
    /// <summary>An edit or a duplicate finding on this line is holding the claim for review.</summary>
    Pend,
    /// <summary>Allowed: the plan pays, or the member's cost share absorbs it (deductible, copay).</summary>
    Pay,
    /// <summary>Nothing allowed on a billed charge, or the whole claim was denied.</summary>
    Deny,
}

/// <summary>One reason on a line: an 835 adjustment with plain-language text.</summary>
public sealed record LineReason(string Code, string Text, decimal? Amount, bool IsDenial);

/// <summary>An edit or duplicate finding that hit this line.</summary>
public sealed record LineEdit(string Type, string Text, string? RuleId, IReadOnlyList<int> OtherLines, bool ModifierPresent);

/// <summary>A line's outcome, the label shown for it, and why.</summary>
public sealed record LineReviewView(
    LineOutcome Outcome,
    string Label,
    IReadOnlyList<LineReason> Reasons,
    IReadOnlyList<LineEdit> Edits)
{
    /// <summary>One line of "why" for the collapsed row: the edit, the denial reason, or the member cost share.</summary>
    public string? Summary =>
        Edits.Count > 0 ? Edits[0].Text
        : Reasons.FirstOrDefault(r => r.IsDenial)?.Text
          ?? Reasons.FirstOrDefault(r => r.Code.StartsWith("PR-", StringComparison.Ordinal))?.Text;
}

/// <summary>
/// Derives each claim line's Pay / Deny / Pend outcome and its reasons from
/// what claims-service already returns: the line's adjudication result and
/// 835 adjustments, and the claim's pend details (edit failures name the
/// lines they hit). Display only: it never changes a claim or line.
/// </summary>
public static class ClaimLineReview
{
    public static LineReviewView Evaluate(ClaimDetails claim, ClaimServiceLine line)
    {
        var edits = EditsFor(claim, line.LineNumber);
        var reasons = line.Adjustments
            .Select(a => new LineReason(
                CarcText.Code(a.GroupCode, a.ReasonCode),
                string.IsNullOrWhiteSpace(a.Description) ? CarcText.Describe(a.GroupCode, a.ReasonCode, a.RemarkCode) : a.Description,
                a.Amount,
                CarcText.IsDenial(a.GroupCode, a.ReasonCode)))
            .ToList();

        var claimPended = IsStatus(claim.Status, "Pended", "Pend");
        var claimDenied = IsStatus(claim.Status, "Denied", "Deny");

        if (claimPended && edits.Count > 0)
            return new(LineOutcome.Pend, "Pend", reasons, edits);

        if (!line.IsAdjudicated)
        {
            if (claimDenied)
            {
                var why = string.IsNullOrWhiteSpace(claim.DenialReason) ? "The claim was denied" : claim.DenialReason!;
                return new(LineOutcome.Deny, "Deny", reasons.Append(new LineReason("Claim", why, null, true)).ToList(), edits);
            }
            return new(LineOutcome.NotAdjudicated, claimPended ? "On hold" : "Not adjudicated", reasons, edits);
        }

        if (line.IsDenied || (line.PaidAmount == 0 && line.PatientResponsibility == 0 && reasons.Any(r => r.IsDenial)))
            return new(LineOutcome.Deny, "Deny", reasons, edits);

        var label = line.PaidAmount > 0 ? "Pay" : "Pay – member cost share";
        return new(LineOutcome.Pay, label, reasons, edits);
    }

    private static List<LineEdit> EditsFor(ClaimDetails claim, int lineNumber)
    {
        var pend = claim.PendDetails;
        if (pend is null) return new();

        var edits = pend.EditFailures
            .Where(e => e.AffectedLineNumbers.Contains(lineNumber))
            .Select(e => new LineEdit(
                e.EditType,
                EditText(e),
                e.RuleId,
                e.AffectedLineNumbers.Where(n => n != lineNumber).Distinct().OrderBy(n => n).ToList(),
                e.ModifierOverridePresent))
            .ToList();

        edits.AddRange(pend.DuplicateFindings
            .Where(d => d.LineNumber == lineNumber)
            .Select(d => new LineEdit(
                "Duplicate",
                !string.IsNullOrWhiteSpace(d.Message)
                    ? d.Message!
                    : $"Possible duplicate of claim {d.MatchedClaimNumber ?? d.MatchedClaimId}"
                      + (d.MatchedLineNumber is { } l ? $", line {l}" : ""),
                d.RuleId,
                Array.Empty<int>(),
                false)));
        return edits;
    }

    private static string EditText(ClaimPendEditFailure e)
    {
        if (!string.IsNullOrWhiteSpace(e.Message)) return e.Message!;
        return e.EditType.ToUpperInvariant() switch
        {
            var t when t.Contains("PTP") || t.Contains("NCCI") =>
                $"NCCI: {e.Column2Code} is not separately payable with {e.Column1Code}",
            var t when t.Contains("MUE") =>
                $"MUE: units on {e.Column1Code ?? "this code"} exceed the per-day limit",
            _ => $"{e.EditType} edit {e.RuleId}".Trim(),
        };
    }

    private static bool IsStatus(string? status, params string[] names) =>
        status is not null && names.Any(n => string.Equals(status, n, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Plain-language readings of the claim adjustment reason codes (CARC) a
/// reviewer meets most, written for the screen, not the official X12 text.
/// Unknown codes fall back to the code itself.
/// </summary>
public static class CarcText
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1"] = "Applied to the member's deductible",
        ["2"] = "Member coinsurance",
        ["3"] = "Member copay",
        ["4"] = "Procedure code doesn't match the modifier billed",
        ["5"] = "Procedure code doesn't match the place of service",
        ["11"] = "Diagnosis doesn't support the procedure",
        ["16"] = "Claim is missing information needed to process it",
        ["18"] = "Exact duplicate of a claim or line already processed",
        ["22"] = "Another payer may be primary (coordination of benefits)",
        ["23"] = "Reduced by what another payer already paid",
        ["24"] = "Covered under a capitation or managed-care agreement",
        ["26"] = "Service was before coverage started",
        ["27"] = "Service was after coverage ended",
        ["29"] = "Filed after the timely filing limit",
        ["31"] = "Patient can't be identified as our member",
        ["45"] = "Billed amount is above the contracted or fee-schedule rate",
        ["49"] = "Routine or preventive service not covered by this plan",
        ["50"] = "Not medically necessary per the plan's criteria",
        ["96"] = "Not a covered service",
        ["97"] = "Included in the payment for another service on this claim",
        ["119"] = "Benefit maximum for this period has been reached",
        ["151"] = "Units or frequency exceed what's supported for this service",
        ["167"] = "Diagnosis isn't covered",
        ["170"] = "Not payable to this type of provider",
        ["185"] = "Rendering provider isn't eligible to perform this service",
        ["197"] = "Prior authorization was required and wasn't obtained",
        ["198"] = "Exceeds the authorized amount or visits",
        ["204"] = "Not covered under the member's current benefit plan",
        ["226"] = "Information requested from the provider wasn't received",
        ["234"] = "Procedure isn't paid separately",
        ["236"] = "Not compatible with another procedure on the same day (NCCI)",
        ["252"] = "Additional documentation is needed to process this",
        ["253"] = "Sequestration reduction",
    };

    /// <summary>Group-code prefixes that mean a reduction the provider can't bill the member for.</summary>
    private static readonly HashSet<string> DenialCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "4", "5", "11", "16", "18", "22", "26", "27", "29", "31", "49", "50", "96", "97", "119", "151",
        "167", "170", "185", "197", "198", "204", "226", "234", "236", "252",
    };

    public static string Code(string group, string reason) =>
        string.IsNullOrWhiteSpace(group) ? reason : $"{group}-{reason}";

    public static string Describe(string group, string reason, string? remark = null)
    {
        var text = Reasons.TryGetValue(reason ?? string.Empty, out var t) ? t : $"Adjustment reason {reason}";
        return string.IsNullOrWhiteSpace(remark) ? text : $"{text} (remark {remark})";
    }

    /// <summary>
    /// True for adjustments that deny rather than reduce: contractual or payer
    /// denials such as CO-97 or CO-50, never patient responsibility or the
    /// contractual write-down (CO-45).
    /// </summary>
    public static bool IsDenial(string group, string reason) =>
        !string.Equals(group, "PR", StringComparison.OrdinalIgnoreCase) && DenialCodes.Contains(reason ?? string.Empty);
}
