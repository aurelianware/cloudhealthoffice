namespace CloudHealthOffice.BenchmarkClaimGenerator.Configuration;

/// <summary>
/// The default Million Claim Challenge corpus profile: 1,000,000 claims
/// with a 60/25/10/5 split across Professional, Institutional, Dental, and Edge Cases.
/// </summary>
public static class DefaultCorpusProfile
{
    /// <summary>
    /// Creates the default 1M claim corpus profile with the standard stratified distribution.
    /// </summary>
    /// <param name="seed">Random seed for reproducibility. Default is 42.</param>
    public static CorpusProfile Create(int seed = 42) => new()
    {
        TotalClaims = 1_000_000,
        Seed = seed,
        Professional = new ProfessionalDistribution
        {
            Count = 600_000,
            OfficeVisitFraction = 0.40,
            MultiLineProcedureFraction = 0.20,
            GlobalSurgeryFraction = 0.10,
            BilateralFraction = 0.05,
            AssistantSurgeonFraction = 0.05,
            TelemedicineFraction = 0.10,
            LabPathologyFraction = 0.10
        },
        Institutional = new InstitutionalDistribution
        {
            Count = 250_000,
            InpatientDrgFraction = 0.40,
            OutpatientPerDiemFraction = 0.25,
            EmergencyFraction = 0.15,
            ObservationFraction = 0.10,
            StopLossOutlierFraction = 0.05,
            SkilledNursingFraction = 0.05
        },
        Dental = new DentalDistribution
        {
            Count = 100_000,
            PreventiveFraction = 0.40,
            RestorativeFraction = 0.25,
            EndodonticsFraction = 0.10,
            PeriodonticsFraction = 0.10,
            OrthodonticsFraction = 0.10,
            OralSurgeryFraction = 0.05
        },
        EdgeCases = new EdgeCaseDistribution
        {
            Count = 50_000,
            CobCount = 12_000,
            RetroEligibilityCount = 8_000,
            NewbornCount = 6_000,
            PriorAuthCount = 8_000,
            SubrogationCount = 4_000,
            BehavioralHealthCount = 6_000,
            MedicaidCount = 6_000
        }
    };
}
