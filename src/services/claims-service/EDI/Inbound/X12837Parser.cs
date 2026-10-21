using System.Globalization;
using CloudHealthOffice.ClaimsScrubEngine.Models;

namespace ClaimsService.EDI.Inbound;

/// <summary>
/// Parses raw X12 837 (Professional/Institutional) EDI text into the same
/// <see cref="X12837Claim"/> shape <c>ClaimToX12837Mapper</c> already
/// produces for outbound submission — so the inbound and outbound paths
/// share one domain model instead of inventing a second one.
///
/// Like <c>Enrollment834EdiParser</c>, this walks the segment stream
/// explicitly with an "am I inside a dependent (2000C) loop" flag rather
/// than relying on a library's declarative loop-grouping — a hands-on
/// evaluation of Indice.Edi found that approach silently mis-attributes
/// data on exactly this kind of nested hierarchy (see the 834 parser's
/// doc comment and PR #1002 for the evidence).
///
/// Deliberately tolerant of two real internal shapes already found in
/// this codebase, not just the spec: <c>EncounterTransformer</c> emits
/// 837s with no HL loops at all (flat SBR-only) and a 6-element SV1 with
/// the diagnosis-pointer composite at index 5, while
/// <c>FmmisClaimTransformer</c> emits full HL/2000A/2000B hierarchy with
/// SV1's diagnosis-pointer composite at the spec-correct index 6 (place
/// of service occupies index 4 there). Real evaluator-submitted files
/// will vary at least this much, if not more — this parser doesn't
/// assume one canonical shape.
///
/// Structure: <see cref="Parse(X12Document)"/> walks the segments once and
/// <see cref="Dispatch"/> routes each one by segment id. Where the same
/// segment id means different things in different loops (SBR, NM1, REF,
/// N3/N4, CAS, DMG, DTP), a per-segment router tests the loop conditions
/// in a fixed order — the first match wins, exactly as the order of
/// guarded <c>case</c> labels did when this was one switch — and hands
/// off to a per-loop handler. All cursor and in-progress state lives in
/// <see cref="ParseState"/>.
/// </summary>
public static class X12837Parser
{
    public static List<X12837Claim> Parse(string ediContent) => Parse(X12Tokenizer.Tokenize(ediContent));

    /// <summary>
    /// Parses an already-tokenized document — e.g. one transaction set cut
    /// out of a file after SNIP validation accepted it.
    /// </summary>
    public static List<X12837Claim> Parse(X12Document doc)
    {
        var state = new ParseState(doc.ComponentSeparator);

        foreach (var seg in doc.Segments)
        {
            Dispatch(state, seg);
        }

        // Safety net for a file missing its trailing SE.
        FlushClaim(state);

        return state.Claims;
    }

    /// <summary>
    /// Cursor and objects under construction while walking one document.
    /// Mutable fields on purpose: the handlers below are the old body of
    /// one long method, split up, and share its locals through this.
    /// </summary>
    private sealed class ParseState(char componentSeparator)
    {
        public readonly char ComponentSep = componentSeparator;

        public readonly List<X12837Claim> Claims = new();

        // Envelope (ISA / ST / BHT).
        public string? InterchangeControlNumber;
        public string TransactionControlNumber = string.Empty;
        public string TransactionDate = string.Empty;
        public ClaimType ClaimType = ClaimType.Professional;
        public string? TransactionTypeCode;

        // 1000A / 1000B.
        public ClaimSubmitter? Submitter;
        public ClaimReceiver? Receiver;

        // 2000A / 2010AA.
        public BillingProvider? BillingProvider;
        // 2010AB / 2010AC belong to the 2000A billing provider loop and apply
        // to every claim under it; reset when a new HL*20 starts.
        public ProviderAddress? PayToAddress;
        public PayToPlan? PayToPlan;

        // 2000B / 2010BA and 2000C / 2010CA.
        public ClaimSubscriber? Subscriber;
        public ClaimPatient? Patient;
        public RenderingProviderInfo? PendingRenderingProvider;

        public bool InsideDependentLoop;
        public string? LastNm1Context;

        // Per-claim accumulation, reset on CLM/flush.
        public string ClaimId = string.Empty;
        public decimal TotalCharge = 0m;
        public string? PlaceOfService;
        public string? FrequencyCode;
        public string? FacilityTypeCode;
        // 2300 REF*G1 — prior authorization number for the claim.
        public string? PriorAuthorizationNumber;
        // 2300 REF*F8 — the original claim a replacement or void amends.
        public string? OriginalClaimReference;
        // 2300 / 2400 PWK — paperwork sent separately, with its control number.
        public List<AttachmentReport> AttachmentReports = [];
        public List<DiagnosisCode> DiagnosisCodes = [];
        // Institutional (837I) 2300 detail, reset on CLM/flush.
        public string? AdmissionDate;
        public string? AdmissionHour;
        public string? DischargeDate;
        public string? DischargeHour;
        public string? StatementFrom;
        public string? StatementTo;
        public string? AdmissionType;
        public string? AdmissionSource;
        public string? PatientStatus;
        public string? DrgCode;
        public InstitutionalCode? PrincipalProcedure;
        public List<InstitutionalCode> OtherProcedures = [];
        public List<InstitutionalCode> OccurrenceCodes = [];
        public List<InstitutionalCode> OccurrenceSpanCodes = [];
        public List<InstitutionalCode> ValueCodes = [];
        public List<InstitutionalCode> ConditionCodes = [];
        public List<ServiceLine> ServiceLines = [];
        public ServiceLine? CurrentLine;
        public bool ClaimOpen;

