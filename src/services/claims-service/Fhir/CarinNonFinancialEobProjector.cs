using System.Text.Json.Nodes;
using ClaimsService.Models;

namespace ClaimsService.Fhir;

/// <summary>
/// The CHO claim record as a CARIN Blue Button <b>non-financial</b>
/// <c>ExplanationOfBenefit</c> — the payload CMS-0057-F's Provider Access API
/// is expected to carry.
///
/// <para>
/// This is not the same resource as <see cref="ExplanationOfBenefitProjector"/>
/// produces. That one is the member-facing Patient Access EOB and carries what
/// adjudication decided, money included. CARIN BB defines a parallel set of
/// "Basis" profiles that require everything <i>but</i> the dollar amounts, and
/// those are what a payer sends another provider: the clinical and
/// administrative record of the encounter without the plan's financial terms.
/// PDex points Provider Access at them, and it puts <b>both</b> claims and
/// encounters in them — there is no FHIR <c>Claim</c> or <c>Encounter</c> in a
/// Provider Access response. CHO's <see cref="ClaimFhirProjector"/> and
/// encounter-service's Encounter projection remain the CHO-native source
/// records behind this projection, not the payload itself.
/// </para>
///
/// <para>
/// So nothing monetary is emitted here, and that is a requirement rather than a
/// simplification: no <c>total</c>, no <c>item.net</c> or <c>unitPrice</c>, no
/// adjudication amount of any kind. A test asserts the absence.
/// </para>
///
/// <para>
/// Which profile an instance declares follows the claim type, as CARIN BB
/// splits them:
/// </para>
/// <list type="table">
///   <item><term><see cref="ClaimType.Professional"/></term>
///     <description>Professional NonClinician Basis.</description></item>
///   <item><term><see cref="ClaimType.Institutional"/></term>
///     <description>Inpatient or Outpatient Institutional Basis, decided by the
///     bill classification digit of the type of bill — see
///     <see cref="IsInpatient"/>.</description></item>
///   <item><term><see cref="ClaimType.Dental"/></term>
///     <description>Oral Basis.</description></item>
/// </list>
///
/// <para>
/// Known conformance limits, stated rather than papered over, and checked by
/// the HL7 validator in CI (see <c>scripts/interop/fhir-validate.sh</c>):
/// <c>insurer</c>, the header <c>provider</c> and <c>insurance.coverage</c> are
/// references by identifier and display, because CHO publishes no
/// <c>Organization</c> or <c>Coverage</c> resource for them to point at, and a
/// billing NPI may denote either a group or an individual; the C4BB
/// <c>targetProfile</c>s on those elements therefore go unsatisfied.
/// <c>careTeam.provider</c> does carry a literal <c>Practitioner</c> reference.
/// The
/// Inpatient profile requires a revenue code on every line, which an 837I
/// carries but the model allows to be null.
/// </para>
/// </summary>
public sealed class CarinNonFinancialEobProjector : ICarinNonFinancialEobProjector
{
    private const string ProfileBase = "http://hl7.org/fhir/us/carin-bb/StructureDefinition/";

    public const string ProfessionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Professional-NonClinician-Basis";
    public const string InpatientInstitutionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Inpatient-Institutional-Basis";
    public const string OutpatientInstitutionalBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Outpatient-Institutional-Basis";
    public const string OralBasisProfile =
        ProfileBase + "C4BB-ExplanationOfBenefit-Oral-Basis";

    private const string ClaimTypeSystem = "http://terminology.hl7.org/CodeSystem/claim-type";

    /// <summary>
    /// The institutional and oral profiles fix <c>type</c> to a pattern that
    /// includes the code system's version, so a coding without it does not match
    /// and the validator rejects the instance. This is the version CARIN BB
    /// 2.2.0 pins, not a version CHO chose.
    /// </summary>
    private const string ClaimTypeSystemVersion = "1.0.1";
    private const string ExDiagnosisTypeSystem = "http://terminology.hl7.org/CodeSystem/ex-diagnosistype";
    private const string ClaimCareTeamRoleSystem = "http://terminology.hl7.org/CodeSystem/claimcareteamrole";
    private const string Icd10CmSystem = "http://hl7.org/fhir/sid/icd-10-cm";
    private const string Cpt4System = "http://www.ama-assn.org/go/cpt";
    private const string NpiSystem = "http://hl7.org/fhir/sid/us-npi";
    private const string RevenueCodeSystem = "https://www.nubc.org/CodeSystem/RevenueCodes";
    private const string TypeOfBillSystem = "https://www.nubc.org/CodeSystem/TypeOfBill";

    private const string C4BbBase = "http://hl7.org/fhir/us/carin-bb/CodeSystem/";
    private const string IdentifierTypeSystem = C4BbBase + "C4BBIdentifierType";
    private const string ClaimDiagnosisTypeSystem = C4BbBase + "C4BBClaimDiagnosisType";
    private const string InstitutionalSubTypeSystem = C4BbBase + "C4BBInstitutionalClaimSubType";
    private const string AdjudicationDiscriminatorSystem = C4BbBase + "C4BBAdjudicationDiscriminator";
    private const string PayerAdjudicationStatusSystem = C4BbBase + "C4BBPayerAdjudicationStatus";
    private const string SupportingInfoTypeSystem = C4BbBase + "C4BBSupportingInfoType";
    private const string CareTeamRoleSystem = C4BbBase + "C4BBClaimCareTeamRole";

