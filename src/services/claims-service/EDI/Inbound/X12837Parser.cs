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
        var componentSep = doc.ComponentSeparator;

        var claims = new List<X12837Claim>();

        string? interchangeControlNumber = null;
        string transactionControlNumber = string.Empty;
        string transactionDate = string.Empty;
        ClaimType claimType = ClaimType.Professional;

        ClaimSubmitter? submitter = null;
        ClaimReceiver? receiver = null;
        BillingProvider? billingProvider = null;
        ClaimSubscriber? subscriber = null;
        ClaimPatient? patient = null;
        RenderingProviderInfo? pendingRenderingProvider = null;

        var insideDependentLoop = false;
        string? lastNm1Context = null;

        // Per-claim accumulation, reset on CLM/flush.
        string claimId = string.Empty;
        decimal totalCharge = 0m;
        string? placeOfService = null;
        string? frequencyCode = null;
        string? facilityTypeCode = null;
        List<DiagnosisCode> diagnosisCodes = [];
        // Institutional (837I) 2300 detail, reset on CLM/flush.
        string? admissionDate = null;
        string? admissionHour = null;
        string? dischargeDate = null;
        string? dischargeHour = null;
        string? statementFrom = null;
        string? statementTo = null;
        string? admissionType = null;
        string? admissionSource = null;
        string? patientStatus = null;
        string? drgCode = null;
        InstitutionalCode? principalProcedure = null;
        List<InstitutionalCode> otherProcedures = [];
        List<InstitutionalCode> occurrenceCodes = [];
        List<InstitutionalCode> occurrenceSpanCodes = [];
        List<InstitutionalCode> valueCodes = [];
        List<InstitutionalCode> conditionCodes = [];
        List<ServiceLine> serviceLines = [];
        ServiceLine? currentLine = null;
        var claimOpen = false;

        // Coordination of benefits. SBR01 of the 2000B loop is the receiving
        // payer's responsibility sequence; it is captured at CLM (the claim's
        // own 2000B precedes it). After CLM and before the first LX, an SBR
        // opens loop 2320 (other subscriber) for another payer, whose 2330B
        // NM1*PR, AMT*D and CAS follow; within a line, SVD opens loop 2430
        // (that payer's line adjudication) and the CAS after it belong to it.
        string? subscriberPayerResponsibility = null;
        string? claimPayerResponsibility = null;
        var hlSinceClaim = false;
        List<OtherPayer> otherPayers = [];
        bool InOtherPayerLoop() => claimOpen && !hlSinceClaim && currentLine is null && otherPayers.Count > 0;

        static List<ClaimAdjustmentEntry> ParseCas(X12Segment seg)
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
        static (string? qualifier, string? id) TrailingIdPair(X12Segment seg) =>
            seg.Elements.Count >= 2
                ? (seg.Elements[^2].Length > 0 ? seg.Elements[^2] : null, seg.Elements[^1].Length > 0 ? seg.Elements[^1] : null)
                : (null, null);

        static string? At(string[] parts, int index) =>
            parts.Length > index && parts[index].Length > 0 ? parts[index] : null;

        // DT (CCYYMMDDHHMM) → date + hour; D8 → date only.
        static (string? date, string? hour) SplitDateTime(string? format, string? value)
        {
            if (string.IsNullOrEmpty(value)) return (null, null);
            if (format == "DT" && value.Length >= 12) return (value[..8], value[8..12]);
            return (value.Length >= 8 ? value[..8] : value, null);
        }

        // RD8 (CCYYMMDD-CCYYMMDD) → from/to; a single D8 date is a one-day period.
        static (string? from, string? to) SplitPeriod(string? format, string? value)
        {
            if (string.IsNullOrEmpty(value)) return (null, null);
            if (format == "RD8" && value.Contains('-'))
            {
                var range = value.Split('-', 2);
                return (range[0], range[1]);
            }
            return (value, value);
        }

        void FlushLine()
        {
            if (currentLine is not null)
            {
                serviceLines.Add(currentLine);
                currentLine = null;
            }
        }

        void FlushClaim()
        {
            if (!claimOpen)
            {
                return;
            }
            FlushLine();

            claims.Add(new X12837Claim
            {
                ClaimId = claimId,
                ClaimType = claimType,
                TransactionControlNumber = transactionControlNumber,
                InterchangeControlNumber = interchangeControlNumber ?? string.Empty,
                TransactionDate = transactionDate,
                Submitter = submitter ?? new ClaimSubmitter { Name = string.Empty, IdentificationCode = string.Empty, IdentificationQualifier = string.Empty },
                Receiver = receiver ?? new ClaimReceiver { Name = string.Empty, IdentificationCode = string.Empty, IdentificationQualifier = string.Empty },
                BillingProvider = billingProvider ?? new BillingProvider { Npi = string.Empty, Name = string.Empty, EntityType = string.Empty, Address = new ProviderAddress { Line1 = string.Empty, City = string.Empty, State = string.Empty, PostalCode = string.Empty } },
                Subscriber = subscriber ?? new ClaimSubscriber { MemberId = string.Empty, FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty },
                Patient = patient,
                ClaimHeader = new ClaimHeader
                {
                    PatientControlNumber = claimId,
                    TotalChargeAmount = totalCharge,
                    PlaceOfServiceCode = placeOfService,
                    FrequencyCode = frequencyCode,
                    FacilityTypeCode = facilityTypeCode,
                    DiagnosisCodes = diagnosisCodes.Count > 0 ? [.. diagnosisCodes] : null,
                    // The principal diagnosis is the ABK/BK composite when
                    // present — not blindly the first HI element, which on
                    // an 837I may be an admitting or reason-for-visit code.
                    PrincipalDiagnosisCode = (diagnosisCodes.FirstOrDefault(d => d.Qualifier is "ABK" or "BK")
                        ?? diagnosisCodes.FirstOrDefault())?.Code,
                    AdmittingDiagnosisCode = diagnosisCodes.FirstOrDefault(d => d.Qualifier is "ABJ" or "BJ")?.Code,
                    RenderingProvider = pendingRenderingProvider,
                    AdmissionDate = admissionDate,
                    AdmissionHour = admissionHour,
                    DischargeDate = dischargeDate,
                    DischargeHour = dischargeHour,
                    StatementFromDate = statementFrom,
                    StatementToDate = statementTo,
                    AdmissionTypeCode = admissionType,
                    AdmissionSourceCode = admissionSource,
                    PatientStatusCode = patientStatus,
                    DrgCode = drgCode,
                    PrincipalProcedure = principalProcedure,
                    OtherProcedures = otherProcedures.Count > 0 ? [.. otherProcedures] : null,
                    OccurrenceCodes = occurrenceCodes.Count > 0 ? [.. occurrenceCodes] : null,
                    OccurrenceSpanCodes = occurrenceSpanCodes.Count > 0 ? [.. occurrenceSpanCodes] : null,
                    ValueCodes = valueCodes.Count > 0 ? [.. valueCodes] : null,
                    ConditionCodes = conditionCodes.Count > 0 ? [.. conditionCodes] : null,
                },
                ServiceLines = [.. serviceLines],
                TotalClaimedAmount = totalCharge,
                ParsedAt = DateTime.UtcNow.ToString("o"),
                PayerResponsibilityCode = claimPayerResponsibility,
                OtherPayers = otherPayers.Count > 0 ? [.. otherPayers] : null,
            });

            claimPayerResponsibility = null;
            otherPayers = [];

            claimId = string.Empty;
            totalCharge = 0m;
            placeOfService = null;
            frequencyCode = null;
            facilityTypeCode = null;
            diagnosisCodes = [];
            admissionDate = null;
            admissionHour = null;
            dischargeDate = null;
            dischargeHour = null;
            statementFrom = null;
            statementTo = null;
            admissionType = null;
            admissionSource = null;
            patientStatus = null;
            drgCode = null;
            principalProcedure = null;
            otherProcedures = [];
            occurrenceCodes = [];
            occurrenceSpanCodes = [];
            valueCodes = [];
            conditionCodes = [];
            serviceLines = [];
            pendingRenderingProvider = null;
            claimOpen = false;
        }

        foreach (var seg in doc.Segments)
        {
            switch (seg.Id)
            {
                case "ISA":
                    interchangeControlNumber = seg.Element(12);
                    break;

                case "ST":
                    transactionControlNumber = seg.Element(1) ?? string.Empty;
                    var version = seg.Element(2);
                    claimType = version switch
                    {
                        not null when version.Contains("223") => ClaimType.Institutional,
                        not null when version.Contains("224") => ClaimType.Dental,
                        _ => ClaimType.Professional
                    };
                    break;

                case "BHT":
                    transactionDate = seg.Element(3) ?? transactionDate;
                    break;

                case "HL":
                    hlSinceClaim = true;
                    var levelCode = seg.Element(2);
                    if (levelCode == "23")
                    {
                        insideDependentLoop = true;
                        patient = new ClaimPatient { FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty, RelationshipCode = string.Empty };
                    }
                    else if (levelCode == "22")
                    {
                        insideDependentLoop = false;
                        patient = null;
                    }
                    break;

                case "PAT":
                    if (patient is not null)
                    {
                        patient = patient with { RelationshipCode = seg.Element(0) ?? string.Empty };
                    }
                    break;

                case "SBR" when claimOpen && !hlSinceClaim && currentLine is null:
                    // Loop 2320 — another payer on this claim.
                    otherPayers.Add(new OtherPayer { PayerResponsibilityCode = seg.Element(0) ?? string.Empty });
                    lastNm1Context = null;
                    break;

                case "SBR":
                    subscriberPayerResponsibility = seg.Element(0);
                    if (subscriber is not null && !insideDependentLoop)
                    {
                        subscriber = subscriber with { GroupNumber = seg.Element(2) };
                    }
                    break;

                case "NM1" when InOtherPayerLoop():
                {
                    // Loops 2330A–I (other subscriber, other payer and its
                    // providers): only 2330B NM1*PR is kept. Marked so the
                    // N3/N4/REF/DMG handlers below never read these as the
                    // claim's own billing provider or subscriber.
                    lastNm1Context = "2330:" + seg.Element(0);
                    if (seg.Element(0) == "PR")
                    {
                        var (_, payerId) = TrailingIdPair(seg);
                        otherPayers[^1] = otherPayers[^1] with { PayerName = seg.Element(2), PayerId = payerId };
                    }
                    break;
                }

                case "REF" when InOtherPayerLoop() && lastNm1Context == "2330:PR":
                {
                    // 2330B REF*2U (payer identification number) / REF*FY
                    // (claim office number): other identifiers a 2430 SVD01
                    // may name the payer by.
                    var qualifier = seg.Element(0);
                    var value = seg.Element(1)?.Trim();
                    if (qualifier is "2U" or "FY" && !string.IsNullOrEmpty(value))
                    {
                        otherPayers[^1] = otherPayers[^1] with
                        {
                            AdditionalPayerIds = [.. otherPayers[^1].AdditionalPayerIds, value]
                        };
                    }
                    break;
                }

                case "AMT" when InOtherPayerLoop() && seg.Element(0) == "D":
                    // 2320 AMT*D — payer paid amount.
                    if (decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var payerPaid))
                    {
                        otherPayers[^1] = otherPayers[^1] with { PaidAmount = payerPaid };
                    }
                    break;

                case "CAS" when InOtherPayerLoop():
                    // 2320 CAS — claim-level adjustments by this payer.
                    otherPayers[^1] = otherPayers[^1] with
                    {
                        ClaimAdjustments = [.. otherPayers[^1].ClaimAdjustments, .. ParseCas(seg)]
                    };
                    break;

                case "SVD" when claimOpen && currentLine is not null:
                {
                    // 2430 — another payer's adjudication of this line.
                    decimal.TryParse(seg.Element(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var linePaid);
                    currentLine = currentLine with
                    {
                        OtherPayerAdjudications =
                        [
                            .. currentLine.OtherPayerAdjudications ?? [],
                            new LineOtherPayerAdjudication { PayerId = seg.Element(0), PaidAmount = linePaid },
                        ]
                    };
                    break;
                }

                case "CAS" when claimOpen && currentLine is { OtherPayerAdjudications.Count: > 0 }:
                {
                    // 2430 CAS — belongs to the SVD it follows.
                    var adjudications = currentLine.OtherPayerAdjudications!;
                    var last = adjudications[^1];
                    currentLine = currentLine with
                    {
                        OtherPayerAdjudications =
                        [
                            .. adjudications.Take(adjudications.Count - 1),
                            last with { Adjustments = [.. last.Adjustments, .. ParseCas(seg)] },
                        ]
                    };
                    break;
                }

                case "DMG" when InOtherPayerLoop():
                    break;

                case "NM1":
                    lastNm1Context = seg.Element(0);
                    switch (lastNm1Context)
                    {
                        case "41":
                        {
                            var (qual, id) = TrailingIdPair(seg);
                            submitter = new ClaimSubmitter
                            {
                                Name = seg.Element(2) ?? string.Empty,
                                IdentificationQualifier = qual ?? string.Empty,
                                IdentificationCode = id ?? string.Empty
                            };
                            break;
                        }

                        case "40":
                        {
                            var (qual, id) = TrailingIdPair(seg);
                            receiver = new ClaimReceiver
                            {
                                Name = seg.Element(2) ?? string.Empty,
                                IdentificationQualifier = qual ?? string.Empty,
                                IdentificationCode = id ?? string.Empty
                            };
                            break;
                        }

                        case "85":
                        {
                            var (_, npi) = TrailingIdPair(seg);
                            billingProvider = new BillingProvider
                            {
                                Npi = npi ?? string.Empty,
                                Name = seg.Element(2) ?? string.Empty,
                                EntityType = seg.Element(1) ?? string.Empty,
                                Address = new ProviderAddress { Line1 = string.Empty, City = string.Empty, State = string.Empty, PostalCode = string.Empty }
                            };
                            break;
                        }

                        case "IL":
                        {
                            var (_, memberId) = TrailingIdPair(seg);
                            subscriber = new ClaimSubscriber
                            {
                                MemberId = memberId ?? string.Empty,
                                FirstName = seg.Element(3) ?? string.Empty,
                                LastName = seg.Element(2) ?? string.Empty,
                                MiddleName = seg.Element(4),
                                DateOfBirth = string.Empty
                            };
                            break;
                        }

                        case "QC" when insideDependentLoop:
                        {
                            var (_, dependentMemberId) = TrailingIdPair(seg);
                            patient = (patient ?? new ClaimPatient { FirstName = string.Empty, LastName = string.Empty, DateOfBirth = string.Empty, RelationshipCode = string.Empty }) with
                            {
                                MemberId = dependentMemberId,
                                FirstName = seg.Element(3) ?? string.Empty,
                                LastName = seg.Element(2) ?? string.Empty,
                                MiddleName = seg.Element(4)
                            };
                            break;
                        }

                        case "82":
                        {
                            var (_, npi) = TrailingIdPair(seg);
                            pendingRenderingProvider = new RenderingProviderInfo
                            {
                                Npi = npi ?? string.Empty,
                                Name = $"{seg.Element(3)} {seg.Element(2)}".Trim()
                            };
                            break;
                        }
                    }
                    break;

                case "N3" when lastNm1Context == "85" && billingProvider is not null:
                    billingProvider = billingProvider with
                    {
                        Address = billingProvider.Address with { Line1 = seg.Element(0) ?? string.Empty, Line2 = seg.Element(1) }
                    };
                    break;

                case "N4" when lastNm1Context == "85" && billingProvider is not null:
                    billingProvider = billingProvider with
                    {
                        Address = billingProvider.Address with { City = seg.Element(0) ?? string.Empty, State = seg.Element(1) ?? string.Empty, PostalCode = seg.Element(2) ?? string.Empty }
                    };
                    break;

                case "REF" when lastNm1Context == "85" && billingProvider is not null && seg.Element(0) == "EI":
                    billingProvider = billingProvider with { TaxIdQualifier = "EI", TaxId = seg.Element(1) };
                    break;

                case "DMG":
                    if (insideDependentLoop && patient is not null)
                    {
                        patient = patient with { DateOfBirth = seg.Element(1) ?? string.Empty, Gender = seg.Element(2) };
                    }
                    else if (subscriber is not null)
                    {
                        subscriber = subscriber with { DateOfBirth = seg.Element(1) ?? string.Empty, Gender = seg.Element(2) };
                    }
                    break;

                case "CLM":
                    FlushClaim();
                    claimOpen = true;
                    hlSinceClaim = false;
                    claimPayerResponsibility = subscriberPayerResponsibility;
                    claimId = seg.Element(0) ?? string.Empty;
                    decimal.TryParse(seg.Element(1), out totalCharge);
                    var clmComposite = seg.Element(4) is { } c4 ? X12Tokenizer.SplitComponents(c4, componentSep) : [];
                    placeOfService = clmComposite.Length > 0 && clmComposite[0].Length > 0 ? clmComposite[0] : null;
                    frequencyCode = clmComposite.Length > 2 && clmComposite[2].Length > 0 ? clmComposite[2] : null;
                    // On an 837I, CLM05-1 is the facility type code (first
                    // two digits of the type of bill), not a place of
                    // service. PlaceOfServiceCode keeps carrying it for
                    // backward compatibility; FacilityTypeCode names it.
                    facilityTypeCode = claimType == ClaimType.Institutional ? placeOfService : null;
                    break;

                case "CL1" when claimOpen:
                    admissionType = seg.Element(0) is { Length: > 0 } cl101 ? cl101 : null;
                    admissionSource = seg.Element(1) is { Length: > 0 } cl102 ? cl102 : null;
                    patientStatus = seg.Element(2) is { Length: > 0 } cl103 ? cl103 : null;
                    break;

                case "HI" when claimOpen:
                    // HI0x composite: -1 qualifier, -2 code, -3 date format
                    // (D8/RD8), -4 date or period, -5 amount, -9 present-on-
                    // admission indicator. On an 837I the qualifier decides
                    // what the code is; only diagnosis qualifiers become
                    // DiagnosisCodes (unrecognized qualifiers stay
                    // diagnoses, as before, so nothing is silently dropped).
                    foreach (var element in seg.Elements)
                    {
                        if (element.Length == 0) continue;
                        var parts = X12Tokenizer.SplitComponents(element, componentSep);
                        if (parts.Length < 2) continue;
                        var qualifier = parts[0];
                        switch (qualifier)
                        {
                            case "DR":
                                drgCode = parts[1].Length > 0 ? parts[1] : null;
                                break;

                            case "BBR" or "BR" or "CAH":
                            {
                                var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                                principalProcedure = new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date };
                                break;
                            }

                            case "BBQ" or "BQ":
                            {
                                var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                                otherProcedures.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date });
                                break;
                            }

                            case "BH":
                            {
                                var (date, _) = SplitDateTime(At(parts, 2), At(parts, 3));
                                occurrenceCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = date });
                                break;
                            }

                            case "BI":
                            {
                                var (from, to) = SplitPeriod(At(parts, 2), At(parts, 3));
                                occurrenceSpanCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1], Date = from, DateEnd = to });
                                break;
                            }

                            case "BE":
                                valueCodes.Add(new InstitutionalCode
                                {
                                    Qualifier = qualifier,
                                    Code = parts[1],
                                    Amount = decimal.TryParse(At(parts, 4), NumberStyles.Number, CultureInfo.InvariantCulture, out var valueAmount)
                                        ? valueAmount
                                        : null
                                });
                                break;

                            case "BG":
                                conditionCodes.Add(new InstitutionalCode { Qualifier = qualifier, Code = parts[1] });
                                break;

                            case "TC":
                                // Treatment codes (therapy plan) — not modeled.
                                break;

                            default:
                                diagnosisCodes.Add(new DiagnosisCode
                                {
                                    Qualifier = qualifier,
                                    Code = parts[1],
                                    Pointer = diagnosisCodes.Count + 1,
                                    PresentOnAdmission = At(parts, 8)
                                });
                                break;
                        }
                    }
                    break;

                case "LX" when claimOpen:
                    FlushLine();
                    int.TryParse(seg.Element(0), out var lineNumber);
                    currentLine = new ServiceLine { LineNumber = lineNumber, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
                    break;

                case "SV1" when claimOpen:
                    currentLine ??= new ServiceLine { LineNumber = serviceLines.Count + 1, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
                    var procComposite = seg.Element(0) is { } c0 ? X12Tokenizer.SplitComponents(c0, componentSep) : [];
                    decimal.TryParse(seg.Element(1), out var chargeAmount);
                    decimal.TryParse(seg.Element(3), out var units);
                    // SV107 (diagnosis pointer composite) is index 6 in a
                    // spec-correct SV1 with SV105 place-of-service present;
                    // EncounterTransformer's simplified SV1 omits SV105/106
                    // entirely and puts it at index 5 instead. Prefer 6,
                    // fall back to 5 — see the class doc comment.
                    var pointerRaw = seg.Element(6) ?? seg.Element(5);
                    var pointers = pointerRaw is { } pr
                        ? X12Tokenizer.SplitComponents(pr, componentSep)
                            .Select(p => int.TryParse(p, out var n) ? n : (int?)null)
                            .Where(n => n.HasValue)
                            .Select(n => n!.Value)
                            .ToList()
                        : null;

                    currentLine = currentLine with
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
                    break;

                case "SV2" when claimOpen:
                {
                    // Institutional service line — a genuinely different
                    // layout from SV1, not just an index shift: revenue
                    // code occupies index 0 (SV1 has no equivalent slot),
                    // which pushes the procedure composite to index 1.
                    // SV2*{revenueCode}*HC:{proc}{mods}*{charge}*UN*{units}
                    currentLine ??= new ServiceLine { LineNumber = serviceLines.Count + 1, ProcedureCode = string.Empty, ServiceDate = string.Empty, Units = 1 };
                    var sv2Proc = seg.Element(1) is { } c1 ? X12Tokenizer.SplitComponents(c1, componentSep) : [];
                    decimal.TryParse(seg.Element(2), out var sv2Charge);
                    decimal.TryParse(seg.Element(4), out var sv2Units);
                    // Not present in every institutional file (this repo's
                    // own FMMIS generator omits it), so no fallback chain
                    // like SV1's — absent means absent.
                    var sv2PointerRaw = seg.Element(6);
                    var sv2Pointers = sv2PointerRaw is { } spr
                        ? X12Tokenizer.SplitComponents(spr, componentSep)
                            .Select(p => int.TryParse(p, out var n) ? n : (int?)null)
                            .Where(n => n.HasValue)
                            .Select(n => n!.Value)
                            .ToList()
                        : null;

                    currentLine = currentLine with
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
                    break;
                }

                case "DTP" when claimOpen && currentLine is null:
                {
                    // 2300 claim-level dates (before the first LX).
                    var format = seg.Element(1);
                    var value = seg.Element(2);
                    switch (seg.Element(0))
                    {
                        case "435": // Admission date/hour (DT or D8)
                            (admissionDate, admissionHour) = SplitDateTime(format, value);
                            break;

                        case "096": // Discharge hour (TM). Tolerates a D8/DT
                                    // date, which EncounterTransformer emits.
                            if (format == "TM")
                            {
                                dischargeHour = string.IsNullOrEmpty(value) ? null : value;
                            }
                            else
                            {
                                var (date, hour) = SplitDateTime(format, value);
                                dischargeDate = date;
                                dischargeHour = hour ?? dischargeHour;
                            }
                            break;

                        case "434": // Statement covers period (RD8)
                            (statementFrom, statementTo) = SplitPeriod(format, value);
                            break;
                    }
                    break;
                }

                case "LIN" when claimOpen && currentLine is not null && seg.Element(1) == "N4":
                    // 2410 drug identification: LIN**N4*{11-digit NDC}
                    currentLine = currentLine with { NationalDrugCode = seg.Element(2) is { Length: > 0 } ndc ? ndc : null };
                    break;

                case "CTP" when claimOpen && currentLine is { NationalDrugCode: not null }:
                {
                    // 2410 drug quantity: CTP****{quantity}*{unit composite}
                    var unitComposite = seg.Element(4) is { } ctp05 ? X12Tokenizer.SplitComponents(ctp05, componentSep) : [];
                    currentLine = currentLine with
                    {
                        DrugQuantity = decimal.TryParse(seg.Element(3), NumberStyles.Number, CultureInfo.InvariantCulture, out var drugQuantity)
                            ? drugQuantity
                            : null,
                        DrugUnitOfMeasure = At(unitComposite, 0)
                    };
                    break;
                }

                case "DTP" when claimOpen && currentLine is not null && seg.Element(0) == "472":
                    var dateQualifier = seg.Element(1);
                    var dateValue = seg.Element(2) ?? string.Empty;
                    if (dateQualifier == "RD8" && dateValue.Contains('-'))
                    {
                        var range = dateValue.Split('-', 2);
                        currentLine = currentLine with { ServiceDate = range[0], ServiceDateEnd = range[1] };
                    }
                    else
                    {
                        currentLine = currentLine with { ServiceDate = dateValue };
                    }
                    break;

                case "SE":
                    FlushClaim();
                    break;
            }
        }

        // Safety net for a file missing its trailing SE.
        FlushClaim();

        return claims;
    }
}
