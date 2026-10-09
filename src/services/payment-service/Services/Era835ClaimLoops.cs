using System.Text;
using PaymentService.Models;

namespace PaymentService.Services;

/// <summary>
/// The 835 2100 (claim payment) and 2110 (service payment) loops, shared by
/// <see cref="EraGeneratorService"/> and <see cref="BatchEraGeneratorService"/>
/// so both emit the segment order 005010X221A1 requires.
///
/// 2100: CLP, CAS (claim adjustments), NM1*QC (patient), NM1*82 (rendering
/// provider), MIA (institutional) or MOA (professional) remark codes, DTM*050
/// (claim received). 2110: SVC, DTM*472/473, CAS (line adjustments), LQ*HE
/// (line remark codes; a CAS carries no RARC, CAS04 is a quantity).
/// </summary>
public static class Era835ClaimLoops
{
    /// <summary>At most five claim-level RARCs fit: MOA03-MOA07, or MIA05 and MIA20-MIA23.</summary>
    public const int MaxClaimRemarkCodes = 5;

    /// <summary>The 2100 loop and its 2110 loops; <paramref name="segmentCount"/> grows by the segments emitted.</summary>
    public static string BuildClaimLoop(ClaimPayment cp, ref int segmentCount)
    {
        var sb = new StringBuilder();

        // CLP01 patient control number, CLP02 status (1 processed, 4 denied,
        // 22 reversal), CLP03 charge, CLP04 payment, CLP05 patient
        // responsibility, CLP06 filing indicator, CLP07 payer claim control number.
        Append(sb, ref segmentCount,
            $"CLP*{cp.PatientControlNumber}*{cp.ClaimStatusCode}" +
            $"*{cp.ChargeAmount:F2}*{cp.PaymentAmount:F2}*{cp.PatientResponsibilityAmount:F2}" +
            $"*HM*{cp.PayerClaimControlNumber ?? cp.ClaimId}");

        // CAS — claim adjustments, directly after CLP. Up to 6 adjustment triplets per segment.
        foreach (var casGroup in cp.ClaimAdjustments.GroupBy(a => a.GroupCode))
        {
            foreach (var chunk in casGroup.Chunk(6))
            {
                Append(sb, ref segmentCount, Cas(casGroup.Key, chunk.Select(a => (a.ReasonCode, a.Amount))));
            }
        }

        if (!string.IsNullOrEmpty(cp.MemberId))
            Append(sb, ref segmentCount, $"NM1*QC*1**{cp.MemberId}****MI*{cp.MemberId}");

        if (!string.IsNullOrEmpty(cp.RenderingProviderNPI))
            Append(sb, ref segmentCount, $"NM1*82*1*****XX*{cp.RenderingProviderNPI}");

        if (RemarkSegment(cp) is { } remarks)
            Append(sb, ref segmentCount, remarks);

        if (cp.ClaimReceivedDate.HasValue)
            Append(sb, ref segmentCount, $"DTM*050*{cp.ClaimReceivedDate.Value:yyyyMMdd}");

        foreach (var sl in cp.ServiceLines)
            sb.Append(BuildServiceLineLoop(sl, ref segmentCount));

        return sb.ToString();
    }