    /// <summary>The plan's own name, used as the insurer's display.</summary>
    private const string InsurerDisplay = "CloudHealthOffice";

    public JsonObject Project(Claim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var shape = Shape(claim);

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
        if (supportingInfo.Count > 0)
            eob["supportingInfo"] = supportingInfo;

        eob["diagnosis"] = Diagnoses(claim, shape);
        eob["insurance"] = Insurance(claim);
        eob["item"] = Items(claim, shape);

        // Header-level adjudication. The institutional profiles require
        // benefitpaymentstatus here; the professional and oral profiles require
        // it on each item instead, which Items() handles.
        var headerAdjudication = HeaderAdjudication(claim, shape);
        if (headerAdjudication.Count > 0)
            eob["adjudication"] = headerAdjudication;

        return eob;
    }

    // ── profile selection ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything that differs between the four Basis profiles, resolved once so
    /// the body of the projection reads the same for all of them.
    /// </summary>
    private sealed record ProfileShape(
        string Profile,
        string ClaimTypeCode,
        string? SubTypeCode,
        bool IsInstitutional,
        bool ServicedMustBeDate,
        bool RequiresHeaderRenderingNetworkStatus,
        string SecondaryDiagnosisCode,
        string DefaultCareTeamRoleSystem,
        string DefaultCareTeamRole);

    private static ProfileShape Shape(Claim claim) => claim.ClaimType switch
    {
        ClaimType.Institutional when IsInpatient(claim) => new ProfileShape(
            InpatientInstitutionalBasisProfile, "institutional", "inpatient", true, false, false,
            // The inpatient profile's diagnosis-type value set has no
            // "secondary": a non-principal diagnosis is "other".
            "other", CareTeamRoleSystem, "attending"),

        ClaimType.Institutional => new ProfileShape(
            OutpatientInstitutionalBasisProfile, "institutional", "outpatient", true, true, false,
            "other", CareTeamRoleSystem, "attending"),

        ClaimType.Dental => new ProfileShape(
            OralBasisProfile, "oral", null, false, false, true,
            "secondary", CareTeamRoleSystem, "rendering"),

        _ => new ProfileShape(
            ProfessionalBasisProfile, "professional", null, false, false, false,
            "secondary", CareTeamRoleSystem, "rendering"),
    };

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
    private static bool IsInpatient(Claim claim)
    {
        var facilityType = claim.Institutional?.FacilityTypeCode;
        if (facilityType is null || facilityType.Length < 2)
            return false;

        return facilityType[1] is '1' or '2';
    }

    // ── header ───────────────────────────────────────────────────────────────

    private static JsonObject BillablePeriod(Claim claim)
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
    private static JsonObject ProviderReference(string? npi, string? name, string? resourceType = null)
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
    private static JsonArray CareTeam(Claim claim, ProfileShape shape)
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

    private static JsonArray SupportingInfo(Claim claim, ProfileShape shape)
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

    private static JsonArray Diagnoses(Claim claim, ProfileShape shape)
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
                    Concept(Icd10CmSystem, diagnosis.Code, diagnosis.Description),
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
    private static JsonArray Insurance(Claim claim)
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

    private static JsonArray Items(Claim claim, ProfileShape shape)
    {
        var items = new JsonArray();

        foreach (var line in claim.ClaimLines)
        {
            var item = new JsonObject
            {
                ["sequence"] = line.LineNumber,
                ["productOrService"] =
                    Concept(Cpt4System, line.ProcedureCode, line.ProcedureDescription),
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

            items.Add(item);
        }

        return items;
    }

    private static JsonArray HeaderAdjudication(Claim claim, ProfileShape shape)
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
    private static JsonObject BenefitPaymentStatus(Claim claim) => new()
    {
        ["category"] = Concept(AdjudicationDiscriminatorSystem, "benefitpaymentstatus", null),
        ["reason"] = Concept(PayerAdjudicationStatusSystem, NetworkStatus(claim) ?? "other", null),
    };

    private static string? NetworkStatus(Claim claim) => claim.AdjudicationResult?.NetworkTier switch
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
    private static string MapStatus(ClaimStatus status) => status switch
    {
        ClaimStatus.Voided => "cancelled",
        _ => "active",
    };

    private static string MapOutcome(ClaimStatus status) => status switch
    {
        ClaimStatus.Paid or ClaimStatus.Approved => "complete",
        ClaimStatus.Denied or ClaimStatus.Deny => "complete",
        ClaimStatus.Voided => "complete",
        _ => "queued",
    };

    private static JsonObject ClaimTypeConcept(string code) => new()
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

    private static JsonObject Concept(string system, string? code, string? display)
    {
        var coding = new JsonObject { ["system"] = system };
        if (!string.IsNullOrWhiteSpace(code))
            coding["code"] = code;
        if (!string.IsNullOrWhiteSpace(display))
            coding["display"] = display;

        return new JsonObject { ["coding"] = new JsonArray { coding } };
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
