using CloudHealthOffice.BenchmarkClaimGenerator.Configuration;

namespace CloudHealthOffice.BenchmarkClaimGenerator.Tests;

public class CorpusProfileTests
{
    [Fact]
    public void DefaultCorpusProfile_Has1MillionTotalClaims()
    {
        var profile = DefaultCorpusProfile.Create();

        Assert.Equal(1_000_000, profile.TotalClaims);
    }

    [Fact]
    public void DefaultCorpusProfile_Has60_25_10_5_Split()
    {
        var profile = DefaultCorpusProfile.Create();

        Assert.Equal(600_000, profile.Professional.Count);
        Assert.Equal(250_000, profile.Institutional.Count);
        Assert.Equal(100_000, profile.Dental.Count);
        Assert.Equal(50_000, profile.EdgeCases.Count);
    }

    [Fact]
    public void DefaultCorpusProfile_ProfessionalFractions_SumToOne()
    {
        var profile = DefaultCorpusProfile.Create();
        var p = profile.Professional;

        var total = p.OfficeVisitFraction + p.MultiLineProcedureFraction +
                    p.GlobalSurgeryFraction + p.BilateralFraction +
                    p.AssistantSurgeonFraction + p.TelemedicineFraction +
                    p.LabPathologyFraction;

        Assert.Equal(1.0, total, 5);
    }

    [Fact]
    public void DefaultCorpusProfile_InstitutionalFractions_SumToOne()
    {
        var profile = DefaultCorpusProfile.Create();
        var inst = profile.Institutional;

        var total = inst.InpatientDrgFraction + inst.OutpatientPerDiemFraction +
                    inst.EmergencyFraction + inst.ObservationFraction +
                    inst.StopLossOutlierFraction + inst.SkilledNursingFraction;

        Assert.Equal(1.0, total, 5);
    }

    [Fact]
    public void DefaultCorpusProfile_DentalFractions_SumToOne()
    {
        var profile = DefaultCorpusProfile.Create();
        var d = profile.Dental;

        var total = d.PreventiveFraction + d.RestorativeFraction +
                    d.EndodonticsFraction + d.PeriodonticsFraction +
                    d.OrthodonticsFraction + d.OralSurgeryFraction;

        Assert.Equal(1.0, total, 5);
    }

    [Fact]
    public void DefaultCorpusProfile_EdgeCaseCounts_SumToTotal()
    {
        var profile = DefaultCorpusProfile.Create();
        var e = profile.EdgeCases;

        var total = e.CobCount + e.RetroEligibilityCount + e.NewbornCount +
                    e.PriorAuthCount + e.SubrogationCount +
                    e.BehavioralHealthCount + e.MedicaidCount;

        Assert.Equal(e.Count, total);
    }

    [Fact]
    public void DefaultCorpusProfile_ClaimCounts_SumToTotal()
    {
        var profile = DefaultCorpusProfile.Create();

        var total = profile.Professional.Count + profile.Institutional.Count +
                    profile.Dental.Count + profile.EdgeCases.Count;

        Assert.Equal(profile.TotalClaims, total);
    }

    [Fact]
    public void DefaultCorpusProfile_CustomSeed_IsApplied()
    {
        var profile = DefaultCorpusProfile.Create(seed: 123);

        Assert.Equal(123, profile.Seed);
    }

    [Fact]
    public void DefaultCorpusProfile_DefaultSeed_Is42()
    {
        var profile = DefaultCorpusProfile.Create();

        Assert.Equal(42, profile.Seed);
    }
}
