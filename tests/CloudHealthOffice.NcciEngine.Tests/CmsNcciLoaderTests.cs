using CloudHealthOffice.NcciEngine.Domain;
using CloudHealthOffice.NcciEngine.Import;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.NcciEngine.Tests;

/// <summary>
/// CMS NCCI PTP / MUE quarterly file parsing and loading. Fixtures under
/// Fixtures/cms are small synthetic files in the CMS layouts — no test
/// downloads anything.
/// </summary>
public class CmsNcciLoaderTests
{
    private const string Tenant = "tenant-ncci-load";

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "cms", name);

    private const string PraPtp = "ccipra-sample-2026Q4-f1.txt";
    private const string OphPtp = "ccioph-sample-2026Q4.txt";
    private const string PraMueQ4 = "MCR_MUE_PractitionerServices_sample_2026Q4.csv";
    private const string PraMueQ1 = "MCR_MUE_PractitionerServices_sample_2027Q1.csv";
    private const string OphMue = "MCR_MUE_OutpatientHospitalServices_sample_2026Q4.csv";

    private sealed class Harness
    {
        public FakeNcciRepository Repo { get; } = new();
        public NcciLookupCache Cache { get; } = new();
        public NcciQuarterlyLoader Loader { get; }
        public NcciEditService Service { get; }

        public Harness()
        {
            Loader = new NcciQuarterlyLoader(Repo, Cache, NullLogger<NcciQuarterlyLoader>.Instance);
            Service = new NcciEditService(Repo, NullLogger<NcciEditService>.Instance, Cache);
        }

        public async Task<NcciLoadResult> Load(
            string file, string quarter, NcciCmsFileKind kind, string setting, bool force = false, string? part = null)
        {
            await using var stream = File.OpenRead(Fixture(file));
            return await Loader.LoadAsync(new NcciLoadRequest
            {
                TenantId = Tenant,
                Quarter = quarter,
                FileKind = kind,
                Setting = setting,
                FileName = file,
                Force = force,
                Part = part,
            }, stream);
        }
    }

    private static NcciScrubRequest Claim(string claimType, DateOnly dos, params (string code, decimal units)[] lines) => new()
    {
        TenantId = Tenant,
        ClaimId = "CLM-LOAD",
        ClaimType = claimType,
        ServiceLines = lines.Select((l, i) => new ClaimServiceLine
        {
            LineNumber = i + 1,
            ProcedureCode = l.code,
            Units = l.units,
            ServiceDate = dos,
        }).ToList(),
    };

    // ── Parsers ───────────────────────────────────────────────────────

    [Fact]
    public void ParsePtp_CmsTabLayout_ReadsRowsSkipsPreambleAndReportsMalformedLines()
    {
        using var reader = File.OpenText(Fixture(PraPtp));
        var result = CmsNcciFileParser.ParsePtp(reader);

        Assert.Equal(6, result.Rows.Count);
        Assert.Equal(3, result.Rejections.Count);
        Assert.Contains(result.Rejections, r => r.StartsWith("line 10:"));
        Assert.Contains(result.Rejections, r => r.Contains("invalid effective date"));
        Assert.Contains(result.Rejections, r => r.Contains("invalid modifier indicator"));

        var first = result.Rows[0];
        Assert.Equal("99213", first.Column1Code);
        Assert.Equal("36415", first.Column2Code);
        Assert.True(first.ExistedPrior1996);
        Assert.Equal(new DateTime(1996, 1, 1), first.EffectiveDate);
        Assert.Null(first.DeletionDate);
        Assert.Equal(NcciModifierIndicator.NotAllowed, first.ModifierIndicator);
        Assert.Equal("Misuse of column two code with column one code", first.Rationale);

        var deleted = result.Rows.Single(r => r.Column1Code == "97110");
        Assert.Equal(new DateTime(2026, 10, 1), deleted.DeletionDate);
        Assert.False(deleted.ExistedPrior1996);

        Assert.Equal(NcciModifierIndicator.NotApplicable, result.Rows.Single(r => r.Column1Code == "93460").ModifierIndicator);
        Assert.Contains(result.Rows, r => r.Column1Code == "0001U" && r.Column2Code == "G0480");
    }

    [Fact]
    public void ParsePtp_CsvSavedFromCmsWorkbook_ReadsSameColumns()
    {
        var csv = "Column 1,Column 2,*=in existence prior to 1996,Effective Date,Deletion Date *=no data,Modifier 0=not allowed 1=allowed 9=not applicable,PTP Edit Rationale\n"
                + "99213,36415,*,19960101,*,0,\"Misuse of column two code, with column one code\"\n";
        var result = CmsNcciFileParser.ParsePtp(new StringReader(csv));

        var row = Assert.Single(result.Rows);
        Assert.Equal("Misuse of column two code, with column one code", row.Rationale);
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void ParseMue_CmsCsvLayout_ReadsMaiDigitQuotedFieldsAndReportsMalformedLines()
    {
        using var reader = File.OpenText(Fixture(PraMueQ4));
        var result = CmsNcciFileParser.ParseMue(reader);

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(2, result.Rejections.Count);
        Assert.Contains(result.Rejections, r => r.Contains("invalid MUE value"));
        Assert.Contains(result.Rejections, r => r.Contains("invalid MUE adjudication indicator"));

        var venipuncture = result.Rows.Single(r => r.ProcedureCode == "36415");
        Assert.Equal(2, venipuncture.MaxUnits);
        Assert.Equal(MueAdjudicationIndicator.DateOfService, venipuncture.AdjudicationIndicator);
        Assert.Equal("CMS Policy", venipuncture.Rationale);

        Assert.Equal(MueAdjudicationIndicator.ClaimLine, result.Rows.Single(r => r.ProcedureCode == "J1100").AdjudicationIndicator);
        var cbc = result.Rows.Single(r => r.ProcedureCode == "85025");
        Assert.Equal(MueAdjudicationIndicator.DateOfServiceAbsolute, cbc.AdjudicationIndicator);
        Assert.Equal("Code Descriptor / CPT Instruction", cbc.Rationale);
    }

    [Fact]
    public void ParseMue_HeaderDrivesColumnPositions()
    {
        var csv = "HCPCS/CPT Code,MUE Rationale,MUE Adjudication Indicator,Practitioner Services MUE Values\n"
                + "36415,CMS Policy,2 Date of Service Edit: Policy,4\n";
        var row = Assert.Single(CmsNcciFileParser.ParseMue(new StringReader(csv)).Rows);
        Assert.Equal(4, row.MaxUnits);
        Assert.Equal("CMS Policy", row.Rationale);
    }

    // ── Loader: idempotency and versioning ────────────────────────────

    [Fact]
    public async Task Load_SameFileTwice_SecondRunIsNoOpAndForceRewritesSameDocuments()
    {
        var h = new Harness();

        var first = await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner);
        Assert.False(first.AlreadyLoaded);
        Assert.Equal(6, first.RowsLoaded);
        Assert.Equal(3, first.RowsRejected);
        Assert.Equal(3, first.Rejections.Count);
        Assert.Equal(6, h.Repo.Pairs.Count);

        var second = await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner);
        Assert.True(second.AlreadyLoaded);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(6, h.Repo.PairUpsertCount);

        var forced = await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner, force: true);
        Assert.False(forced.AlreadyLoaded);
        Assert.Equal(12, h.Repo.PairUpsertCount);
        Assert.Equal(6, h.Repo.Pairs.Count); // stable ids: replaced, not duplicated

        var pair = h.Repo.Pairs.Single(p => p.Column1Code == "99213");
        Assert.Equal(NcciSettings.Practitioner, pair.Setting);
        Assert.Equal("2026Q4", pair.SourceQuarter);
        Assert.Equal($"{Tenant}_{NcciSettings.Practitioner}_99213_36415_19960101", pair.Id);
        Assert.Equal(NcciPolicyType.MutuallyExclusive, h.Repo.Pairs.Single(p => p.Column1Code == "70553").PolicyType);
    }

    [Fact]
    public async Task Load_PtpDeletionDate_IsExclusiveEndOfEdit()
    {
        var h = new Harness();
        await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner);

        var before = await h.Service.ScrubAsync(Claim("837P", new DateOnly(2026, 9, 30), ("97110", 1), ("97010", 1)));
        var after = await h.Service.ScrubAsync(Claim("837P", new DateOnly(2026, 10, 1), ("97110", 1), ("97010", 1)));

        Assert.Contains(before.EditFailures, f => f.RuleId == "NE001" && f.Column2Code == "97010");
        Assert.DoesNotContain(after.EditFailures, f => f.RuleId == "NE001");
    }

    [Fact]
    public async Task Load_PractitionerAndOutpatientTables_ApplyToTheirOwnClaimTypes()
    {
        var h = new Harness();
        await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner);
        await h.Load(OphPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.OutpatientHospital);

        // Same pair, different modifier indicator per setting: both rows coexist.
        Assert.Equal(2, h.Repo.Pairs.Count(p => p.Column1Code == "99213" && p.Column2Code == "36415"));

        var dos = new DateOnly(2026, 11, 2);
        var professional = await h.Service.ScrubAsync(Claim("837P", dos, ("99213", 1), ("36415", 1)));
        var institutional = await h.Service.ScrubAsync(Claim("837I", dos, ("99213", 1), ("36415", 1)));

        Assert.Equal("97", Assert.Single(professional.EditFailures, f => f.RuleId == "NE001").SuggestedCarc);   // MI 0
        Assert.Equal("B20", Assert.Single(institutional.EditFailures, f => f.RuleId == "NE001").SuggestedCarc); // MI 1

        // 70553/70551 is only in the practitioner table.
        var oph = await h.Service.ScrubAsync(Claim("837I", dos, ("70553", 1), ("70551", 1)));
        Assert.DoesNotContain(oph.EditFailures, f => f.RuleId == "NE001");
    }

    [Fact]
    public async Task Load_MueTables_PerSettingValuesAndAdjudicationIndicator()
    {
        var h = new Harness();
        await h.Load(PraMueQ4, "2026Q4", NcciCmsFileKind.Mue, NcciSettings.Practitioner);
        await h.Load(OphMue, "2026Q4", NcciCmsFileKind.Mue, NcciSettings.OutpatientHospital);

        var dos = new DateOnly(2026, 11, 2);
        // Practitioner MUE for 36415 is 2 (MAI 2, summed across lines); OPH is 3.
        var pro = await h.Service.ScrubAsync(Claim("837P", dos, ("36415", 2), ("36415", 1)));
        var inst = await h.Service.ScrubAsync(Claim("837I", dos, ("36415", 2), ("36415", 1)));

        var failure = Assert.Single(pro.EditFailures, f => f.RuleId == "NE002");
        Assert.Equal(2, failure.MueMaxUnits);
        Assert.Equal(3m, failure.UnitsBilled);
        Assert.DoesNotContain(inst.EditFailures, f => f.RuleId == "NE002");

        // MAI 1 (line edit): two lines of 30 each pass, one line of 31 fails.
        var lineEditOk = await h.Service.ScrubAsync(Claim("837P", dos, ("J1100", 30), ("J1100", 30)));
        var lineEditFail = await h.Service.ScrubAsync(Claim("837P", dos, ("J1100", 31)));
        Assert.DoesNotContain(lineEditOk.EditFailures, f => f.RuleId == "NE002");
        Assert.Contains(lineEditFail.EditFailures, f => f.RuleId == "NE002");

        var entry = h.Repo.Mues.Single(m => m.ProcedureCode == "J1100");
        Assert.True(entry.AppliesToProfessional);
        Assert.False(entry.AppliesToOutpatientFacility);
        Assert.Equal(new DateTime(2026, 10, 1), entry.EffectiveDate);
    }

    [Fact]
    public async Task Load_NextQuarterMue_SupersedesByDateOfServiceAndExpiresDroppedCodes()
    {
        var h = new Harness();
        await h.Load(PraMueQ4, "2026Q4", NcciCmsFileKind.Mue, NcciSettings.Practitioner);
        var q1 = await h.Load(PraMueQ1, "2027Q1", NcciCmsFileKind.Mue, NcciSettings.Practitioner);

        Assert.Equal(1, q1.RowsExpired); // 97110 is not in the 2027Q1 table

        var q4Dos = new DateOnly(2026, 12, 15);
        var q1Dos = new DateOnly(2027, 1, 15);

        // 36415: 2 units allowed in Q4, 1 in Q1.
        Assert.DoesNotContain((await h.Service.ScrubAsync(Claim("837P", q4Dos, ("36415", 2)))).EditFailures, f => f.RuleId == "NE002");
        Assert.Contains((await h.Service.ScrubAsync(Claim("837P", q1Dos, ("36415", 2)))).EditFailures, f => f.RuleId == "NE002");

        // 97110: limited to 6 in Q4; no MUE from Q1 on.
        Assert.Contains((await h.Service.ScrubAsync(Claim("837P", q4Dos, ("97110", 7)))).EditFailures, f => f.RuleId == "NE002");
        Assert.DoesNotContain((await h.Service.ScrubAsync(Claim("837P", q1Dos, ("97110", 7)))).EditFailures, f => f.RuleId == "NE002");

        Assert.Equal("2027Q1", h.Repo.Version!.Quarter);
        Assert.Equal(4, h.Repo.Version.MueEntryCount);
        Assert.Equal(new DateTime(2027, 1, 1), h.Repo.Version.EffectiveDate);
    }

    [Fact]
    public async Task Load_BackfillOlderQuarter_DoesNotMoveVersionBackward()
    {
        var h = new Harness();
        await h.Load(PraMueQ1, "2027Q1", NcciCmsFileKind.Mue, NcciSettings.Practitioner);
        await h.Load(PraMueQ4, "2026Q4", NcciCmsFileKind.Mue, NcciSettings.Practitioner);

        Assert.Equal("2027Q1", h.Repo.Version!.Quarter);
    }

    [Fact]
    public async Task Load_VersionCountsSumEveryFileOfTheQuarter()
    {
        var h = new Harness();
        await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner, part: "f1");
        await h.Load(OphPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.OutpatientHospital);
        await h.Load(PraMueQ4, "2026Q4", NcciCmsFileKind.Mue, NcciSettings.Practitioner);

        Assert.Equal("2026Q4", h.Repo.Version!.Quarter);
        Assert.Equal(8, h.Repo.Version.NcciPairCount);
        Assert.Equal(5, h.Repo.Version.MueEntryCount);
    }

    [Fact]
    public async Task Load_SeedRowsWithoutSetting_StillApplyAfterCmsLoad()
    {
        var h = new Harness();
        h.Repo.AddEditPair(new NcciEditPair
        {
            Id = "seed", TenantId = Tenant, Column1Code = "47563", Column2Code = "49320",
            ModifierIndicator = NcciModifierIndicator.NotAllowed, EffectiveDate = new DateTime(2025, 1, 1),
        });
        await h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, NcciSettings.Practitioner);

        var result = await h.Service.ScrubAsync(Claim("837I", new DateOnly(2026, 11, 2), ("47563", 1), ("49320", 1)));
        Assert.Contains(result.EditFailures, f => f.RuleId == "NE001");
    }

    [Theory]
    [InlineData("2026-Q4")]
    [InlineData("2026Q5")]
    [InlineData("Q4 2026")]
    [InlineData("")]
    public async Task Load_InvalidQuarter_Throws(string quarter)
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Load(PraPtp, quarter, NcciCmsFileKind.Ptp, NcciSettings.Practitioner));
    }

    [Fact]
    public async Task Load_UnknownSetting_Throws()
    {
        var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            h.Load(PraPtp, "2026Q4", NcciCmsFileKind.Ptp, "DME"));
    }

    [Fact]
    public async Task Load_FileWithNoCmsRows_ThrowsAndWritesNothing()
    {
        var h = new Harness();
        using var stream = new MemoryStream("not,a,cms,file\nhello\n"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => h.Loader.LoadAsync(new NcciLoadRequest
        {
            TenantId = Tenant,
            Quarter = "2026Q4",
            FileKind = NcciCmsFileKind.Mue,
            Setting = NcciSettings.Practitioner,
        }, stream));

        Assert.Empty(h.Repo.Mues);
        Assert.Null(h.Repo.Version);
    }

    [Theory]
    [InlineData("837P", NcciSettings.Practitioner)]
    [InlineData("837I", NcciSettings.OutpatientHospital)]
    [InlineData("837D", null)]
    public void NcciSettings_ForClaimType(string claimType, string? expected)
        => Assert.Equal(expected, NcciSettings.ForClaimType(claimType));
}
