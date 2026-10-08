using ClaimsService.Models;
using ClaimsService.Services.Adjudication.Stages;

namespace ClaimsService.Services;

/// <summary>
/// Projects the duplicate-claim stage's persisted findings
/// (<see cref="PendDetails.DuplicateFindings"/>) into one
/// <see cref="ClaimDuplicateMatch"/> per matched prior claim for the
/// claim-detail "Possible duplicates" panel.
///
/// <para>
/// The stage records which prior claim/line each flagged line matched but not
/// a structured list of matching fields, so those are recomputed here by
/// comparing the two lines with the stage's normalization (codes and
/// modifiers case-insensitive, modifiers order-insensitive, line service
/// dates falling back to the claim header). When the matched claim or line
/// can no longer be read, the fields implied by the match type are reported
/// instead: everything for an exact match, member + service dates + code for
/// a suspect match.
/// </para>
/// </summary>
public static class DuplicateMatchBuilder
{
    /// <summary>Upper bound on distinct matched claims read per request.</summary>
    public const int MaxMatchedClaims = 25;

    public const string FieldMember = "Member";
    public const string FieldBillingProvider = "Billing provider";
    public const string FieldServiceDates = "Service dates";
    public const string FieldProcedureCode = "Procedure code";
    public const string FieldRevenueCode = "Revenue code";
    public const string FieldModifiers = "Modifiers";
    public const string FieldUnits = "Units";
    public const string FieldCharge = "Charge";

    private static readonly string[] FieldOrder =
    {
        FieldMember, FieldBillingProvider, FieldServiceDates, FieldProcedureCode,
        FieldRevenueCode, FieldModifiers, FieldUnits, FieldCharge,
    };

    /// <summary>Distinct matched claim ids recorded on the claim, capped at <see cref="MaxMatchedClaims"/>.</summary>
    public static IReadOnlyList<string> MatchedClaimIds(Claim claim) =>
        (claim.PendDetails?.DuplicateFindings ?? new List<DuplicateFindingSnapshot>())
            .Select(f => f.MatchedClaimId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxMatchedClaims)
            .ToList();

    public static List<ClaimDuplicateMatch> Build(
        Claim claim,
        IReadOnlyDictionary<string, Claim> matchedClaims)
    {
        var findings = claim.PendDetails?.DuplicateFindings;
        if (findings is not { Count: > 0 })
        {
            return new List<ClaimDuplicateMatch>();
        }

        var allowedIds = MatchedClaimIds(claim).ToHashSet(StringComparer.Ordinal);

        return findings
            .Where(f => !string.IsNullOrWhiteSpace(f.MatchedClaimId) && allowedIds.Contains(f.MatchedClaimId!))
            .GroupBy(f => f.MatchedClaimId!, StringComparer.Ordinal)
            .Select(group =>
            {
                matchedClaims.TryGetValue(group.Key, out var prior);
                var lines = group
                    .OrderBy(f => f.LineNumber)
                    .Select(f => BuildLine(claim, prior, f))
                    .ToList();

                var common = lines
                    .Select(l => (IEnumerable<string>)l.MatchedFields)
                    .Aggregate((a, b) => a.Intersect(b));

                return new ClaimDuplicateMatch
                {
                    MatchedClaimId = group.Key,
                    MatchedClaimNumber = prior?.ClaimNumber is { Length: > 0 } number
                        ? number
                        : group.Select(f => f.MatchedClaimNumber).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    MatchType = lines.Any(l => IsExact(l.MatchType))
                        ? DuplicateClaimStage.ExactDuplicateType
                        : DuplicateClaimStage.SuspectDuplicateType,
                    MatchedClaimFound = prior is not null,
                    ServiceDateFrom = prior?.ServiceDateFrom,
                    ServiceDateTo = prior?.ServiceDateTo,
                    BilledAmount = prior?.TotalChargeAmount,
                    Status = prior?.Status.ToString(),
                    MatchedFields = Ordered(common),
                    Lines = lines,
                };
            })
            // Exact matches first, then by claim number for a stable order.
            .OrderBy(m => IsExact(m.MatchType) ? 0 : 1)
            .ThenBy(m => m.MatchedClaimNumber ?? m.MatchedClaimId, StringComparer.Ordinal)
            .ToList();
    }

