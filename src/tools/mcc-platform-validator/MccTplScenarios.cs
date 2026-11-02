using CloudHealthOffice.BenchmarkClaimGenerator.Models;

namespace CloudHealthOffice.Tools.MccPlatformValidator;

/// <summary>
/// Texas and California Medicaid third-party liability (TPL) scenarios.
/// Opt in with <c>--tpl-scenarios</c>. They need claims-service running with
/// <c>Adjudication:ThirdPartyLiability:Mode=CostAvoid</c>, which
/// <c>scripts/run-mcc-local-k8s.sh</c> sets when <c>TPL_SCENARIOS=true</c>.
///
/// <para>Each scenario is a clean professional office visit (the same shape
/// as <see cref="MccWorkflowValidation.CleanProfessionalPaidScenario"/>) for a
/// dedicated member with one TPL row recorded through coverage-service's
/// plan-found endpoint (<c>POST /api/v1/coverage/member/{id}/tpl</c>). The
/// claim's <see cref="SyntheticClaim.BenefitPlanId"/> carries the scenario
/// marker; claims are still submitted against the validation plan.</para>
///
/// <para>The rules scored come from the claims-service
/// <c>ThirdPartyLiabilityStage</c>, coverage-service <c>TplLayouts</c> and
/// docs/integration/MEDICAID-TPL.md.</para>
/// </summary>
public static class MccTplScenarios
{
    public const string PlanIdPrefix = "MCC_VALIDATION_TPL_";
    public const string CoveredByAnotherPayerCode = "CARC_22";

    /// <summary>Share of the corpus turned into TPL scenarios (at least one of each kind).</summary>
    public const double Rate = 0.01;

    public static IReadOnlyList<MccTplScenario> All { get; } =
    [
        // Texas: comprehensive other coverage on file; Medicaid is payer of
        // last resort (42 CFR 433.139), so the claim is cost-avoided CO-22.
        new("TX", "TxCostAvoid", "Comprehensive", "MCC Commercial Carrier", ExpectPaid: false),
        // Texas: same coverage, but a well-child visit under 21 is paid and
        // chased (SSA 1902(a)(25)(E); the stage's PayPediatricPreventive default).
        new("TX", "TxPediatricPreventivePayAndChase", "Comprehensive", "MCC Commercial Carrier", ExpectPaid: true, PediatricWellChild: true),
        // Texas: casualty-only coverage pays only for injuries; an office
        // visit with no accident indicator must not be cost-avoided.
        new("TX", "TxCasualtyOnlyPays", "Casualty", "MCC Auto Insurer", ExpectPaid: true),
        // California: Medi-Cal OHC code K (Kaiser, comprehensive), cost-avoided.
        new("CA", "CaOhcCostAvoid", "K", "MCC Kaiser Plan", ExpectPaid: false),
        // California: OHC code A is pay-and-chase only (APL 21-002): Medi-Cal
        // pays and the plan recovers.
        new("CA", "CaOhcCodeAPayAndChase", "A", "MCC Pay And Chase Carrier", ExpectPaid: true),
        // California: OHC code L is dental-only; a medical visit is not covered
        // by it and must pay.
        new("CA", "CaOhcDentalOnlyPays", "L", "MCC Dental Carrier", ExpectPaid: true),
    ];

    public static string PlanIdFor(MccTplScenario scenario) => PlanIdPrefix + scenario.Name.ToUpperInvariant();

