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
    public static List<X12837Claim> Parse(string ediContent)
    {
        var doc = X12Tokenizer.Tokenize(ediContent);
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
        List<DiagnosisCode> diagnosisCodes = [];
        List<ServiceLine> serviceLines = [];
        ServiceLine? currentLine = null;
        var claimOpen = false;

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
                    DiagnosisCodes = diagnosisCodes.Count > 0 ? [.. diagnosisCodes] : null,
                    PrincipalDiagnosisCode = diagnosisCodes.Count > 0 ? diagnosisCodes[0].Code : null,
                    RenderingProvider = pendingRenderingProvider
                },
                ServiceLines = [.. serviceLines],
                TotalClaimedAmount = totalCharge,
                ParsedAt = DateTime.UtcNow.ToString("o")
            });

            claimId = string.Empty;
            totalCharge = 0m;
            placeOfService = null;
            frequencyCode = null;
            diagnosisCodes = [];
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

                case "SBR":
                    if (subscriber is not null && !insideDependentLoop)
                    {
                        subscriber = subscriber with { GroupNumber = seg.Element(2) };
                    }
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
                    claimId = seg.Element(0) ?? string.Empty;
                    decimal.TryParse(seg.Element(1), out totalCharge);
                    var clmComposite = seg.Element(4) is { } c4 ? X12Tokenizer.SplitComponents(c4, componentSep) : [];
                    placeOfService = clmComposite.Length > 0 && clmComposite[0].Length > 0 ? clmComposite[0] : null;
                    frequencyCode = clmComposite.Length > 2 && clmComposite[2].Length > 0 ? clmComposite[2] : null;
                    break;

                case "HI" when claimOpen:
                    foreach (var element in seg.Elements)
                    {
                        if (element.Length == 0) continue;
                        var parts = X12Tokenizer.SplitComponents(element, componentSep);
                        if (parts.Length < 2) continue;
                        diagnosisCodes.Add(new DiagnosisCode
                        {
                            Qualifier = parts[0],
                            Code = parts[1],
                            Pointer = diagnosisCodes.Count + 1
                        });
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
                        ProcedureCodeQualifier = sv2Proc.Length > 0 ? sv2Proc[0] : null,
                        ProcedureCode = sv2Proc.Length > 1 ? sv2Proc[1] : string.Empty,
                        Modifiers = sv2Proc.Length > 2 ? [.. sv2Proc[2..].Where(m => m.Length > 0)] : null,
                        ChargeAmount = sv2Charge,
                        UnitType = seg.Element(3),
                        Units = sv2Units,
                        DiagnosisPointers = sv2Pointers is { Count: > 0 } ? sv2Pointers : null
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