        // Coordination of benefits. SBR01 of the 2000B loop is the receiving
        // payer's responsibility sequence; it is captured at CLM (the claim's
        // own 2000B precedes it). After CLM and before the first LX, an SBR
        // opens loop 2320 (other subscriber) for another payer, whose 2330B
        // NM1*PR, AMT*D and CAS follow; within a line, SVD opens loop 2430
        // (that payer's line adjudication) and the CAS after it belong to it.
        public string? SubscriberPayerResponsibility;
        public string? ClaimPayerResponsibility;
        public bool HlSinceClaim;
        public List<OtherPayer> OtherPayers = [];

        public bool InOtherPayerLoop() => ClaimOpen && !HlSinceClaim && CurrentLine is null && OtherPayers.Count > 0;
    }

    // ------------------------------------------------------------------
    // Dispatch
    // ------------------------------------------------------------------

    private static void Dispatch(ParseState s, X12Segment seg)
    {
        switch (seg.Id)
        {
            case "ISA": ParseInterchangeHeader(s, seg); break;
            case "ST": ParseTransactionSetHeader(s, seg); break;
            case "BHT": ParseBeginningOfHierarchicalTransaction(s, seg); break;
            case "HL": ParseHierarchicalLevel(s, seg); break;
            case "PAT": Parse2000CPatientRelationship(s, seg); break;
            case "SBR": RouteSbr(s, seg); break;
            case "NM1": RouteNm1(s, seg); break;
            case "REF": RouteRef(s, seg); break;
            case "AMT": RouteAmt(s, seg); break;
            case "CAS": RouteCas(s, seg); break;
            case "SVD": RouteSvd(s, seg); break;
            case "DMG": RouteDmg(s, seg); break;
            case "N3": RouteN3(s, seg); break;
            case "N4": RouteN4(s, seg); break;
            case "CLM": Parse2300Claim(s, seg); break;
            case "CL1": if (s.ClaimOpen) Parse2300InstitutionalClaimCode(s, seg); break;
            case "HI": if (s.ClaimOpen) Parse2300HealthCareCodes(s, seg); break;
            case "PWK": if (s.ClaimOpen) ParsePaperwork(s, seg); break;
            case "LX": if (s.ClaimOpen) Parse2400LineNumber(s, seg); break;
            case "SV1": if (s.ClaimOpen) Parse2400ProfessionalService(s, seg); break;
            case "SV2": if (s.ClaimOpen) Parse2400InstitutionalService(s, seg); break;
            case "DTP": RouteDtp(s, seg); break;
            case "LIN": if (s.ClaimOpen && s.CurrentLine is not null && seg.Element(1) == "N4") Parse2410DrugIdentification(s, seg); break;
            case "CTP": if (s.ClaimOpen && s.CurrentLine is { NationalDrugCode: not null }) Parse2410DrugQuantity(s, seg); break;
            case "SE": FlushClaim(s); break;
        }
    }

    // Each router tests its loop conditions in the order the guarded case
    // labels of the original single switch did; the first match wins and
    // no match means the segment is ignored.

    private static void RouteSbr(ParseState s, X12Segment seg)
    {
        if (s.ClaimOpen && !s.HlSinceClaim && s.CurrentLine is null) Parse2320OtherSubscriber(s, seg);
        else Parse2000BSubscriberInformation(s, seg);
    }

    private static void RouteNm1(ParseState s, X12Segment seg)
    {
        if (s.InOtherPayerLoop()) Parse2330OtherPayerName(s, seg);
        else ParseName(s, seg);
    }

    private static void RouteRef(ParseState s, X12Segment seg)
    {
        if (s.InOtherPayerLoop() && s.LastNm1Context == "2330:PR")
            Parse2330BPayerReference(s, seg);
        else if (s.ClaimOpen && s.CurrentLine is not null && seg.Element(0) == "G1")
            Parse2400PriorAuthorization(s, seg);
        else if (s.ClaimOpen && s.CurrentLine is null && !s.HlSinceClaim && !s.InOtherPayerLoop() && seg.Element(0) == "G1")
            Parse2300PriorAuthorization(s, seg);
        else if (s.ClaimOpen && s.CurrentLine is null && !s.HlSinceClaim && !s.InOtherPayerLoop() && seg.Element(0) == "F8"
                 && seg.Element(1)?.Trim() is { Length: > 0 } originalClaim)
            s.OriginalClaimReference = originalClaim;
        else if (s.LastNm1Context == "PE" && s.PayToPlan is not null && seg.Element(0) == "EI")
            s.PayToPlan = s.PayToPlan with { TaxId = seg.Element(1) };
        else if (s.LastNm1Context == "85" && s.BillingProvider is not null && seg.Element(0) == "EI")
            s.BillingProvider = s.BillingProvider with { TaxIdQualifier = "EI", TaxId = seg.Element(1) };
    }

    private static void RouteAmt(ParseState s, X12Segment seg)
    {
        if (s.InOtherPayerLoop() && seg.Element(0) == "D") Parse2320PayerPaidAmount(s, seg);
    }

    private static void RouteCas(ParseState s, X12Segment seg)
    {
        if (s.InOtherPayerLoop()) Parse2320ClaimAdjustment(s, seg);
        else if (s.ClaimOpen && s.CurrentLine is { OtherPayerAdjudications.Count: > 0 }) Parse2430LineAdjustment(s, seg);
    }

    private static void RouteSvd(ParseState s, X12Segment seg)
    {
        if (s.ClaimOpen && s.CurrentLine is not null) Parse2430LineAdjudication(s, seg);
    }

    private static void RouteDmg(ParseState s, X12Segment seg)
    {
        // A DMG inside 2330 (other subscriber) is skipped.
        if (s.InOtherPayerLoop()) return;
        ParseDemographics(s, seg);
    }

