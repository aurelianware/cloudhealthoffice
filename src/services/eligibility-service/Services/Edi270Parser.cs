using EligibilityService.Models;

namespace EligibilityService.Services;

/// <summary>
/// Parses X12 005010X279A1 (270 Eligibility Inquiry) EDI into an EligibilityInquiry.
///
/// Handles the standard 4-level HL hierarchy:
///   2000A — Information Source (Payer)          HL03=20
///   2000B — Information Receiver (Provider)     HL03=21
///   2000C — Subscriber                          HL03=22
///   2000D — Dependent (optional)                HL03=23
///
/// Tolerant parser — skips unknown segments and handles missing optional loops.
/// </summary>
public interface IEdi270Parser
{
    /// <summary>
    /// Parse a raw X12 270 string.
    /// Returns the inquiry plus the interchange IDs needed to construct the 271 envelope.
    /// </summary>
    Edi270ParseResult Parse(string edi270);
}

public class Edi270ParseResult
{
    public EligibilityInquiry Inquiry { get; init; } = new();
    /// <summary>ISA06 from the 270 — becomes ISA08 (receiver) on the 271 response.</summary>
    public string InterchangeSenderId   { get; init; } = string.Empty;
    /// <summary>ISA08 from the 270 — becomes ISA06 (sender) on the 271 response.</summary>
    public string InterchangeReceiverId { get; init; } = string.Empty;
    public string ApplicationSenderId   { get; init; } = string.Empty;
    public string ApplicationReceiverId { get; init; } = string.Empty;
}

public class Edi270Parser : IEdi270Parser
{
    private readonly ILogger<Edi270Parser> _logger;

    public Edi270Parser(ILogger<Edi270Parser> logger)
    {
        _logger = logger;
    }

