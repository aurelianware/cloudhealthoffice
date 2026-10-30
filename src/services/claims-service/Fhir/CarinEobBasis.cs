using System.Text.Json.Nodes;
using ClaimsService.Models;
using CloudHealthOffice.Infrastructure.ReferenceData;

namespace ClaimsService.Fhir;

/// <summary>
/// The CARIN Blue Button <c>ExplanationOfBenefit</c> shaping both of CHO's EOB
/// projections share.
///
/// <para>
/// CARIN BB splits each claim type into two profiles: a <c>-Basis</c> profile
/// carrying the clinical and administrative record of an encounter, and a
/// financial profile whose <c>baseDefinition</c> is that Basis profile and
/// which adds the plan's money. Provider Access sends the first and Patient
/// Access the second, so CHO serves both — from one shaping, because the
/// financial profile requires everything the Basis profile does.
/// </para>
///
/// <para>
/// What is NOT here is anything monetary. The Basis profiles forbid it and the
/// financial ones require it, so it belongs to the one projector that may emit
/// it, through the callbacks <see cref="Scaffold"/> takes.
/// </para>
/// </summary>
internal static class CarinEobBasis
{
    internal const string ProfileBase = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/";

    internal const string ProfessionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Professional-NonClinician-Basis";
    internal const string InpatientInstitutionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Inpatient-Institutional-Basis";
    internal const string OutpatientInstitutionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Outpatient-Institutional-Basis";
    internal const string OralBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Oral-Basis";

    internal const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";

    /// <summary>
    /// The institutional and oral profiles fix <c>type</c> to a pattern that
    /// includes the code system's version, so a coding without it does not match
    /// and the validator rejects the instance. This is the version CARIN BB
    /// 2.2.0 pins, not a version CHO chose.
    /// </summary>
    internal const string ClaimTypeSystemVersion = "1.0.1";
    internal const string ExDiagnosisTypeSystem = "http://terminology.hl7.org/CodeSystem/ex-diagnosistype";
    internal const string ClaimCareTeamRoleSystem = "http://terminology.hl7.org/CodeSystem/claimcareteamrole";
    internal const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    internal const string Cpt4System = "http://www.ama-assn.org/go/cpt";
    internal const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    internal const string RevenueCodeSystem = "https://www.nubc.org/CodeSystem/RevenueCodes";
    internal const string TypeOfBillSystem = "https://www.nubc.org/CodeSystem/TypeOfBill";

    internal const string C4BbBase = "http://hl7.org/fhir/us/carin-bb/CodeSystem/";
    internal const string IdentifierTypeSystem = C4BbBase + "C4BBIdentifierType";
    internal const string ClaimDiagnosisTypeSystem = C4BbBase + "C4BBClaimDiagnosisType";
    internal const string InstitutionalSubTypeSystem = C4BbBase + "C4BBInstitutionalClaimSubType";
    internal const string AdjudicationDiscriminatorSystem = C4BbBase + "C4BBAdjudicationDiscriminator";
    internal const string PayerAdjudicationStatusSystem = C4BbBase + "C4BBPayerAdjudicationStatus";
    internal const string SupportingInfoTypeSystem = C4BbBase + "C4BBSupportingInfoType";
    internal const string CareTeamRoleSystem = C4BbBase + "C4BBClaimCareTeamRole";

    /// <summary>The plan's own name, used as the insurer's display.</summary>
    internal const string InsurerDisplay = "CloudHealthOffice";

    /// <summary>
    /// Everything the Basis profiles require, which is everything the financial
    /// profiles require too. The callbacks are the seams the financial
    /// projection adds money through; a non-financial projection passes none.
    /// </summary>
    internal static JsonObject Scaffold(
        Claim claim,
        ProfileShape shape,
        Action<JsonObject, ClaimLine>? augmentItem = null,
        Action<JsonArray>? augmentSupportingInfo = null,
        Action<JsonArray>? augmentHeaderAdjudication = null)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // Same chain-stable id as the Claim and EOB projections of this record,
        // so all three name one claim across the three resource types.
        var fhirId = string.IsNullOrEmpty(claim.ClaimVersionId) ? claim.Id : claim.ClaimVersionId;

