using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json.Nodes;
using AuthorizationService.Services.Fhir;
using ClaimsService.Fhir;
using EncounterService.Fhir;
using FluentAssertions;
using ChoAuthorization = AuthorizationService.Models.Authorization;
using ChoAuthorizationStatus = AuthorizationService.Models.AuthorizationStatus;
using ChoAuthDiagnosis = AuthorizationService.Models.DiagnosisCode;
using ChoClaim = ClaimsService.Models.Claim;
using ChoClaimDiagnosis = ClaimsService.Models.DiagnosisCode;
using ChoClaimLine = ClaimsService.Models.ClaimLine;
using ChoClaimStatus = ClaimsService.Models.ClaimStatus;
using ChoClaimType = ClaimsService.Models.ClaimType;
using ChoEncounter = EncounterService.Models.Encounter;
using ChoEncounterDiagnosis = EncounterService.Models.EncounterDiagnosis;
using ChoEncounterLine = EncounterService.Models.EncounterServiceLine;
using ChoLineOfBusiness = ClaimsService.Models.LineOfBusiness;
using ChoRequestedService = AuthorizationService.Models.RequestedService;

namespace CloudHealthOffice.FhirConformance.Tests;

/// <summary>
/// One golden instance per FHIR projection CHO serves, written from the real
/// projectors so that the HL7 validator judges what a caller actually receives.
///
/// <para>
/// Each test is a drift test: it asserts the projector still emits the
/// checked-in golden byte for byte. A failure means the wire contract changed —
/// regenerate with <c>UPDATE_FHIR_GOLDENS=1 dotnet test</c> and read the diff,
/// because the same change has to pass <c>scripts/fhir-validate.sh</c>.
/// </para>
///
/// <para>
/// The records are synthetic and deliberately rich: an authorization approved
/// in part with a denied line, a paid claim with a drug detail, so the goldens
/// exercise the elements a profile is most likely to reject rather than a
/// minimal happy path.
/// </para>
/// </summary>
public class GoldenInstanceTests
{
    /// <summary>
    /// Indented output, with the default resolver named explicitly: a projector
    /// may hold a value the node model only knows how to write through a
    /// resolver (a boxed decimal from <c>JsonValue.Create</c>), and custom
    /// options without one throw rather than writing it.
    /// </summary>
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void Prior_authorization_explanation_of_benefit()
        => Golden("prior-authorization-eob", new PriorAuthorizationEobProjector().Project(Authorization()));

    [Fact]
    public void Claim_explanation_of_benefit()
        => Golden("claim-eob", new ExplanationOfBenefitProjector().Project(Claim()));

    [Fact]
    public void Claim_as_billed()
        => Golden("claim-as-billed", new ClaimFhirProjector().Project(Claim()));

    [Fact]
    public void Encounter()
        => Golden("encounter", new EncounterFhirProjector().Project(EncounterRecord()));

    // ── the golden file ──────────────────────────────────────────────────────

    /// <summary>
    /// Compares the projection with <c>conformance/golden/{name}.json</c>, or
    /// writes it when <c>UPDATE_FHIR_GOLDENS</c> is set.
    /// </summary>
    private static void Golden(string name, JsonObject projection)
    {
        var path = Path.Combine(RepositoryRoot(), "conformance", "golden", name + ".json");
        var rendered = projection.ToJsonString(Pretty) + "\n";

        if (Environment.GetEnvironmentVariable("UPDATE_FHIR_GOLDENS") is { Length: > 0 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, rendered);
            return;
        }

        File.Exists(path).Should().BeTrue(
            $"conformance/golden/{name}.json is what the validator checks; regenerate it with UPDATE_FHIR_GOLDENS=1");
        File.ReadAllText(path).Replace("\r\n", "\n").Should().Be(rendered,
            "the projection changed, so the golden the HL7 validator reads is stale");
    }