    private static void RouteN3(ParseState s, X12Segment seg)
    {
        if (s.LastNm1Context == "85" && s.BillingProvider is not null)
            s.BillingProvider = s.BillingProvider with
            {
                Address = s.BillingProvider.Address with { Line1 = seg.Element(0) ?? string.Empty, Line2 = seg.Element(1) }
            };
        else if (s.LastNm1Context == "87" && s.PayToAddress is not null)
            s.PayToAddress = s.PayToAddress with { Line1 = seg.Element(0) ?? string.Empty, Line2 = seg.Element(1) };
        else if (s.LastNm1Context == "PE" && s.PayToPlan is not null)
            s.PayToPlan = s.PayToPlan with { Address = (s.PayToPlan.Address ?? EmptyAddress()) with { Line1 = seg.Element(0) ?? string.Empty, Line2 = seg.Element(1) } };
    }

    private static void RouteN4(ParseState s, X12Segment seg)
    {
        if (s.LastNm1Context == "85" && s.BillingProvider is not null)
            s.BillingProvider = s.BillingProvider with
            {
                Address = s.BillingProvider.Address with { City = seg.Element(0) ?? string.Empty, State = seg.Element(1) ?? string.Empty, PostalCode = seg.Element(2) ?? string.Empty }
            };
        else if (s.LastNm1Context == "87" && s.PayToAddress is not null)
            s.PayToAddress = WithCityStateZip(s.PayToAddress, seg);
        else if (s.LastNm1Context == "PE" && s.PayToPlan is not null)
            s.PayToPlan = s.PayToPlan with { Address = WithCityStateZip(s.PayToPlan.Address ?? EmptyAddress(), seg) };
    }

    private static void RouteDtp(ParseState s, X12Segment seg)
    {
        if (s.ClaimOpen && s.CurrentLine is null) Parse2300ClaimDates(s, seg);
        else if (s.ClaimOpen && s.CurrentLine is not null && seg.Element(0) == "472") Parse2400ServiceDate(s, seg);
    }

    // ------------------------------------------------------------------
    // Envelope: ISA / ST / BHT
    // ------------------------------------------------------------------

    private static void ParseInterchangeHeader(ParseState s, X12Segment seg)
    {
        s.InterchangeControlNumber = seg.Element(12);
    }

    private static void ParseTransactionSetHeader(ParseState s, X12Segment seg)
    {
        s.TransactionControlNumber = seg.Element(1) ?? string.Empty;
        var version = seg.Element(2);
        s.ClaimType = version switch
        {
            not null when version.Contains("223") => ClaimType.Institutional,
            not null when version.Contains("224") => ClaimType.Dental,
            _ => ClaimType.Professional
        };
    }

    private static void ParseBeginningOfHierarchicalTransaction(ParseState s, X12Segment seg)
    {
        s.TransactionDate = seg.Element(3) ?? s.TransactionDate;
        s.TransactionTypeCode = seg.Element(5);
    }

    // ------------------------------------------------------------------
    // HL hierarchy: 2000A billing provider / 2000B subscriber / 2000C patient
    // ------------------------------------------------------------------

    private static void ParseHierarchicalLevel(ParseState s, X12Segment seg)
    {
        // A new HL closes the open claim: 2300 never spans an HL,
        // and the claim must be built from its own HL's billing
        // provider / subscriber / patient / pay-to state, not the
        // next HL's.
        FlushClaim(s);
        // No 2320/2330 loop follows an HL until the next CLM
        // (FlushClaim already closed the claim; kept so the
        // 2320 guards never depend on flush order).
        s.HlSinceClaim = true;
        var levelCode = seg.Element(2);
        if (levelCode == "20")
        {
            s.PayToAddress = null;
            s.PayToPlan = null;
        }
        if (levelCode == "23")
        {
            s.InsideDependentLoop = true;
            s.Patient = new ClaimPatient { FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty, RelationshipCode = string.Empty };
        }
        else if (levelCode == "22")
        {
            s.InsideDependentLoop = false;
            s.Patient = null;
        }
    }

    private static void Parse2000BSubscriberInformation(ParseState s, X12Segment seg)
    {
        s.SubscriberPayerResponsibility = seg.Element(0);
        if (s.Subscriber is not null && !s.InsideDependentLoop)
        {
            s.Subscriber = s.Subscriber with { GroupNumber = seg.Element(2) };
        }
    }

    private static void Parse2000CPatientRelationship(ParseState s, X12Segment seg)
    {
        if (s.Patient is not null)
        {
            s.Patient = s.Patient with { RelationshipCode = seg.Element(0) ?? string.Empty };
        }
    }

    // ------------------------------------------------------------------
    // NM1 outside 2330: 1000A/B, 2010AA/AB/AC, 2010BA, 2010CA, 2310B
    // ------------------------------------------------------------------

    private static void ParseName(ParseState s, X12Segment seg)
    {
        s.LastNm1Context = seg.Element(0);
        switch (s.LastNm1Context)
        {
            case "41": Parse1000ASubmitterName(s, seg); break;
            case "40": Parse1000BReceiverName(s, seg); break;
            case "85": Parse2010AABillingProviderName(s, seg); break;
            // 2010AB pay-to address: NM101 = 87, NM102 = 1/2; NM103
            // onward are not used in 5010, so only N3/N4 carry data.
            case "87": s.PayToAddress = EmptyAddress(); break;
            case "PE": Parse2010ACPayToPlanName(s, seg); break;
            case "IL": Parse2010BASubscriberName(s, seg); break;
            case "QC" when s.InsideDependentLoop: Parse2010CAPatientName(s, seg); break;
            case "82": Parse2310BRenderingProviderName(s, seg); break;
        }
    }