        var eob = new JsonObject
        {
            ["resourceType"] = "ExplanationOfBenefit",
            ["id"] = fhirId,
            ["meta"] = new JsonObject
            {
                ["lastUpdated"] = AsUtc(claim.LastUpdatedDate).ToString("o"),
                ["profile"] = new JsonArray { shape.Profile },
            },
            // identifier:uniqueclaimid — the slice C4BB requires, discriminated
            // on type. "uc" is C4BB's "unique claim identifier".
            ["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = Concept(IdentifierTypeSystem, "uc", "Unique Claim ID"),
                    ["system"] = "urn:cho:claim-number",
                    ["value"] = string.IsNullOrWhiteSpace(claim.ClaimNumber) ? fhirId : claim.ClaimNumber,
                },
            },
            ["status"] = MapStatus(claim.Status),
            ["type"] = ClaimTypeConcept(shape.ClaimTypeCode),
            ["use"] = "claim",
            ["patient"] = new JsonObject { ["reference"] = $"Patient/{claim.MemberId}" },
            ["billablePeriod"] = BillablePeriod(claim),
            ["created"] = AsUtc(claim.SubmittedDate).ToString("o"),
            ["insurer"] = new JsonObject { ["display"] = InsurerDisplay },
            ["provider"] = ProviderReference(claim.BillingProviderNPI, claim.BillingProviderName),
            ["outcome"] = MapOutcome(claim.Status),
        };

        if (shape.SubTypeCode is { } subType)
            eob["subType"] = Concept(InstitutionalSubTypeSystem, subType, null);

        var careTeam = CareTeam(claim, shape);
        if (careTeam.Count > 0)
            eob["careTeam"] = careTeam;

        var supportingInfo = SupportingInfo(claim, shape);
        augmentSupportingInfo?.Invoke(supportingInfo);
        if (supportingInfo.Count > 0)
            eob["supportingInfo"] = supportingInfo;

        eob["diagnosis"] = Diagnoses(claim, shape);
        eob["insurance"] = Insurance(claim);
        eob["item"] = Items(claim, shape, augmentItem);

        // Header-level adjudication. The institutional profiles require
        // benefitpaymentstatus here; the professional and oral profiles require
        // it on each item instead, which Items() handles.
        var headerAdjudication = HeaderAdjudication(claim, shape);
        augmentHeaderAdjudication?.Invoke(headerAdjudication);
        if (headerAdjudication.Count > 0)
            eob["adjudication"] = headerAdjudication;

        return eob;
    }


    // ── profile selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything that differs between the four Basis profiles, resolved once so
    /// the body of the projection reads the same for all of them.
    /// </summary>
    internal sealed record ProfileShape(
        string Profile,
        string ClaimTypeCode,
        string? SubTypeCode,
        bool IsInstitutional,
        bool ServicedMustBeDate,
        bool RequiresHeaderRenderingNetworkStatus,
        string SecondaryDiagnosisCode,
        string DefaultCareTeamRoleSystem,
        string DefaultCareTeamRole,
        bool Financial);

    /// <summary>
    /// Resolves the profile shape for a claim.
    ///
    /// <para>
    /// <paramref name="financial"/> selects between the two halves of the same
    /// family. CARIN BB names each financial profile and its non-financial
    /// counterpart identically but for a <c>-Basis</c> suffix, and the Basis
    /// profile is the financial one's <c>baseDefinition</c>: everything the
    /// Basis profile requires, the financial profile requires too, and it adds
    /// the money. So one shaping serves both, and the suffix is the only thing
    /// that differs about which profile is declared.
    /// </para>
    /// </summary>
    internal static ProfileShape Shape(Claim claim, bool financial) => claim.ClaimType switch
    {
        ClaimType.Institutional when IsInpatient(claim) => new ProfileShape(
            Canonical(InpatientInstitutionalBasisProfile, financial),
            "institutional", "inpatient", true, false, false,
            // The inpatient profile's diagnosis-type value set has no
            // "secondary": a non-principal diagnosis is "other".
            "other", CareTeamRoleSystem, "attending", financial),

        ClaimType.Institutional => new ProfileShape(
            Canonical(OutpatientInstitutionalBasisProfile, financial),
            "institutional", "outpatient", true, true, false,
            "other", CareTeamRoleSystem, "attending", financial),

        ClaimType.Dental => new ProfileShape(
            Canonical(OralBasisProfile, financial),
            "oral", null, false, false, true,
            "secondary", CareTeamRoleSystem, "rendering", financial),

        _ => new ProfileShape(
            Canonical(ProfessionalBasisProfile, financial),
            "professional", null, false, false, false,
            "secondary", CareTeamRoleSystem, "rendering", financial),
    };

    private const string BasisSuffix = "-Basis";

    private static string Canonical(string basisProfile, bool financial) =>
        financial ? basisProfile[..^BasisSuffix.Length] : basisProfile;

    /// <summary>
    /// Whether an institutional claim is inpatient, from the UB-04 type of
    /// bill. CHO stores the first two digits in
    /// <see cref="InstitutionalClaimDetails.FacilityTypeCode"/>: the first is
    /// the facility type and the second the bill classification, where 1 and 2
    /// are inpatient (Part A and Part B) and 3 and up are outpatient and other.
    ///
    /// <para>
    /// A claim with no institutional detail at all is treated as outpatient. It
    /// is the weaker claim of the two — the inpatient profile additionally
    /// requires an admission period — so an unknown bill classification must
    /// not land on it.
    /// </para>
    /// </summary>
    internal static bool IsInpatient(Claim claim)
    {
        var facilityType = claim.Institutional?.FacilityTypeCode;
        if (facilityType is null || facilityType.Length < 2)
            return false;

        return facilityType[1] is '1' or '2';
    }

    // ── header ───────────────────────────────────────────────────────────────

    internal static JsonObject BillablePeriod(Claim claim)
    {
        var period = new JsonObject
        {
            ["start"] = AsUtc(claim.ServiceDateFrom).ToString("yyyy-MM-dd"),
        };
        if (claim.ServiceDateTo >= claim.ServiceDateFrom)
            period["end"] = AsUtc(claim.ServiceDateTo).ToString("yyyy-MM-dd");
        return period;
    }

    /// <summary>
    /// A provider reference by NPI.
    ///
    /// <para>
    /// <paramref name="resourceType"/> is given only when CHO can say which
    /// kind of resource the NPI denotes. An 837's rendering provider is an
    /// individual by definition, so it gets a literal
    /// <c>Practitioner/{npi}</c> — provider-service keys its
    /// <c>us-core-practitioner</c> projection by NPI and fhir-service serves
    /// it from the same <c>fhir/r4</c> base as this EOB, so the relative
    /// reference resolves against a live server. A billing NPI may belong to a
    /// group, and CHO does not know which from the claim alone, so it stays an
    /// identifier-and-display reference rather than asserting a type that may
    /// 404.
    /// </para>
    /// </summary>
    internal static JsonObject ProviderReference(string? npi, string? name, string? resourceType = null)
    {
        var reference = new JsonObject();
        if (!string.IsNullOrWhiteSpace(npi))
        {
            if (resourceType is not null)
                reference["reference"] = $"{resourceType}/{npi}";
            reference["identifier"] = new JsonObject
            {
                ["system"] = NpiSystem,
                ["value"] = npi,
            };
        }
        if (!string.IsNullOrWhiteSpace(name))
            reference["display"] = name;

        // ref-1: a reference must carry something. A claim with neither an NPI
        // nor a name is a data defect upstream, not a reason to emit {}.
        if (reference.Count == 0)
            reference["display"] = "Unknown provider";

        return reference;
    }

    /// <summary>
    /// The care team. Only the rendering provider goes here, and deliberately
    /// not the billing provider:
    ///
    /// <list type="bullet">
    ///   <item><description>The billing provider is already the header's
    ///     <c>provider</c>, where C4BB allows an <c>Organization</c> as well as
    ///     a <c>Practitioner</c>. Repeating it on the care team would add
    ///     nothing.</description></item>
    ///   <item><description>Worse, it would assert something CHO does not know.
    ///     Every care-team role but <c>purchasedservice</c> trips C4BB's
    ///     invariant that the provider resolve to a <c>Practitioner</c>, and a
    ///     billing NPI may belong to a group. The 837's rendering provider is an
    ///     individual by definition.</description></item>
    /// </list>
    ///
    /// <para>
    /// The rendering provider therefore carries a literal
    /// <c>Practitioner/{npi}</c> reference, which provider-service serves. The
    /// validator still reports the invariant against a golden, because a
    /// single-file instance gives <c>resolve()</c> nothing to find; that
    /// standalone-only residue is recorded in
    /// <c>conformance/validator-baseline.txt</c>.
    /// </para>
    /// </summary>
    internal static JsonArray CareTeam(Claim claim, ProfileShape shape)
    {
        var careTeam = new JsonArray();

        if (!string.IsNullOrWhiteSpace(claim.RenderingProviderNPI))
        {
            careTeam.Add(new JsonObject
            {
                ["sequence"] = 1,
                ["provider"] = ProviderReference(claim.RenderingProviderNPI, claim.RenderingProviderName, "Practitioner"),
                ["role"] = Concept(shape.DefaultCareTeamRoleSystem, shape.DefaultCareTeamRole, null),
            });
        }

        return careTeam;
    }

    internal static JsonArray SupportingInfo(Claim claim, ProfileShape shape)
    {
        var entries = new JsonArray();
        var sequence = 1;

        if (claim.ReceivedDate is { } received)
        {
            entries.Add(new JsonObject
            {
                ["sequence"] = sequence++,
                ["category"] = Concept(SupportingInfoTypeSystem, "clmrecvddate", null),
                ["timingDate"] = AsUtc(received).ToString("yyyy-MM-dd"),
            });
        }

        if (!shape.IsInstitutional)
            return entries;

        if (claim.TypeOfBill is { Length: > 0 } typeOfBill)
        {
            entries.Add(new JsonObject
            {
                ["sequence"] = sequence++,
                ["category"] = Concept(SupportingInfoTypeSystem, "typeofbill", null),
                ["code"] = Concept(TypeOfBillSystem, typeOfBill, null),
            });
        }

        // The inpatient profile requires an admission period. The 837I carries
        // the admission date; for a discharged inpatient the discharge date is
        // the statement-to date, which is what Claim's own helper resolves.
        if (shape.SubTypeCode == "inpatient")
        {
            var admission = claim.Institutional?.AdmissionDate;
            var discharge = claim.Institutional?.DischargeDate
                ?? claim.Institutional?.StatementToDate;

            if (admission is { } admitted)
            {
                var period = new JsonObject
                {
                    ["start"] = AsUtc(admitted).ToString("o"),
                };
                if (discharge is { } discharged && discharged >= admitted)
                    period["end"] = AsUtc(discharged).ToString("o");

                entries.Add(new JsonObject
                {
                    ["sequence"] = sequence,
                    ["category"] = Concept(SupportingInfoTypeSystem, "admissionperiod", null),
                    ["timingPeriod"] = period,
                });
            }
        }

        return entries;
    }

    internal static JsonArray Diagnoses(Claim claim, ProfileShape shape)
    {
        var diagnoses = new JsonArray();
        var ordinal = 1;

        foreach (var diagnosis in claim.DiagnosisCodes)
        {
            var sequence = diagnosis.PointerNumber > 0 ? diagnosis.PointerNumber : ordinal;

            // ABK is the 837's principal-diagnosis qualifier. Unlike base R4,
            // CARIN BB does define a concept for the rest — in its own code
            // system, and under a different name per profile — so a
            // non-principal diagnosis can carry the type the profile requires.
            var isPrincipal = string.Equals(diagnosis.CodeQualifier, "ABK", StringComparison.OrdinalIgnoreCase);
            var type = isPrincipal
                ? Concept(ExDiagnosisTypeSystem, "principal", null)
                : Concept(ClaimDiagnosisTypeSystem, shape.SecondaryDiagnosisCode, null);

            diagnoses.Add(new JsonObject
            {
                ["sequence"] = sequence,
                ["diagnosisCodeableConcept"] =
                    Concept(Icd10CmSystem, Icd10CmCode.ToFhir(diagnosis.Code), diagnosis.Description),
                ["type"] = new JsonArray { type },
            });
            ordinal++;
        }

        return diagnoses;
    }

    /// <summary>
    /// <c>insurance</c> is 1..* with a required <c>coverage</c>, so it is always
    /// emitted. When the claim carries a coverage id the reference is literal;
    /// otherwise it names the plan by display, because coverage-service has no
    /// FHIR <c>Coverage</c> projection for every claim yet and a reference to a
    /// resource that would 404 is worse than one that says what is known.
    /// </summary>
    internal static JsonArray Insurance(Claim claim)
    {
        var coverage = string.IsNullOrWhiteSpace(claim.CoverageId)
            ? new JsonObject { ["display"] = InsurerDisplay }
            : new JsonObject { ["reference"] = $"Coverage/{claim.CoverageId}" };

        return new JsonArray
        {
            new JsonObject
            {
                ["focal"] = true,
                ["coverage"] = coverage,
            },
        };
    }

    /// <summary>
    /// The item array. <paramref name="augment"/> is how the financial
    /// projection adds what the Basis profiles forbid — the line's charge, its
    /// unit price and its adjudication amounts — without a second copy of the
    /// shaping the two have in common.
    /// </summary>
    internal static JsonArray Items(
        Claim claim, ProfileShape shape, Action<JsonObject, ClaimLine>? augment = null)
    {
        var items = new JsonArray();

        foreach (var line in claim.ClaimLines)
        {
            var item = new JsonObject
            {
                ["sequence"] = line.LineNumber,
                ["productOrService"] = ClaimProcedureCoding.Concept(line),
            };

            if (line.Modifiers.Count > 0)
            {
                var modifiers = new JsonArray();
                foreach (var modifier in line.Modifiers)
                {
                    if (!string.IsNullOrWhiteSpace(modifier))
                        modifiers.Add(Concept(Cpt4System, modifier, null));
                }
                if (modifiers.Count > 0)
                    item["modifier"] = modifiers;
            }

            if (line.DiagnosisPointers.Count > 0)
            {
                var pointers = new JsonArray();
                foreach (var pointer in line.DiagnosisPointers)
                    pointers.Add(pointer);
                item["diagnosisSequence"] = pointers;
            }

            // serviced[x] is required on every Basis profile, but the outpatient
            // institutional profile narrows the choice to a date, so a period
            // there is a type error rather than extra precision.
            if (shape.ServicedMustBeDate)
            {
                item["servicedDate"] = AsUtc(line.ServiceDateFrom).ToString("yyyy-MM-dd");
            }
            else
            {
                item["servicedPeriod"] = new JsonObject
                {
                    ["start"] = AsUtc(line.ServiceDateFrom).ToString("yyyy-MM-dd"),
                    ["end"] = AsUtc(line.ServiceDateTo >= line.ServiceDateFrom
                        ? line.ServiceDateTo
                        : line.ServiceDateFrom).ToString("yyyy-MM-dd"),
                };
            }

            // The institutional profiles put the setting on the revenue code and
            // forbid nothing else; the professional and oral profiles require
            // location[x] as a CMS place-of-service code. This is where the
            // place of service belongs — the Claim projection carries it as a
            // CHO-local extension only because R4 Claim has nowhere else.
            if (shape.IsInstitutional)
            {
                if (!string.IsNullOrWhiteSpace(line.RevenueCode))
                    item["revenue"] = Concept(RevenueCodeSystem, line.RevenueCode, null);
            }
            else
            {
                var placeOfService = line.PlaceOfServiceCode ?? claim.PlaceOfServiceCode;
                if (!string.IsNullOrWhiteSpace(placeOfService))
                    item["locationCodeableConcept"] = Concept(ClaimFhirProjector.PlaceOfServiceSystem, placeOfService, null);
            }

            if (line.Units > 0)
            {
                var quantity = new JsonObject { ["value"] = line.Units };
                if (!string.IsNullOrWhiteSpace(line.UnitType))
                {
                    // qty-3: a unit code needs the system that defines it. The
                    // 837's SV103/SV204 code list is X12 element 355.
                    quantity["system"] = ClaimFhirProjector.DrugUnitOfMeasureSystem;
                    quantity["code"] = line.UnitType;
                }
                item["quantity"] = quantity;
            }

            // The professional and oral profiles require benefitpaymentstatus on
            // each item. No amount travels with it: it is a status, which is
            // exactly what a non-financial EOB is allowed to carry.
            if (!shape.IsInstitutional)
                item["adjudication"] = new JsonArray { BenefitPaymentStatus(claim) };

            augment?.Invoke(item, line);

            items.Add(item);
        }

        return items;
    }

    internal static JsonArray HeaderAdjudication(Claim claim, ProfileShape shape)
    {
        var entries = new JsonArray();

        // The institutional profiles require benefitpaymentstatus at the header;
        // the oral profile requires it in both places, which is why this is not
        // an either/or with the item-level entry.
        if (shape.IsInstitutional || shape.RequiresHeaderRenderingNetworkStatus)
            entries.Add(BenefitPaymentStatus(claim));

        // The oral profile alone requires a rendering network status. It can only
        // be emitted when the claim was adjudicated on a tier the profile's value
        // set has a code for; otherwise the element is left out and the instance
        // is non-conformant, which is the truth rather than a guessed status.
        if (shape.RequiresHeaderRenderingNetworkStatus && NetworkStatus(claim) is { } rendering)
        {
            entries.Add(new JsonObject
            {
                ["category"] = Concept(AdjudicationDiscriminatorSystem, "renderingnetworkstatus", null),
                ["reason"] = Concept(PayerAdjudicationStatusSystem, rendering, null),
            });
        }

        // billingnetworkstatus is 0..1 on every Basis profile and its reason is
        // bound required to innetwork / outofnetwork only. CHO's OutOfArea tier
        // maps to neither, so it is omitted rather than flattened into one of
        // them — which would misstate the provider's network position.
        if (NetworkStatus(claim) is { } status)
        {
            entries.Add(new JsonObject
            {
                ["category"] = Concept(AdjudicationDiscriminatorSystem, "billingnetworkstatus", null),
                ["reason"] = Concept(PayerAdjudicationStatusSystem, status, null),
            });
        }

        return entries;
    }

    /// <summary>
    /// The benefit payment status, whose required value set is innetwork,
    /// outofnetwork or other. "other" is the honest answer when the claim has
    /// not been adjudicated or was adjudicated on a tier the value set has no
    /// code for; it is not a default standing in for a value CHO knows.
    /// </summary>
    internal static JsonObject BenefitPaymentStatus(Claim claim) => new()
    {
        ["category"] = Concept(AdjudicationDiscriminatorSystem, "benefitpaymentstatus", null),
        ["reason"] = Concept(PayerAdjudicationStatusSystem, NetworkStatus(claim) ?? "other", null),
    };

    internal static string? NetworkStatus(Claim claim) => claim.AdjudicationResult?.NetworkTier switch
    {
        "InNetwork" => "innetwork",
        "OutOfNetwork" => "outofnetwork",
        _ => null,
    };

    // ── shared mappings ──────────────────────────────────────────────────────

    /// <summary>
    /// <c>status</c> is the resource's fm-status, not the adjudication state —
    /// the same rule <see cref="ClaimFhirProjector"/> follows. A denied claim
    /// still stands; only a void cancels it.
    /// </summary>
    internal static string MapStatus(ClaimStatus status) => status switch
    {
        ClaimStatus.Voided => "cancelled",
        _ => "active",
    };

    /// <summary>
    /// <c>outcome</c> is how far adjudication got, so a denial is
    /// <c>complete</c> rather than <c>error</c>: the claim was processed and the
    /// answer was no. <c>error</c> is reserved for a claim that could not be
    /// processed at all, which CHO does not represent as a status.
    /// </summary>
    internal static string MapOutcome(ClaimStatus status) => status switch
    {
        ClaimStatus.Paid or ClaimStatus.Approved => "complete",
        ClaimStatus.Denied or ClaimStatus.Deny => "complete",
        ClaimStatus.Voided => "complete",
        ClaimStatus.PartiallyPaid => "partial",
        _ => "queued",
    };

    internal static JsonObject ClaimTypeConcept(string code) => new()
    {
        ["coding"] = new JsonArray
        {
            new JsonObject
            {
                ["system"] = ClaimTypeSystem,
                ["version"] = ClaimTypeSystemVersion,
                ["code"] = code,
            },
        },
    };

    internal static JsonObject Concept(string system, string? code, string? display)
    {
        var coding = new JsonObject { ["system"] = system };
        if (!string.IsNullOrWhiteSpace(code))
            coding["code"] = code;
        if (!string.IsNullOrWhiteSpace(display))
            coding["display"] = display;

        return new JsonObject { ["coding"] = new JsonArray { coding } };
    }

    internal static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
