using EncounterSubmissionService.StateRoutes;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.Texas;

/// <summary>
/// Texas-format sample files: the 837P, 837I and 837D CHO writes for TMHP, from
/// synthetic encounters, compared against the files under Texas/Samples. The samples
/// are what an EDI lead or a plan's trading partner team reviews before a TMHP test
/// cycle. Set UPDATE_TEXAS_SAMPLES=1 to rewrite them after an intended change.
/// </summary>
public class TexasSampleFileTests
{
    private static readonly string SamplesDir = Path.Combine(
        FindRepoRoot(), "tests", "CloudHealthOffice.EncounterSubmissionService.Tests", "Texas", "Samples");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cloudhealthoffice-main.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    /// <summary>TMHP test receiver from the public EDI Connectivity Guide (§2.2); the submitter is synthetic.</summary>
    private static Encounter837Envelope TmhpEnvelope(long icn) => Envelope(icn) with
    {
        ReceiverId = "617591011C21T",
        ReceiverName = "TMHP",
        SenderId = "SYNTHTPID01",
        SubmitterName = "SYNTHETIC STAR PLAN",
        PlanName = "SYNTHETIC STAR PLAN",
        DestinationPayerName = "TEXAS HHSC (SYNTHETIC PAYER ID)",
        DestinationPayerId = "TXHHSCSYN"
    };

    public static TheoryData<string, EncounterFormType, string> Forms => new()
    {
        { "TMHP_SYNTH_837P_TEST.dat", EncounterFormType.Professional, Encounter837Writer.ProfessionalVersion },
        { "TMHP_SYNTH_837I_TEST.dat", EncounterFormType.Institutional, Encounter837Writer.InstitutionalVersion },
        { "TMHP_SYNTH_837D_TEST.dat", EncounterFormType.Dental, Encounter837Writer.DentalVersion }
    };

    private static IReadOnlyList<EncounterClaim> Encounters(EncounterFormType form) => form switch
    {
        EncounterFormType.Institutional => new[] { Institutional("tx-i1") with { PatientControlNumber = "SYN-I-000001" } },
        EncounterFormType.Dental => new[]
        {
            Dental("tx-d1") with { PatientControlNumber = "SYN-D-000001" },
            Dental("tx-d2") with
            {
                PatientControlNumber = "SYN-D-000002",
                FrequencyCode = "7",
                OriginalStateReference = "SYNICN000000001"
            }
        },
        _ => new[]
        {
            Professional("tx-p1") with { PatientControlNumber = "SYN-P-000001" },
            Professional("tx-p2") with { PatientControlNumber = "SYN-P-000002", FrequencyCode = "8", OriginalStateReference = "SYNICN000000002" }
        }
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void SampleFile_PassesTexasValidation_AndTmhpStructureChecks_AndMatchesTheReviewedSample(
        string fileName, EncounterFormType form, string version)
    {
        var encounters = Encounters(form);
        foreach (var e in encounters)
            EncounterValidator.Validate(e, StateEncounterRoute.Texas).Should().BeEmpty($"{e.PatientControlNumber} is a valid Texas encounter");

        var content = Encounter837Writer.Write(form, encounters, TmhpEnvelope(900 + (int)form)).Content;

        TmhpFiles.Check837(content, version).Should().BeEmpty();

        var path = Path.Combine(SamplesDir, fileName);
        if (Environment.GetEnvironmentVariable("UPDATE_TEXAS_SAMPLES") == "1")
            File.WriteAllText(path, content);
        File.Exists(path).Should().BeTrue($"{fileName} is checked in; run with UPDATE_TEXAS_SAMPLES=1 to create it");
        File.ReadAllText(path).Should().Be(content, "the reviewed Texas sample must change only on purpose");
    }

    [Fact]
    public void StructureChecks_CatchWhatTmhpRejects()
    {
        var good = Encounter837Writer.Write(EncounterFormType.Professional,
            new[] { Professional() with { PatientControlNumber = "SYN-P-1" } }, TmhpEnvelope(1)).Content;

        TmhpFiles.Check837(good, Encounter837Writer.ProfessionalVersion).Should().BeEmpty();
        TmhpFiles.Check837(good.Replace("CLM*SYN-P-1*", "CLM*SYN-P-1-WITH-A-VERY-LONG-NUMBER*"),
            Encounter837Writer.ProfessionalVersion).Should().Contain(p => p.Contains("longer than"));
        TmhpFiles.Check837(good.Replace("~GE*", "~GS*HC*X*Y*20261101*1430*2*X*005010X222A1~GE*"),
            Encounter837Writer.ProfessionalVersion).Should().Contain(p => p.Contains("one GS-GE"));
        TmhpFiles.Check837(good, Encounter837Writer.InstitutionalVersion).Should().NotBeEmpty();
    }

    [Fact]
    public void Tmhp277Ca_ForEncounters_ParsesWithoutAnIcn_AndTreatsR3AsAWarning()
    {
        var x12 = X12Segments.Parse(TmhpFiles.Ack277Ca("SYNTHTPID01", "SYN-P-000001",
            ("SYN-P-000001", new[] { "A2:PR001" }, 250m),
            ("SYN-P-000002", new[] { "A2:PR001", "R3:Bx000095W" }, 250m),
            ("SYN-P-000003", new[] { "A3:Bx0000011" }, 250m)));

        var acks = Ack277Parser.Parse(x12, StateEncounterRoute.Texas.WarningStatusCategories.ToList());

        acks.Select(a => a.PatientControlNumber).Should().Equal("SYN-P-000001", "SYN-P-000002", "SYN-P-000003");
        acks.Should().OnlyContain(a => a.StateClaimReference == null, "TMHP does not send REF*1K for encounters");
        acks[0].IsAccepted.Should().BeTrue();
        acks[1].IsAccepted.Should().BeTrue();
        acks[1].Warnings.Should().Equal("R3:Bx000095W");
        acks[2].IsRejected.Should().BeTrue();
    }
}