    /// <summary>
    /// The repository root, found by walking up to the directory holding the
    /// solution file — the test runs from bin/, and the goldens are source.
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFiles("*.sln").Any())
            directory = directory.Parent;

        directory.Should().NotBeNull("the golden files live beside the solution");
        return directory!.FullName;
    }

    // ── synthetic records ────────────────────────────────────────────────────

    private static ChoAuthorization Authorization() => new()
    {
        TenantId = "tenant-golden",
        AuthorizationNumber = "AUTH-GOLD-1",
        MemberId = "MEM-GOLD-1",
        CoverageId = "cov-gold-1",
        LineOfBusiness = AuthorizationService.Models.LineOfBusiness.Medicaid,
        RequestingProviderNPI = "1234567893",
        RequestingProviderName = "Example Orthopaedics",
        ServicingProviderNPI = "1245319599",
        ServicingProviderName = "Example Surgical Center",
        ServiceTypeCode = "2",
        Status = ChoAuthorizationStatus.Modified,
        DenialReasonCode = "NOTMEDNEC",
        DenialReason = "Physical therapy not medically necessary for this indication",
        FollowUpAction = "Submit new clinical documentation to request reconsideration",
        SubmittedDate = new DateTime(2026, 9, 10, 14, 30, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateFrom = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc),
        RequestedServiceDateTo = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateFrom = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc),
        ApprovedServiceDateTo = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes =
        [
            new ChoAuthDiagnosis { Code = "M17.11", Description = "Unilateral primary osteoarthritis, right knee" },
        ],
        RequestedServices =
        [
            new ChoRequestedService
            {
                LineNumber = 1,
                ProcedureCode = "27447",
                ProcedureDescription = "Total knee arthroplasty",
                Modifiers = ["RT"],
                RequestedUnits = 1,
                ApprovedUnits = 1,
                UnitType = "procedures",
            },
            new ChoRequestedService
            {
                LineNumber = 2,
                ProcedureCode = "97110",
                ProcedureDescription = "Therapeutic exercise",
                RequestedUnits = 24,
                ApprovedUnits = 12,
                UnitType = "visits",
                DenialReasonCode = "NOTMEDNEC",
                DenialReason = "Twelve visits approved; the remainder is not medically necessary",
            },
        ],
    };

    private static ChoClaim Claim() => new()
    {
        Id = "clm-gold-1",
        ClaimVersionId = "clm-gold-1",
        TenantId = "tenant-golden",
        ClaimNumber = "CLM-GOLD-0001",
        MemberId = "MEM-GOLD-1",
        CoverageId = "cov-gold-1",
        Status = ChoClaimStatus.Paid,
        ClaimType = ChoClaimType.Professional,
        LineOfBusiness = ChoLineOfBusiness.Medicaid,
        BillingProviderNPI = "1234567893",
        PlaceOfServiceCode = "11",
        PriorAuthorizationNumber = "AUTH-GOLD-1",
        TotalChargeAmount = 430m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        SubmittedDate = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc),
        LastUpdatedDate = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes =
        [
            new ChoClaimDiagnosis { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" },
            new ChoClaimDiagnosis { Code = "I10", PointerNumber = 2, CodeQualifier = "ABF" },
        ],
        ClaimLines =
        [
            new ChoClaimLine
            {
                LineNumber = 1,
                ProcedureCode = "99214",
                Units = 1,
                ChargeAmount = 180m,
                DiagnosisPointers = [1],
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
            new ChoClaimLine
            {
                LineNumber = 2,
                ProcedureCode = "J1745",
                Units = 10,
                ChargeAmount = 250m,
                DiagnosisPointers = [1, 2],
                NationalDrugCode = "00002771501",
                DrugQuantity = 100m,
                DrugUnitOfMeasure = "ML",
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
    };

    private static ChoEncounter EncounterRecord() => new()
    {
        Id = "enc-gold-1",
        TenantId = "tenant-golden",
        EncounterControlNumber = "ECN-GOLD-1",
        ClaimId = "clm-gold-1",
        MemberId = "MEM-GOLD-1",
        BillingProviderNPI = "1234567893",
        BillingProviderName = "Example Clinic",
        RenderingProviderNPI = "1245319599",
        PayerId = "PAYER-GOLD",
        PlaceOfServiceCode = "11",
        TotalChargeAmount = 430m,
        ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
        // The projector falls back to the current time for meta.lastUpdated, so
        // the golden only holds still when the record carries its own.
        LastUpdatedDate = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc),
        DiagnosisCodes =
        [
            new ChoEncounterDiagnosis { Code = "E11.9", PointerNumber = 1, CodeQualifier = "ABK" },
        ],
        ServiceLines =
        [
            new ChoEncounterLine
            {
                LineNumber = 1,
                ProcedureCode = "99214",
                Units = 1,
                ChargeAmount = 180m,
                ServiceDateFrom = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
                ServiceDateTo = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc),
            },
        ],
    };
}
