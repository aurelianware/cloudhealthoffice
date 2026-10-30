using System.Globalization;
using System.Text;
using ClaimsService.Models;

namespace ClaimsService.EDI.ClaimStatusInquiry;

/// <summary>What CHO found for one 276 inquiry.</summary>
public sealed record ClaimStatusAnswer(ClaimStatusInquiryItem Inquiry, IReadOnlyList<Claim> Claims, bool Unavailable = false);

/// <summary>
/// Writes an X12 005010X212 277 (Health Care Claim Status Response) for a
/// 276: the payer (2000A), the information receiver (2000B), one service
/// provider loop (2000C) per provider asked about, and per inquiry a
/// subscriber loop (2000D, plus 2000E for a dependent) with one 2200 claim
/// status loop per matching claim, each echoing the inquiry's TRN.
///
/// <para>STC01 is <c>{category}:{status}</c> from the X12 Claim Status
/// Category and Claim Status code lists: a claim CHO does not hold is
/// <c>A4:35</c> (not found); an inquiry CHO could not look up in time is
/// <c>E1:0</c> (response not possible, system status).</para>
/// </summary>
public static class Edi277ClaimStatusGenerator
{
    public const string ImplementationReference = "005010X212";

    public const string NotFound = "A4:35";
    public const string UnableToRespond = "E1:0";

    /// <summary>
    /// STC01 for a CHO claim status. Approved (Pay) and Deny are adjudicated
    /// but not final until the payment run's 835, so they report "adjudicated,
    /// awaiting payment cycle" (3) under Pending; Paid and Denied went out on
    /// an 835 and report Finalized/Payment and Finalized/Denial.
    /// </summary>
    public static string Stc01(ClaimStatus status) => status switch
    {
        ClaimStatus.Submitted or ClaimStatus.Received => "A2:20",
        ClaimStatus.InAdjudication => "P1:20",
        ClaimStatus.Pended => "P2:20",
        ClaimStatus.Approved or ClaimStatus.Deny => "P1:3",
        ClaimStatus.Paid or ClaimStatus.PartiallyPaid => "F1:65",
        ClaimStatus.Denied => "F2:1",
        ClaimStatus.Voided => "F0:1",
        _ => "A2:20",
    };

    public static string Generate(Edi276ParseResult request, IReadOnlyList<ClaimStatusAnswer> answers, long controlNumber, DateTime now)
    {
        var control = (controlNumber % 1_000_000_000).ToString("D9", CultureInfo.InvariantCulture);
        var group = (controlNumber % 1_000_000_000).ToString(CultureInfo.InvariantCulture);
        var date8 = D8(now);
        var time = now.ToString("HHmm", CultureInfo.InvariantCulture);

        var payerId = Clean(request.PayerId, Clean(request.InterchangeReceiverId, "CHO"));
        var body = new List<string>
        {
            $"ST*277*0001*{ImplementationReference}",
            $"BHT*0010*08*{Clean(request.ReferenceId, control)}*{date8}*{time}*DG",
        };

        var hl = 0;
        var hlA = ++hl;
        body.Add($"HL*{hlA}**20*1");
        body.Add($"NM1*PR*2*{Clean(request.PayerName, "HEALTH PLAN")}*****PI*{payerId}");
        var hlB = ++hl;
        body.Add($"HL*{hlB}*{hlA}*21*1");
        body.Add($"NM1*41*2*{Clean(request.ReceiverName, "SUBMITTER")}*****46*{Clean(request.ReceiverId, Clean(request.InterchangeSenderId, "SUBMITTER"))}");

        foreach (var provider in answers.GroupBy(a => a.Inquiry.ProviderNpi ?? string.Empty))
        {
            var hlC = ++hl;
            body.Add($"HL*{hlC}*{hlB}*19*1");
            var first = provider.First().Inquiry;
            body.Add($"NM1*1P*2*{Clean(first.ProviderName, Clean(provider.Key, "PROVIDER"))}*****XX*{Clean(provider.Key, "UNKNOWN")}");

            var answersForProvider = provider.ToList();
            for (var i = 0; i < answersForProvider.Count; i++)
            {
                var answer = answersForProvider[i];
                var q = answer.Inquiry;
                var hlD = ++hl;
                body.Add($"HL*{hlD}*{hlC}*22*{(q.IsDependent ? "1" : "0")}");
                body.Add($"NM1*IL*1*{Clean(q.SubscriberLastName, "")}*{Clean(q.SubscriberFirstName, "")}****MI*{Clean(q.SubscriberId, "UNKNOWN")}");
                if (q.IsDependent)
                {
                    var hlE = ++hl;
                    body.Add($"HL*{hlE}*{hlD}*23*0");
                    body.Add($"NM1*QC*1*{Clean(q.DependentLastName, "")}*{Clean(q.DependentFirstName, "")}");
                }
                AddClaimLoops(body, answer, date8);
            }
        }

        body.Add($"SE*{body.Count + 1}*0001");

        var sender = Clean(request.InterchangeReceiverId, "CHO");
        var receiver = Clean(request.InterchangeSenderId, "SUBMITTER");
        var sb = new StringBuilder();
        sb.Append($"ISA*00*          *00*          *ZZ*{Pad(sender)}*ZZ*{Pad(receiver)}*{now.ToString("yyMMdd", CultureInfo.InvariantCulture)}*{time}*^*00501*{control}*0*{request.UsageIndicator}*:~");
        sb.Append($"GS*HN*{Clean(request.ApplicationReceiverId, sender)}*{Clean(request.ApplicationSenderId, receiver)}*{date8}*{time}*{group}*X*{ImplementationReference}~");
        foreach (var segment in body)
            sb.Append(Trim(segment)).Append('~');
        sb.Append($"GE*1*{group}~IEA*1*{control}~");
        return sb.ToString();
    }