    public Edi270ParseResult Parse(string edi270)
    {
        if (string.IsNullOrWhiteSpace(edi270))
            throw new ArgumentException("EDI 270 content is empty", nameof(edi270));

        // ── Detect delimiters from ISA (fixed-width, always 106 chars) ──
        // Position 3  = element separator (*), position 105 = segment terminator (~)
        // Position 104 = component/sub-element separator (:)
        if (edi270.Length < 106)
            throw new FormatException("EDI content is too short to contain a valid ISA segment.");

        char elemSep   = edi270[3];
        char segTerm   = edi270[105];

        // ── Split into segments ──────────────────────────────────────────
        var segments = edi270
            .Split(segTerm)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Select(s => s.Split(elemSep))
            .ToList();

        var inquiry = new EligibilityInquiry
        {
            Id            = Guid.NewGuid().ToString(),
            Status        = EligibilityInquiryStatus.Pending,
            CreatedDate   = DateTime.UtcNow,
            RequestDate   = DateTime.UtcNow,
        };

        string isaSenderId   = string.Empty;
        string isaReceiverId = string.Empty;
        string gsAppSender   = string.Empty;
        string gsAppReceiver = string.Empty;

        // ── Current HL context ───────────────────────────────────────────
        string currentHlLevel = string.Empty; // "20"=payer, "21"=provider, "22"=subscriber, "23"=dependent

        foreach (var seg in segments)
        {
            if (seg.Length == 0) continue;
            var id = seg[0];

            switch (id)
            {
                // ── Interchange / group headers ─────────────────────────
                case "ISA":
                    // ISA06 = sender ID (elem 6), ISA08 = receiver ID (elem 8)
                    isaSenderId   = seg.ElementAtOrDefault(6)?.Trim() ?? string.Empty;
                    isaReceiverId = seg.ElementAtOrDefault(8)?.Trim() ?? string.Empty;
                    break;

                case "GS":
                    // GS02 = application sender, GS03 = application receiver
                    gsAppSender   = seg.ElementAtOrDefault(2) ?? string.Empty;
                    gsAppReceiver = seg.ElementAtOrDefault(3) ?? string.Empty;
                    break;

                case "ST":
                    // ST02 = transaction control number
                    inquiry.ControlNumber = seg.ElementAtOrDefault(2) ?? string.Empty;
                    break;

                // ── HL — set current loop context ───────────────────────
                case "HL":
                    // HL03 = hierarchical level code
                    currentHlLevel = seg.ElementAtOrDefault(3) ?? string.Empty;
                    break;

                // ── NM1 — Name segments per loop ────────────────────────
                case "NM1":
                    ParseNm1(seg, currentHlLevel, inquiry);
                    break;

                // ── REF — Reference segments (subscriber/group IDs) ─────
                case "REF":
                {
                    var qualifier = seg.ElementAtOrDefault(1) ?? string.Empty;
                    var value     = seg.ElementAtOrDefault(2) ?? string.Empty;
                    switch (qualifier)
                    {
                        case "0F": // Subscriber Number
                            inquiry.SubscriberId = value;
                            break;
                        case "1L": // Group or Policy Number
                            inquiry.GroupNumber = value;
                            break;
                    }
                    break;
                }

                // ── DMG — Demographic Information (DOB, gender) ─────────
                case "DMG":
                {
                    // DMG01=format qualifier (D8=CCYYMMDD), DMG02=date, DMG03=gender
                    var dateStr = seg.ElementAtOrDefault(2) ?? string.Empty;
                    var gender  = seg.ElementAtOrDefault(3) ?? string.Empty;

                    if (dateStr.Length == 8 && DateTime.TryParseExact(
                            dateStr, "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None,
                            out var dob))
                    {
                        if (currentHlLevel == "22") // subscriber
                        {
                            inquiry.SubscriberDOB    = dob;
                            inquiry.SubscriberGender = gender;
                        }
                        else if (currentHlLevel == "23") // dependent
                        {
                            inquiry.DependentDOB    = dob;
                            inquiry.DependentGender = gender;
                        }
                    }
                    break;
                }

                // ── DTP — Date/Time Reference ────────────────────────────
                case "DTP":
                {
                    // DTP01=qualifier (291=Service Date), DTP02=format (D8), DTP03=date
                    var qualifier = seg.ElementAtOrDefault(1) ?? string.Empty;
                    if (qualifier == "291")
                    {
                        var dateStr = seg.ElementAtOrDefault(3) ?? string.Empty;
                        if (dateStr.Length == 8 && DateTime.TryParseExact(
                                dateStr, "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None,
                                out var serviceDate))
                        {
                            inquiry.ServiceDateFrom = serviceDate;
                            inquiry.ServiceDateTo   = serviceDate;
                        }
                    }
                    break;
                }

                // ── EQ — Eligibility/Benefit Inquiry ────────────────────
                case "EQ":
                    // EQ01 = service type code (30=Health Benefit Plan Coverage)
                    inquiry.ServiceTypeCode = seg.ElementAtOrDefault(1) ?? "30";
                    break;

                // ── INS — Dependent relationship ─────────────────────────
                case "INS":
                    // INS02 = individual relationship code (01=Spouse, 19=Child, 18=Self)
                    if (currentHlLevel == "23")
                        inquiry.DependentRelationship = seg.ElementAtOrDefault(2);
                    break;
            }
        }

        _logger.LogInformation(
            "Parsed 270 EDI: subscriber={SubscriberId}, serviceType={ServiceType}, controlNumber={Control}",
            SanitizeForLog(inquiry.SubscriberId), inquiry.ServiceTypeCode, SanitizeForLog(inquiry.ControlNumber));

        return new Edi270ParseResult
        {
            Inquiry               = inquiry,
            InterchangeSenderId   = isaSenderId,
            InterchangeReceiverId = isaReceiverId,
            ApplicationSenderId   = gsAppSender,
            ApplicationReceiverId = gsAppReceiver,
        };
    }

    private static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", "").Replace("\n", "");

    private static void ParseNm1(string[] seg, string hlLevel, EligibilityInquiry inquiry)
    {
        // NM1*{entityCode}*{type}*{lastName}*{firstName}*...*{idQual}*{id}
        var entityCode = seg.ElementAtOrDefault(1) ?? string.Empty;
        var lastName   = seg.ElementAtOrDefault(3) ?? string.Empty;
        var firstName  = seg.ElementAtOrDefault(4) ?? string.Empty;
        var idQual     = seg.ElementAtOrDefault(8) ?? string.Empty;
        var idValue    = seg.ElementAtOrDefault(9) ?? string.Empty;

        switch (entityCode)
        {
            case "PR": // Payer (information source)
                inquiry.PayerName = lastName; // org name is in NM103
                if (idQual == "PI") inquiry.PayerId = idValue;
                break;

            case "1P": // Provider (information receiver)
                inquiry.ProviderId = idValue;
                if (idQual == "XX") inquiry.ProviderNPI = idValue;
                break;

            case "IL": // Insured/Subscriber (2000C)
                inquiry.SubscriberLastName  = lastName;
                inquiry.SubscriberFirstName = firstName;
                if (idQual is "MI" or "II") inquiry.SubscriberId = idValue;
                break;

            case "QC" when hlLevel == "23": // Patient/Dependent
            case "03" when hlLevel == "23":
                inquiry.DependentLastName  = lastName;
                inquiry.DependentFirstName = firstName;
                break;
        }
    }
}