    private static void Parse1000ASubmitterName(ParseState s, X12Segment seg)
    {
        var (qual, id) = TrailingIdPair(seg);
        s.Submitter = new ClaimSubmitter
        {
            Name = seg.Element(2) ?? string.Empty,
            IdentificationQualifier = qual ?? string.Empty,
            IdentificationCode = id ?? string.Empty
        };
    }

    private static void Parse1000BReceiverName(ParseState s, X12Segment seg)
    {
        var (qual, id) = TrailingIdPair(seg);
        s.Receiver = new ClaimReceiver
        {
            Name = seg.Element(2) ?? string.Empty,
            IdentificationQualifier = qual ?? string.Empty,
            IdentificationCode = id ?? string.Empty
        };
    }

    private static void Parse2010AABillingProviderName(ParseState s, X12Segment seg)
    {
        var (_, npi) = TrailingIdPair(seg);
        s.BillingProvider = new BillingProvider
        {
            Npi = npi ?? string.Empty,
            Name = seg.Element(2) ?? string.Empty,
            EntityType = seg.Element(1) ?? string.Empty,
            Address = new ProviderAddress { Line1 = string.Empty, City = string.Empty, State = string.Empty, PostalCode = string.Empty }
        };
    }

    // 2010AC pay-to plan (subrogation): NM103 name,
    // NM108/NM109 PI or XV plan id; N3/N4 and REF*EI follow.
    private static void Parse2010ACPayToPlanName(ParseState s, X12Segment seg)
    {
        // Only a PI/XV trailing pair is the plan id; without
        // NM108/NM109 the trailing pair would be NM102/NM103.
        var (qual, id) = TrailingIdPair(seg);
        var hasId = qual is "PI" or "XV";
        s.PayToPlan = new PayToPlan
        {
            Name = seg.Element(2) ?? string.Empty,
            IdentificationQualifier = hasId ? qual : null,
            IdentificationCode = hasId ? id : null,
        };
    }

    private static void Parse2010BASubscriberName(ParseState s, X12Segment seg)
    {
        var (_, memberId) = TrailingIdPair(seg);
        s.Subscriber = new ClaimSubscriber
        {
            MemberId = memberId ?? string.Empty,
            FirstName = seg.Element(3) ?? string.Empty,
            LastName = seg.Element(2) ?? string.Empty,
            MiddleName = seg.Element(4),
            DateOfBirth = string.Empty
        };
    }

    private static void Parse2010CAPatientName(ParseState s, X12Segment seg)
    {
        var (_, dependentMemberId) = TrailingIdPair(seg);
        s.Patient = (s.Patient ?? new ClaimPatient { FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty, RelationshipCode = string.Empty }) with
        {
            MemberId = dependentMemberId,
            FirstName = seg.Element(3) ?? string.Empty,
            LastName = seg.Element(2) ?? string.Empty,
            MiddleName = seg.Element(4)
        };
    }

    private static void Parse2310BRenderingProviderName(ParseState s, X12Segment seg)
    {
        var (_, npi) = TrailingIdPair(seg);
        s.PendingRenderingProvider = new RenderingProviderInfo
        {
            Npi = npi ?? string.Empty,
            Name = $"{seg.Element(3)} {seg.Element(2)}".Trim()
        };
    }

    // 2010BA subscriber / 2010CA patient demographics.
    private static void ParseDemographics(ParseState s, X12Segment seg)
    {
        if (s.InsideDependentLoop && s.Patient is not null)
        {
            s.Patient = s.Patient with { DateOfBirth = seg.Element(1) ?? string.Empty, Gender = seg.Element(2) };
        }
        else if (s.Subscriber is not null)
        {
            s.Subscriber = s.Subscriber with { DateOfBirth = seg.Element(1) ?? string.Empty, Gender = seg.Element(2) };
        }
    }

    // ------------------------------------------------------------------
    // 2300 claim information
    // ------------------------------------------------------------------