    private static ClaimDuplicateLineMatch BuildLine(Claim claim, Claim? prior, DuplicateFindingSnapshot finding)
    {
        var line = claim.ClaimLines.FirstOrDefault(l => l.LineNumber == finding.LineNumber);
        var priorLine = prior?.ClaimLines.FirstOrDefault(l => l.LineNumber == finding.MatchedLineNumber);

        var fields = line is not null && prior is not null && priorLine is not null
            ? CompareLines(claim, line, prior, priorLine)
            : ImpliedFields(finding.DuplicateType);

        return new ClaimDuplicateLineMatch
        {
            LineNumber = finding.LineNumber,
            MatchedLineNumber = finding.MatchedLineNumber,
            MatchType = finding.DuplicateType,
            RuleId = finding.RuleId,
            MatchedFields = fields,
            Message = finding.Message,
        };
    }

    internal static List<string> CompareLines(Claim claim, ClaimLine line, Claim prior, ClaimLine priorLine)
    {
        var fields = new List<string>();
        if (Normalize(claim.MemberId).Length > 0 && Normalize(claim.MemberId) == Normalize(prior.MemberId))
            fields.Add(FieldMember);
        if (Normalize(claim.BillingProviderNPI).Length > 0
            && Normalize(claim.BillingProviderNPI) == Normalize(prior.BillingProviderNPI))
            fields.Add(FieldBillingProvider);

        var (from, to) = LineDates(line, claim);
        var (priorFrom, priorTo) = LineDates(priorLine, prior);
        if (from != default && from == priorFrom && to == priorTo)
            fields.Add(FieldServiceDates);

        if (Normalize(line.ProcedureCode).Length > 0
            && Normalize(line.ProcedureCode) == Normalize(priorLine.ProcedureCode))
            fields.Add(FieldProcedureCode);
        if (Normalize(line.RevenueCode).Length > 0
            && Normalize(line.RevenueCode) == Normalize(priorLine.RevenueCode))
            fields.Add(FieldRevenueCode);
        if (Modifiers(line).SequenceEqual(Modifiers(priorLine)))
            fields.Add(FieldModifiers);
        if (line.Units == priorLine.Units)
            fields.Add(FieldUnits);
        if (line.ChargeAmount == priorLine.ChargeAmount)
            fields.Add(FieldCharge);
        return fields;
    }

    private static List<string> ImpliedFields(string duplicateType) => IsExact(duplicateType)
        ? FieldOrder.ToList()
        : new List<string> { FieldMember, FieldServiceDates, FieldProcedureCode };

    private static bool IsExact(string? type) =>
        string.Equals(type, DuplicateClaimStage.ExactDuplicateType, StringComparison.OrdinalIgnoreCase);

    private static List<string> Ordered(IEnumerable<string> fields)
    {
        var set = fields.ToHashSet(StringComparer.Ordinal);
        return FieldOrder.Where(set.Contains).ToList();
    }

    private static (DateTime From, DateTime To) LineDates(ClaimLine line, Claim claim)
    {
        var from = (line.ServiceDateFrom == default ? claim.ServiceDateFrom : line.ServiceDateFrom).Date;
        var to = (line.ServiceDateTo == default
            ? (line.ServiceDateFrom == default ? claim.ServiceDateTo : line.ServiceDateFrom)
            : line.ServiceDateTo).Date;
        if (to < from) to = from;
        return (from, to);
    }

    private static List<string> Modifiers(ClaimLine line) =>
        (line.Modifiers ?? new List<string>())
            .Select(Normalize)
            .Where(m => m.Length > 0)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
}
