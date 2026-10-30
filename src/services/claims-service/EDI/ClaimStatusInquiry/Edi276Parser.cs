using System.Globalization;

namespace ClaimsService.EDI.ClaimStatusInquiry;

/// <summary>One claim a 276 asks about (a 2200D or 2200E loop).</summary>
public sealed class ClaimStatusInquiryItem
{
    /// <summary>TRN02 of the 2200 loop: echoed on the 277 so the submitter can match it.</summary>
    public string TraceNumber { get; set; } = string.Empty;

    /// <summary>NM109 of the 2100D subscriber (MI).</summary>
    public string SubscriberId { get; set; } = string.Empty;
    public string? SubscriberLastName { get; set; }
    public string? SubscriberFirstName { get; set; }

    /// <summary>Set when the patient is a dependent (2000E / 2100E).</summary>
    public string? DependentLastName { get; set; }
    public string? DependentFirstName { get; set; }
    public bool IsDependent { get; set; }

    /// <summary>REF*1K: the payer claim control number (CHO's claim id).</summary>
    public string? PayerClaimControlNumber { get; set; }

    /// <summary>REF*EJ: the patient control number the provider sent in CLM01.</summary>
    public string? PatientControlNumber { get; set; }

    /// <summary>AMT*T3: the claim's total submitted charge.</summary>
    public decimal? TotalChargeAmount { get; set; }

    public DateTime? ServiceDateFrom { get; set; }
    public DateTime? ServiceDateTo { get; set; }

    /// <summary>NM109 of the 2100C service provider (XX = NPI).</summary>
    public string? ProviderNpi { get; set; }
    public string? ProviderName { get; set; }
}

public sealed class Edi276ParseResult
{
    public string InterchangeSenderId { get; init; } = string.Empty;
    public string InterchangeReceiverId { get; init; } = string.Empty;
    public string ApplicationSenderId { get; init; } = string.Empty;
    public string ApplicationReceiverId { get; init; } = string.Empty;
    public string UsageIndicator { get; init; } = "P";
    public string TransactionControlNumber { get; init; } = string.Empty;

    /// <summary>BHT03: echoed as the 277's BHT03.</summary>
    public string ReferenceId { get; init; } = string.Empty;

    public string? PayerName { get; init; }
    public string? PayerId { get; init; }
    public string? ReceiverName { get; init; }
    public string? ReceiverId { get; init; }
    public IReadOnlyList<ClaimStatusInquiryItem> Inquiries { get; init; } = [];
}