    private static void AddClaimLoops(List<string> body, ClaimStatusAnswer answer, string today)
    {
        var q = answer.Inquiry;
        var trace = Clean(q.TraceNumber, "0");

        if (answer.Unavailable || answer.Claims.Count == 0)
        {
            body.Add($"TRN*2*{trace}");
            body.Add($"STC*{(answer.Unavailable ? UnableToRespond : NotFound)}*{today}**{Amount(q.TotalChargeAmount ?? 0m)}*0");
            if (q.PayerClaimControlNumber is not null) body.Add($"REF*1K*{Clean(q.PayerClaimControlNumber, "")}");
            if (q.PatientControlNumber is not null) body.Add($"REF*EJ*{Clean(q.PatientControlNumber, "")}");
            if (q.ServiceDateFrom is { } from) body.Add($"DTP*472*RD8*{D8(from)}-{D8(q.ServiceDateTo ?? from)}");
            return;
        }

        foreach (var claim in answer.Claims)
        {
            var finalized = claim.Status is ClaimStatus.Paid or ClaimStatus.PartiallyPaid or ClaimStatus.Denied;
            var paid = finalized ? claim.AdjudicationResult?.PayerPayment ?? 0m : 0m;
            var statusDate = D8(claim.AdjudicatedDate ?? claim.ReceivedDate ?? claim.SubmittedDate);
            // STC06: the adjudication (finalized) date, for finalized claims only.
            var stc06 = finalized && claim.AdjudicatedDate is { } adjudicated ? D8(adjudicated) : "";
            var stc08 = claim.PaidDate is { } paidDate && finalized ? D8(paidDate) : "";
            body.Add($"TRN*2*{trace}");
            body.Add($"STC*{Stc01(claim.Status)}*{statusDate}**{Amount(claim.TotalChargeAmount)}*{Amount(paid)}*{stc06}**{stc08}");
            body.Add($"REF*1K*{Clean(claim.Id, "")}");
            if (!string.IsNullOrEmpty(claim.ClaimNumber)) body.Add($"REF*EJ*{Clean(claim.ClaimNumber, "")}");
            body.Add($"DTP*472*RD8*{D8(claim.ServiceDateFrom)}-{D8(claim.ServiceDateTo == default ? claim.ServiceDateFrom : claim.ServiceDateTo)}");
        }
    }

    private static string D8(DateTime d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Amount(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Pad(string id) => id.Length >= 15 ? id[..15] : id.PadRight(15);

    // Drops trailing empty elements so no segment ends in '*'.
    private static string Trim(string segment) => segment.TrimEnd('*');

    private static string Clean(string? value, string fallback)
    {
        var v = new string((value ?? string.Empty).Trim().Where(c => c is not ('*' or '~' or ':' or '^')).ToArray());
        return v.Length == 0 ? fallback : v;
    }
}