    public static MccTplScenario? ForPlanId(string? benefitPlanId)
    {
        if (string.IsNullOrWhiteSpace(benefitPlanId)
            || !benefitPlanId.StartsWith(PlanIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        return All.FirstOrDefault(s => string.Equals(PlanIdFor(s), benefitPlanId, StringComparison.Ordinal));
    }

    public static bool IsTplScenario(SyntheticClaim claim) => ForPlanId(claim.BenefitPlanId) is not null;

    /// <summary>How many claims to turn into TPL scenarios: about <see cref="Rate"/>
    /// of the corpus, never fewer than one of each kind.</summary>
    public static int RequestedCount(int corpusSize)
        => Math.Max(All.Count, (int)Math.Round(corpusSize * Rate, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Turns a claim already shaped as a clean paid office visit into
    /// <paramref name="scenario"/>: a dedicated member (so no other corpus
    /// claim sees the TPL row), the member's state, and the answer key.
    /// </summary>
    public static void Apply(SyntheticClaim claim, MccTplScenario scenario)
    {
        var serviceDate = claim.DateOfService.Date;
        claim.BenefitPlanId = PlanIdFor(scenario);

        var member = claim.Member;
        member.MemberId = $"MBR-TPL-{claim.ClaimId}";
        member.SubscriberId = $"SUB-TPL-{claim.ClaimId}";
        member.State = scenario.State;
        member.Relationship = "Self";
        member.RelationshipCode = "18";
        member.IsSubscriber = true;
        member.EnrollmentStatus = "Active";
        member.CoverageTermDate = null;
        if (member.CoverageEffectiveDate == default || member.CoverageEffectiveDate.Date > serviceDate.AddDays(-30))
        {
            member.CoverageEffectiveDate = serviceDate.AddYears(-1);
        }

        if (scenario.PediatricWellChild)
        {
            // Eight years old on the date of service; well-child diagnosis.
            member.DateOfBirth = serviceDate.AddYears(-8).AddDays(-45);
            claim.PrimaryDiagnosisCode = "Z00.129";
        }
        else if (member.DateOfBirth == default || Age(member.DateOfBirth, serviceDate) < 21)
        {
            // Adults only, so the pediatric preventive exemption can't apply.
            member.DateOfBirth = serviceDate.AddYears(-40);
        }

        // No accident indicator: casualty coverage must not apply.
        claim.RelatedCausesCode = null;
        claim.AccidentDate = null;

        if (!scenario.ExpectPaid)
        {
            claim.ExpectedOutcome = new ExpectedOutcome
            {
                Disposition = "Denied",
                DenialReasonCode = "22",
                ExpectedAllowedAmount = 0m,
                ExpectedPaidAmount = 0m,
                ExpectedMemberLiability = 0m,
                ExpectedFhirCompliant = true,
                ExpectedPriorAuthDecision = "N/A",
                LineOutcomes = claim.Lines.Select(l => new LineOutcome
                {
                    LineNumber = l.LineNumber,
                    Disposition = "Denied",
                    ReasonCode = "22",
                }).ToList(),
            };
        }
    }

    public static ExpectedValidation ExpectedValidationFor(MccTplScenario scenario)
        => scenario.ExpectPaid
            ? new ExpectedValidation(ScenarioName(scenario), ClaimValidationOutcome.Paid, null)
            : new ExpectedValidation(ScenarioName(scenario), ClaimValidationOutcome.BusinessDenial, CoveredByAnotherPayerCode);

    public static string ScenarioName(MccTplScenario scenario) => $"Tpl:{scenario.Name}";

    /// <summary>The plan-found TPL row seeded for the scenario's member, as
    /// coverage-service's <c>TplDiscoveryRequest</c> reads it.</summary>
    public static object DiscoveryRequest(SyntheticClaim claim, MccTplScenario scenario)
    {
        var effective = DateTime.SpecifyKind(claim.DateOfService.Date.AddYears(-1), DateTimeKind.Utc);
        return new
        {
            state = scenario.State,
            carrierName = scenario.CarrierName,
            carrierCode = (string?)null,
            policyNumber = $"MCC-TPL-{claim.ClaimId}",
            groupNumber = (string?)null,
            policyholderName = $"{claim.Member.FirstName} {claim.Member.LastName}",
            coverage = scenario.Coverage,
            effectiveDate = effective,
            endDate = (DateTime?)null,
            discoveredAt = (DateTime?)null,
            howFound = "MCC validator synthetic fixture",
        };
    }

    private static int Age(DateTime dateOfBirth, DateTime on)
    {
        var age = on.Year - dateOfBirth.Year;
        if (on.Date < dateOfBirth.Date.AddYears(age)) age--;
        return age;
    }
}

/// <param name="State">The state whose rules the TPL row carries (TX or CA).</param>
/// <param name="Name">Scenario name used in the run summary.</param>
/// <param name="Coverage">The state's coverage code (Medi-Cal OHC code) or scope names.</param>
/// <param name="CarrierName">Synthetic carrier on the TPL row.</param>
/// <param name="ExpectPaid">True when Medicaid pays (no TPL applies, or pay-and-chase); false when cost-avoided.</param>
/// <param name="PediatricWellChild">Member under 21 with a well-child diagnosis.</param>
public sealed record MccTplScenario(
    string State,
    string Name,
    string Coverage,
    string CarrierName,
    bool ExpectPaid,
    bool PediatricWellChild = false);