/// <summary>
/// Reads an X12 005010X212 276 (Health Care Claim Status Request):
/// 2000A payer (HL 20), 2000B information receiver (HL 21), 2000C service
/// provider (HL 19), 2000D subscriber (HL 22) and optional 2000E dependent
/// (HL 23), each subscriber or dependent with one or more 2200 claim loops
/// (TRN, REF*1K / REF*EJ, AMT*T3, DTP*472). Tolerant of unknown segments;
/// throws <see cref="FormatException"/> when there is no 276 to read.
/// </summary>
public static class Edi276Parser
{
    public static Edi276ParseResult Parse(string edi)
    {
        if (string.IsNullOrWhiteSpace(edi))
            throw new ArgumentException("EDI 276 content is empty", nameof(edi));
        if (edi.Length < 106 || !edi.StartsWith("ISA", StringComparison.Ordinal))
            throw new FormatException("EDI content is too short to contain a valid ISA segment.");

        var el = edi[3];
        var term = edi[105];
        var segments = edi.Split(term)
            .Select(s => s.Trim('\r', '\n', ' '))
            .Where(s => s.Length > 0)
            .Select(s => s.Split(el))
            .ToList();

        string isaSender = "", isaReceiver = "", usage = "P", gsSender = "", gsReceiver = "", st02 = "", bht03 = "";
        string? payerName = null, payerId = null, receiverName = null, receiverId = null;
        string? providerNpi = null, providerName = null;
        string? subId = null, subLast = null, subFirst = null;
        string? depLast = null, depFirst = null;
        var level = "";
        var sawSt = false;
        var items = new List<ClaimStatusInquiryItem>();
        ClaimStatusInquiryItem? current = null;

        static string E(string[] s, int i) => i < s.Length ? s[i].Trim() : string.Empty;

        foreach (var seg in segments)
        {
            switch (seg[0])
            {
                case "ISA":
                    isaSender = E(seg, 6);
                    isaReceiver = E(seg, 8);
                    usage = E(seg, 15) is "T" ? "T" : "P";
                    break;
                case "GS":
                    gsSender = E(seg, 2);
                    gsReceiver = E(seg, 3);
                    break;
                case "ST":
                    if (E(seg, 1) != "276")
                        throw new FormatException($"Expected a 276 transaction set, found {E(seg, 1)}.");
                    sawSt = true;
                    st02 = E(seg, 2);
                    break;
                case "BHT":
                    bht03 = E(seg, 3);
                    break;
                case "HL":
                    level = E(seg, 3);
                    current = null;
                    if (level == "22")
                    {
                        subId = subLast = subFirst = null;
                        depLast = depFirst = null;
                    }
                    else if (level == "23")
                    {
                        depLast = depFirst = null;
                    }
                    break;
                case "NM1":
                {
                    var entity = E(seg, 1);
                    if (entity == "PR") { payerName = E(seg, 3); payerId = E(seg, 9); }
                    else if (entity == "41") { receiverName = E(seg, 3); receiverId = E(seg, 9); }
                    else if (entity == "1P") { providerName = E(seg, 3); providerNpi = E(seg, 9); }
                    else if (entity == "IL") { subLast = E(seg, 3); subFirst = E(seg, 4); subId = E(seg, 9); }
                    else if (entity == "QC") { depLast = E(seg, 3); depFirst = E(seg, 4); }
                    break;
                }
                case "TRN" when level is "22" or "23":
                    current = new ClaimStatusInquiryItem
                    {
                        TraceNumber = E(seg, 2),
                        SubscriberId = subId ?? string.Empty,
                        SubscriberLastName = subLast,
                        SubscriberFirstName = subFirst,
                        IsDependent = level == "23",
                        DependentLastName = level == "23" ? depLast : null,
                        DependentFirstName = level == "23" ? depFirst : null,
                        ProviderNpi = NullIfEmpty(providerNpi),
                        ProviderName = NullIfEmpty(providerName),
                    };
                    items.Add(current);
                    break;
                case "REF" when current is not null:
                    if (E(seg, 1) == "1K") current.PayerClaimControlNumber = NullIfEmpty(E(seg, 2));
                    else if (E(seg, 1) == "EJ") current.PatientControlNumber = NullIfEmpty(E(seg, 2));
                    break;
                case "AMT" when current is not null && E(seg, 1) == "T3":
                    if (decimal.TryParse(E(seg, 2), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                        current.TotalChargeAmount = amount;
                    break;
                case "DTP" when current is not null && E(seg, 1) == "472":
                    (current.ServiceDateFrom, current.ServiceDateTo) = Dates(E(seg, 2), E(seg, 3));
                    break;
            }
        }

        if (!sawSt)
            throw new FormatException("The interchange has no 276 transaction set.");
        if (items.Count == 0)
            throw new FormatException("The 276 has no claim status inquiry (2200D TRN).");

        return new Edi276ParseResult
        {
            InterchangeSenderId = isaSender,
            InterchangeReceiverId = isaReceiver,
            ApplicationSenderId = gsSender,
            ApplicationReceiverId = gsReceiver,
            UsageIndicator = usage,
            TransactionControlNumber = st02,
            ReferenceId = bht03,
            PayerName = NullIfEmpty(payerName),
            PayerId = NullIfEmpty(payerId),
            ReceiverName = NullIfEmpty(receiverName),
            ReceiverId = NullIfEmpty(receiverId),
            Inquiries = items,
        };
    }

    private static (DateTime?, DateTime?) Dates(string qualifier, string value)
    {
        static DateTime? D8(string s) =>
            DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        if (qualifier == "RD8" && value.Split('-') is [var from, var to])
            return (D8(from), D8(to));
        var single = D8(value);
        return (single, single);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