    private static void Parse2300Claim(ParseState s, X12Segment seg)
    {
        FlushClaim(s);
        s.ClaimOpen = true;
        s.HlSinceClaim = false;
        s.ClaimPayerResponsibility = s.SubscriberPayerResponsibility;
        s.ClaimId = seg.Element(0) ?? string.Empty;
        decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out s.TotalCharge);
        var clmComposite = seg.Element(4) is { } c4 ? X12Tokenizer.SplitComponents(c4, s.ComponentSep) : [];
        s.PlaceOfService = clmComposite.Length > 0 && clmComposite[0].Length > 0 ? clmComposite[0] : null;
        s.FrequencyCode = clmComposite.Length > 2 && clmComposite[2].Length > 0 ? clmComposite[2] : null;
        // On an 837I, CLM05-1 is the facility type code (first
        // two digits of the type of bill), not a place of
        // service. PlaceOfServiceCode keeps carrying it for
        // backward compatibility; FacilityTypeCode names it.
        s.FacilityTypeCode = s.ClaimType == ClaimType.Institutional ? s.PlaceOfService : null;
    }

    private static void Parse2300PriorAuthorization(ParseState s, X12Segment seg)
    {
        // 2300 REF*G1 — the claim's prior authorization number.
        // A REF*G1 inside 2320/2330 is another payer's (handled
        // or skipped above), never this payer's.
        if (seg.Element(1)?.Trim() is { Length: > 0 } claimPriorAuth)
        {
            s.PriorAuthorizationNumber = claimPriorAuth;
        }
    }

    private static void Parse2300InstitutionalClaimCode(ParseState s, X12Segment seg)
    {
        s.AdmissionType = seg.Element(0) is { Length: > 0 } cl101 ? cl101 : null;
        s.AdmissionSource = seg.Element(1) is { Length: > 0 } cl102 ? cl102 : null;
        s.PatientStatus = seg.Element(2) is { Length: > 0 } cl103 ? cl103 : null;
    }

    private static void Parse2300HealthCareCodes(ParseState s, X12Segment seg)
    {
        // HI0x composite: -1 qualifier, -2 code, -3 date format
        // (D8/RD8), -4 date or period, -5 amount, -9 present-on-
        // admission indicator. On an 837I the qualifier decides
        // what the code is; only diagnosis qualifiers become
        // DiagnosisCodes (unrecognized qualifiers stay
        // diagnoses, as before, so nothing is silently dropped).
        foreach (var element in seg.Elements)
        {
            if (element.Length == 0) continue;
            var parts = X12Tokenizer.SplitComponents(element, s.ComponentSep);
            if (parts.Length < 2) continue;
            var qualifier = parts[0];
            switch (qualifier)
            {
                case "DR":
                    s.DrgCode = parts[1].Length > 0 ? parts[1] : null;
                    break;

                case "BBR" or "BR" or "CAH":
                {
                    var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                    s.PrincipalProcedure = new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date };
                    break;
                }

                case "BBQ" or "BQ":
                {
                    var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                    s.OtherProcedures.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date });
                    break;
                }

                case "BH":
                {
                    var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                    s.OccurrenceCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date });
                    break;
                }

                case "BI":
                {
                    var (from, to) = SplitPeriod(At(parts, 2), At(parts, 3));
                    s.OccurrenceSpanCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = from, DateEnd = to });
                    break;
                }

                case "BE":
                    s.ValueCodes.Add(new InstitutionalCode
                    {
                        Qualifier = qualifier,
                        Code = parts[1],
                        Amount = decimal.TryParse(At(parts, 4), NumberStyles.Number, CultureInfo.InvariantCulture, out var valueAmount)
                            ? valueAmount
                            : null
                    });
                    break;

                case "BG":
                    s.ConditionCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1] });
                    break;

                case "TC":
                    // Treatment codes (therapy plan) — not modeled.
                    break;

                default:
                    s.DiagnosisCodes.Add(new DiagnosisCode
                    {
                        Qualifier = qualifier,
                        Code = parts[1],
                        Pointer = s.DiagnosisCodes.Count + 1,
                        PresentOnAdmission = At(parts, 8)
                    });
                    break;
            }
        }
    }

    private static void Parse2300ClaimDates(ParseState s, X12Segment seg)
    {
        // 2300 claim-level dates (before the first LX).
        var format = seg.Element(1);
        var value = seg.Element(2);
        switch (seg.Element(0))
        {
            case "435": // Admission date/hour (DT or D8)
                (s.AdmissionDate, s.AdmissionHour) = SplitDateTime(format, value);
                break;

            case "096": // Discharge hour (TM). Tolerates a D8/DT
                        // date, which EncounterTransformer emits.
                if (format == "TM")
                {
                    s.DischargeHour = string.IsNullOrEmpty(value) ? null : value;
                }
                else
                {
                    var (date, hour) = SplitDateTime(format, value);
                    s.DischargeDate = date;
                    s.DischargeHour = hour ?? s.DischargeHour;
                }
                break;

            case "434": // Statement covers period (RD8)
                (s.StatementFrom, s.StatementTo) = SplitPeriod(format, value);
                break;
        }
    }

    // ------------------------------------------------------------------
    // 2320 / 2330 coordination of benefits (claim level)
    // ------------------------------------------------------------------

    private static void Parse2320OtherSubscriber(ParseState s, X12Segment seg)
    {
        // Loop 2320 — another payer on this claim.
        s.OtherPayers.Add(new OtherPayer { PayerResponsibilityCode = seg.Element(0) ?? string.Empty });
        s.LastNm1Context = null;
    }

    private static void Parse2320PayerPaidAmount(ParseState s, X12Segment seg)
    {
        // 2320 AMT*D — payer paid amount.
        if (decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var payerPaid))
        {
            s.OtherPayers[^1] = s.OtherPayers[^1] with { PaidAmount = payerPaid };
        }
    }

    private static void Parse2320ClaimAdjustment(ParseState s, X12Segment seg)
    {
        // 2320 CAS — claim-level adjustments by this payer.
        s.OtherPayers[^1] = s.OtherPayers[^1] with
        {
            ClaimAdjustments = [.. s.OtherPayers[^1].ClaimAdjustments, .. ParseCas(seg)]
        };
    }

    private static void Parse2330OtherPayerName(ParseState s, X12Segment seg)
    {
        // Loops 2330A–I (other subscriber, other payer and its
        // providers): only 2330B NM1*PR is kept. Marked so the
        // N3/N4/REF/DMG handlers below never read these as the
        // claim's own billing provider or subscriber.
        s.LastNm1Context = "2330:" + seg.Element(0);
        if (seg.Element(0) == "PR")
        {
            var (_, payerId) = TrailingIdPair(seg);
            s.OtherPayers[^1] = s.OtherPayers[^1] with { PayerName = seg.Element(2), PayerId = payerId };
        }
    }

    private static void Parse2330BPayerReference(ParseState s, X12Segment seg)
    {
        // 2330B REF*2U (payer identification number) / REF*FY
        // (claim office number): other identifiers a 2430 SVD01
        // may name the payer by.
        var qualifier = seg.Element(0);
        var value = seg.Element(1)?.Trim();
        if (qualifier is "2U" or "FY" && !string.IsNullOrEmpty(value))
        {
            s.OtherPayers[^1] = s.OtherPayers[^1] with
            {
                AdditionalPayerIds = [.. s.OtherPayers[^1].AdditionalPayerIds, value]
            };
        }
    }

    // ------------------------------------------------------------------
    // 2400 service line
    // ------------------------------------------------------------------

    /// <summary>
    /// PWK in 2300 (before the first LX: the claim) or 2400 (the current
    /// line). PWK01 report type and PWK02 transmission code are required;
    /// PWK06 is the attachment control number a later 275 repeats in TRN02
    /// (PWK05 = AC). A PWK without type or transmission code is dropped (the
    /// SNIP validator reports it).
    /// </summary>
    private static void ParsePaperwork(ParseState s, X12Segment seg)
    {
        var reportType = seg.Element(0)?.Trim();
        var transmission = seg.Element(1)?.Trim();
        if (string.IsNullOrEmpty(reportType) || string.IsNullOrEmpty(transmission))
            return;

        var controlNumber = seg.Element(5)?.Trim();
        s.AttachmentReports.Add(new AttachmentReport
        {
            ReportTypeCode = reportType,
            TransmissionCode = transmission,
            ControlNumber = string.IsNullOrEmpty(controlNumber) ? null : controlNumber,
            LineNumber = s.CurrentLine?.LineNumber,
        });
    }

    private static void Parse2400LineNumber(ParseState s, X12Segment seg)
    {
        FlushLine(s);
        int.TryParse(seg.Element(0), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNumber);
        s.CurrentLine = new ServiceLine { LineNumber = lineNumber, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
    }

    private static void Parse2400ProfessionalService(ParseState s, X12Segment seg)
    {
        s.CurrentLine ??= new ServiceLine { LineNumber = s.ServiceLines.Count + 1, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
        var procComposite = seg.Element(0) is { } c0 ? X12Tokenizer.SplitComponents(c0, s.ComponentSep) : [];
        decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var chargeAmount);
        decimal.TryParse(seg.Element(3), NumberStyles.Number, CultureInfo.InvariantCulture, out var units);
        // SV107 (diagnosis pointer composite) is index 6 in a
        // spec-correct SV1 with SV105 place-of-service present;
        // EncounterTransformer's simplified SV1 omits SV105/106
        // entirely and puts it at index 5 instead. Prefer 6,
        // fall back to 5 — see the class doc comment.
        var pointerRaw = seg.Element(6) ?? seg.Element(5);
        var pointers = pointerRaw is { } pr
            ? X12Tokenizer.SplitComponents(pr, s.ComponentSep)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList()
            : null;

        s.CurrentLine = s.CurrentLine with
        {
            ProcedureCodeQualifier = procComposite.Length > 0 ? procComposite[0] : null,
            ProcedureCode = procComposite.Length > 1 ? procComposite[1] : string.Empty,
            Modifiers = procComposite.Length > 2 ? [.. procComposite[2..].Where(m => m.Length > 0)] : null,
            ChargeAmount = chargeAmount,
            UnitType = seg.Element(2),
            Units = units,
            PlaceOfService = seg.Element(4),
            DiagnosisPointers = pointers is { Count: > 0 } ? pointers : null
        };
    }

    private static void Parse2400InstitutionalService(ParseState s, X12Segment seg)
    {
        // Institutional service line — a genuinely different
        // layout from SV1, not just an index shift: revenue
        // code occupies index 0 (SV1 has no equivalent slot),
        // which pushes the procedure composite to index 1.
        // SV2*{revenueCode}*HC:{proc}{mods}*{charge}*UN*{units}
        s.CurrentLine ??= new ServiceLine { LineNumber = s.ServiceLines.Count + 1, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
        var sv2Proc = seg.Element(1) is { } c1 ? X12Tokenizer.SplitComponents(c1, s.ComponentSep) : [];
        decimal.TryParse(seg.Element(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var sv2Charge);
        decimal.TryParse(seg.Element(4), NumberStyles.Number, CultureInfo.InvariantCulture, out var sv2Units);
        // Not present in every institutional file (this repo's
        // own FMMIS generator omits it), so no fallback chain
        // like SV1's — absent means absent.
        var sv2PointerRaw = seg.Element(6);
        var sv2Pointers = sv2PointerRaw is { } spr
            ? X12Tokenizer.SplitComponents(spr, s.ComponentSep)
                .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
                .Where(n => n.HasValue)
                .Select(n => n!.Value)
                .ToList()
            : null;

        s.CurrentLine = s.CurrentLine with
        {
            RevenueCode = seg.Element(0),
            // SV202 is situational on an 837I: room and board,
            // pharmacy etc. are billed by revenue code alone.
            ProcedureCodeQualifier = sv2Proc.Length > 0 && sv2Proc[0].Length > 0 ? sv2Proc[0] : null,
            ProcedureCode = sv2Proc.Length > 1 ? sv2Proc[1] : string.Empty,
            Modifiers = sv2Proc.Length > 2 ? [.. sv2Proc[2..].Where(m => m.Length > 0)] : null,
            ChargeAmount = sv2Charge,
            UnitType = seg.Element(3),
            Units = sv2Units,
            DiagnosisPointers = sv2Pointers is { Count: > 0 } ? sv2Pointers : null
        };
    }

    private static void Parse2400PriorAuthorization(ParseState s, X12Segment seg)
    {
        // 2400 REF*G1 — this line's prior authorization number;
        // overrides the claim's (2300) for the line.
        if (seg.Element(1)?.Trim() is { Length: > 0 } linePriorAuth)
        {
            s.CurrentLine = s.CurrentLine! with { PriorAuthorizationNumber = linePriorAuth };
        }
    }

    private static void Parse2400ServiceDate(ParseState s, X12Segment seg)
    {
        var dateQualifier = seg.Element(1);
        var dateValue = seg.Element(2) ?? string.Empty;
        if (dateQualifier == "RD8" && dateValue.Contains('-'))
        {
            var range = dateValue.Split('-', 2);
            s.CurrentLine = s.CurrentLine! with { ServiceDate = range[0], ServiceDateEnd = range[1] };
        }
        else
        {
            s.CurrentLine = s.CurrentLine! with { ServiceDate = dateValue };
        }
    }

    // ------------------------------------------------------------------
    // 2410 drug identification
    // ------------------------------------------------------------------

    private static void Parse2410DrugIdentification(ParseState s, X12Segment seg)
    {
        // 2410 drug identification: LIN**N4*{11-digit NDC}
        s.CurrentLine = s.CurrentLine! with { NationalDrugCode = seg.Element(2) is { Length: > 0 } ndc ? ndc : null };
    }

    private static void Parse2410DrugQuantity(ParseState s, X12Segment seg)
    {
        // 2410 drug quantity: CTP****{quantity}*{unit composite}
        var unitComposite = seg.Element(4) is { } ctp05 ? X12Tokenizer.SplitComponents(ctp05, s.ComponentSep) : [];
        s.CurrentLine = s.CurrentLine! with
        {
            DrugQuantity = decimal.TryParse(seg.Element(3), NumberStyles.Number, CultureInfo.InvariantCulture, out var drugQuantity)
                ? drugQuantity
                : null,
            DrugUnitOfMeasure = At(unitComposite, 0)
        };
    }

    // ------------------------------------------------------------------
    // 2430 line adjudication (coordination of benefits, line level)
    // ------------------------------------------------------------------

    private static void Parse2430LineAdjudication(ParseState s, X12Segment seg)
    {
        // 2430 — another payer's adjudication of this line.
        var currentLine = s.CurrentLine!;
        decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var linePaid);
        s.CurrentLine = currentLine with
        {
            OtherPayerAdjudications =
            [
                .. currentLine.OtherPayerAdjudications ?? [],
                new LineOtherPayerAdjudication { PayerId = seg.Element(0), PaidAmount = linePaid },
            ]
        };
    }

    private static void Parse2430LineAdjustment(ParseState s, X12Segment seg)
    {
        // 2430 CAS — belongs to the SVD it follows.
        var currentLine = s.CurrentLine!;
        var adjudications = currentLine.OtherPayerAdjudications!;
        var last = adjudications[^1];
        s.CurrentLine = currentLine with
        {
            OtherPayerAdjudications =
            [
                .. adjudications.Take(adjudications.Count - 1),
                last with { Adjustments = [.. last.Adjustments, .. ParseCas(seg)] },
            ]
        };
    }

    // ------------------------------------------------------------------
    // Flush: close the open service line / claim
    // ------------------------------------------------------------------

    private static void FlushLine(ParseState s)
    {
        if (s.CurrentLine is not null)
        {
            s.ServiceLines.Add(s.CurrentLine);
            s.CurrentLine = null;
        }
    }

    private static void FlushClaim(ParseState s)
    {
        if (!s.ClaimOpen)
        {
            return;
        }
        FlushLine(s);

        s.Claims.Add(BuildClaim(s));
        ResetClaim(s);
    }

    private static X12837Claim BuildClaim(ParseState s) => new()
    {
        ClaimId = s.ClaimId,
        ClaimType = s.ClaimType,
        TransactionControlNumber = s.TransactionControlNumber,
        InterchangeControlNumber = s.InterchangeControlNumber ?? string.Empty,
        TransactionDate = s.TransactionDate,
        Submitter = s.Submitter ?? new ClaimSubmitter { Name = string.Empty, IdentificationCode = string.Empty, IdentificationQualifier = string.Empty },
        Receiver = s.Receiver ?? new ClaimReceiver { Name = string.Empty, IdentificationCode = string.Empty, IdentificationQualifier = string.Empty },
        BillingProvider = s.BillingProvider ?? new BillingProvider { Npi = string.Empty, Name = string.Empty, EntityType = string.Empty, Address = new ProviderAddress { Line1 = string.Empty, City = string.Empty, State = string.Empty, PostalCode = string.Empty } },
        PayToAddress = s.PayToAddress,
        PayToPlan = s.PayToPlan,
        TransactionTypeCode = s.TransactionTypeCode,
        Subscriber = s.Subscriber ?? new ClaimSubscriber { MemberId = string.Empty, FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty },
        Patient = s.Patient,
        ClaimHeader = new ClaimHeader
        {
            PatientControlNumber = s.ClaimId,
            TotalChargeAmount = s.TotalCharge,
            PlaceOfServiceCode = s.PlaceOfService,
            FrequencyCode = s.FrequencyCode,
            FacilityTypeCode = s.FacilityTypeCode,
            PriorAuthorizationNumber = s.PriorAuthorizationNumber,
            OriginalClaimReference = s.OriginalClaimReference,
            DiagnosisCodes = s.DiagnosisCodes.Count > 0 ? [.. s.DiagnosisCodes] : null,
            // The principal diagnosis is the ABK/BK composite when
            // present — not blindly the first HI element, which on
            // an 837I may be an admitting or reason-for-visit code.
            PrincipalDiagnosisCode = (s.DiagnosisCodes.FirstOrDefault(d => d.Qualifier is "ABK" or "BK")
                ?? s.DiagnosisCodes.FirstOrDefault())?.Code,
            AdmittingDiagnosisCode = s.DiagnosisCodes.FirstOrDefault(d => d.Qualifier is "ABJ" or "BJ")?.Code,
            RenderingProvider = s.PendingRenderingProvider,
            AdmissionDate = s.AdmissionDate,
            AdmissionHour = s.AdmissionHour,
            DischargeDate = s.DischargeDate,
            DischargeHour = s.DischargeHour,
            StatementFromDate = s.StatementFrom,
            StatementToDate = s.StatementTo,
            AdmissionTypeCode = s.AdmissionType,
            AdmissionSourceCode = s.AdmissionSource,
            PatientStatusCode = s.PatientStatus,
            DrgCode = s.DrgCode,
            PrincipalProcedure = s.PrincipalProcedure,
            OtherProcedures = s.OtherProcedures.Count > 0 ? [.. s.OtherProcedures] : null,
            OccurrenceCodes = s.OccurrenceCodes.Count > 0 ? [.. s.OccurrenceCodes] : null,
            OccurrenceSpanCodes = s.OccurrenceSpanCodes.Count > 0 ? [.. s.OccurrenceSpanCodes] : null,
            ValueCodes = s.ValueCodes.Count > 0 ? [.. s.ValueCodes] : null,
            ConditionCodes = s.ConditionCodes.Count > 0 ? [.. s.ConditionCodes] : null,
        },
        ServiceLines = [.. s.ServiceLines],
        TotalClaimedAmount = s.TotalCharge,
        ParsedAt = DateTime.UtcNow.ToString("o"),
        PayerResponsibilityCode = s.ClaimPayerResponsibility,
        OtherPayers = s.OtherPayers.Count > 0 ? [.. s.OtherPayers] : null,
        AttachmentReports = s.AttachmentReports.Count > 0 ? [.. s.AttachmentReports] : null,
    };

    // Per-claim state back to its initial values. Envelope, 1000A/B and
    // HL-level state (billing provider, pay-to, subscriber, patient)
    // carries over to the next claim.
    private static void ResetClaim(ParseState s)
    {
        s.ClaimPayerResponsibility = null;
        s.OtherPayers = [];

        s.ClaimId = string.Empty;
        s.TotalCharge = 0m;
        s.PlaceOfService = null;
        s.FrequencyCode = null;
        s.FacilityTypeCode = null;
        s.PriorAuthorizationNumber = null;
        s.OriginalClaimReference = null;
        s.AttachmentReports = [];
        s.DiagnosisCodes = [];
        s.AdmissionDate = null;
        s.AdmissionHour = null;
        s.DischargeDate = null;
        s.DischargeHour = null;
        s.StatementFrom = null;
        s.StatementTo = null;
        s.AdmissionType = null;
        s.AdmissionSource = null;
        s.PatientStatus = null;
        s.DrgCode = null;
        s.PrincipalProcedure = null;
        s.OtherProcedures = [];
        s.OccurrenceCodes = [];
        s.OccurrenceSpanCodes = [];
        s.ValueCodes = [];
        s.ConditionCodes = [];
        s.ServiceLines = [];
        s.PendingRenderingProvider = null;
        s.ClaimOpen = false;
    }

    // ------------------------------------------------------------------
    // Segment helpers
    // ------------------------------------------------------------------

    private static List<ClaimAdjustmentEntry> ParseCas(X12Segment seg)
    {
        // CAS01 group, then up to six (CARC, amount, quantity) triplets.
        var entries = new List<ClaimAdjustmentEntry>();
        var group = seg.Element(0) ?? string.Empty;
        for (var i = 1; i < seg.Elements.Count; i += 3)
        {
            var reason = seg.Element(i);
            if (string.IsNullOrEmpty(reason)) continue;
            if (!decimal.TryParse(seg.Element(i + 1), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) continue;
            entries.Add(new ClaimAdjustmentEntry { GroupCode = group, ReasonCode = reason, Amount = amount });
        }
        return entries;
    }

    // NM108/NM109 (id qualifier + id) are always the trailing pair of
    // an NM1 segment when present — but how many blank elements
    // precede them varies between generators in this codebase (e.g.
    // EncounterTransformer's rendering-provider NM1 omits one blank
    // slot other NM1s include), which shifts their absolute index.
    // Reading them relative to the end of the segment is robust to
    // that; reading by absolute position (Element(7)/Element(8)) is not.
    private static (string? qualifier, string? id) TrailingIdPair(X12Segment seg) =>
        seg.Elements.Count >= 2
            ? (seg.Elements[^2].Length > 0 ? seg.Elements[^2] : null, seg.Elements[^1].Length > 0 ? seg.Elements[^1] : null)
            : (null, null);

    private static string? At(string[] parts, int index) =>
        parts.Length > index && parts[index].Length > 0 ? parts[index] : null;

    // DT (CCYYMMDDHHMM) → date + hour; D8 → date only.
    private static (string? date, string? hour) SplitDateTime(string? format, string? value)
    {
        if (string.IsNullOrEmpty(value)) return (null, null);
        if (format == "DT" && value.Length >= 12) return (value[..8], value[8..12]);
        return (value.Length >= 8 ? value[..8] : value, null);
    }

    // RD8 (CCYYMMDD-CCYYMMDD) → from/to; a single D8 date is a one-day period.
    private static (string? from, string? to) SplitPeriod(string? format, string? value)
    {
        if (string.IsNullOrEmpty(value)) return (null, null);
        if (format == "RD8" && value.Contains('-'))
        {
            var range = value.Split('-', 2);
            return (range[0], range[1]);
        }
        return (value, value);
    }

    private static ProviderAddress EmptyAddress() =>
        new() { Line1 = string.Empty, City = string.Empty, State = string.Empty, PostalCode = string.Empty };

    private static ProviderAddress WithCityStateZip(ProviderAddress address, X12Segment seg) => address with
    {
        City = seg.Element(0) ?? string.Empty,
        State = seg.Element(1) ?? string.Empty,
        PostalCode = seg.Element(2) ?? string.Empty,
        CountryCode = seg.Element(3),
    };
}