    /// <summary>
    /// The claim's remark codes: MIA for an institutional claim (MIA01 covered
    /// days, required, 0 when unknown; RARCs in MIA05 and MIA20-MIA23), MOA for
    /// any other (RARCs in MOA03-MOA07). Null when the claim has none.
    /// </summary>
    public static string? RemarkSegment(ClaimPayment cp)
    {
        var remarks = cp.RemarkCodes
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Esc)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxClaimRemarkCodes)
            .ToList();
        if (remarks.Count == 0)
            return null;

        if (!cp.IsInstitutional)
            return "MOA***" + string.Join("*", remarks);

        var e = new string[24];
        Array.Fill(e, string.Empty);
        e[0] = "MIA";
        e[1] = "0";
        e[5] = remarks[0];
        for (var i = 1; i < remarks.Count; i++)
            e[19 + i] = remarks[i]; // MIA20-MIA23
        return string.Join("*", e).TrimEnd('*');
    }

    private static string BuildServiceLineLoop(ServiceLinePayment sl, ref int segmentCount)
    {
        var sb = new StringBuilder();

        Append(sb, ref segmentCount, Svc(sl));

        if (sl.ServiceDateFrom.HasValue)
            Append(sb, ref segmentCount, $"DTM*472*{sl.ServiceDateFrom.Value:yyyyMMdd}");
        if (sl.ServiceDateTo.HasValue && sl.ServiceDateTo != sl.ServiceDateFrom)
            Append(sb, ref segmentCount, $"DTM*473*{sl.ServiceDateTo.Value:yyyyMMdd}");

        foreach (var casGroup in sl.Adjustments.GroupBy(a => a.GroupCode))
        {
            foreach (var chunk in casGroup.Chunk(6))
            {
                Append(sb, ref segmentCount, Cas(casGroup.Key, chunk.Select(a => (a.ReasonCode, a.Amount))));
            }
        }

        // LQ*HE — line remark codes (one per segment, each distinct code
        // once), after the line CAS: the adjustments' and the line's own.
        foreach (var rarc in sl.Adjustments
                     .Select(a => a.RemarkCode)
                     .Concat(sl.RemarkCodes)
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Select(r => Esc(r))
                     .Distinct(StringComparer.Ordinal))
        {
            Append(sb, ref segmentCount, $"LQ*HE*{rarc}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The SVC segment (005010X221A1 2110): SVC01 the adjudicated procedure —
    /// <c>HC:{HCPCS}:{modifiers}</c>, or <c>NU:{revenue code}</c> for an
    /// institutional line billed with a revenue code only; SVC02 charge;
    /// SVC03 payment; SVC04 the NUBC revenue code when SVC01 carries a
    /// HCPCS code; SVC05 units. (Previously a revenue-coded line was sent as
    /// <c>NU:{rev}:{HCPCS}</c>, which puts the HCPCS code in the modifier
    /// position, or <c>NU:{rev}:</c> with a trailing separator, and modifiers
    /// were dropped.)
    /// </summary>
    public static string Svc(ServiceLinePayment sl)
    {
        var hasProcedure = !string.IsNullOrWhiteSpace(sl.ProcedureCode);
        var revenue = NormalizeRevenueCode(sl.RevenueCode);
        var svc01 = hasProcedure || revenue is null
            ? string.Join(":", new[] { "HC", sl.ProcedureCode }
                .Concat(sl.Modifiers.Where(m => !string.IsNullOrWhiteSpace(m)).Take(4).Select(Esc)))
            : $"NU:{revenue}";
        var svc04 = hasProcedure && revenue is not null ? revenue : string.Empty;
        return $"SVC*{svc01}*{sl.ChargeAmount:F2}*{sl.PaymentAmount:F2}*{svc04}*{sl.Units:G}";
    }

    /// <summary>
    /// NUBC revenue codes are four characters; claims-service accepts the
    /// three-character form ("120"), which is the same code ("0120").
    /// </summary>
    private static string? NormalizeRevenueCode(string? revenueCode)
        => string.IsNullOrWhiteSpace(revenueCode) ? null : Esc(revenueCode.Trim()).PadLeft(4, '0');

    /// <summary>
    /// One CAS segment. Each adjustment is a reason/amount/quantity triplet
    /// (CAS02-04, 05-07, ... 17-19): no quantity is sent, so repetitions are
    /// separated by an empty quantity element, e.g. CAS*CO*45*100.00**253*50.00.
    /// </summary>
    public static string Cas(string groupCode, IEnumerable<(string ReasonCode, decimal Amount)> adjustments) =>
        $"CAS*{groupCode}*" + string.Join("**", adjustments.Select(a => $"{a.ReasonCode}*{a.Amount:F2}"));

    private static void Append(StringBuilder sb, ref int segmentCount, string segment)
    {
        segmentCount++;
        sb.Append(segment).Append('~');
    }

    private static string Esc(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Replace("*", " ").Replace("~", " ").Replace(":", " ").Replace("\\", " ").Replace("^", " ");
    }
}
