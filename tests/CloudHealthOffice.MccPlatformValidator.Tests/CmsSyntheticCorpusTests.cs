using CloudHealthOffice.Tools.MccPlatformValidator;

namespace CloudHealthOffice.MccPlatformValidator.Tests;

public sealed class CmsSyntheticCorpusTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cms-synthetic-" + Guid.NewGuid().ToString("N"));

    public CmsSyntheticCorpusTests()
    {
        Directory.CreateDirectory(_dir);
        // Pipe-delimited with a header row, one row per claim line, as CMS ships them.
        File.WriteAllLines(Path.Combine(_dir, "beneficiary_2022.csv"),
        [
            "BENE_ID|BENE_BIRTH_DT|BENE_SEX_IDENT_CD",
            "-1001|19500314|2",
            "-1002|19421101|1",
        ]);
        File.WriteAllLines(Path.Combine(_dir, "carrier.csv"),
        [
            "BENE_ID|CLM_ID|CLM_FROM_DT|CLM_THRU_DT|CARR_CLM_BLG_NPI_NUM|PRF_PHYSN_NPI|PRNCPAL_DGNS_CD|ICD_DGNS_CD1|ICD_DGNS_CD2|LINE_NUM|HCPCS_CD|HCPCS_1ST_MDFR_CD|LINE_SRVC_CNT|LINE_SBMTD_CHRG_AMT|LINE_PLACE_OF_SRVC_CD|LINE_1ST_EXPNS_DT|LINE_NCH_PMT_AMT",
            "-1001|C1|20220110|20220110|1234567890|1987654321|E119|E119|I10|1|99213|25|1|135.50|11|20220110|80.12",
            "-1001|C1|20220110|20220110|1234567890|1987654321|E119|E119|I10|2|83036||1|40.00|11|20220110|12.01",
            "-1002|C2|20220301|20220301|1234567890|1987654321|I10|I10||1|99214||2|210.00|22|20220301|99.00",
        ]);
        File.WriteAllLines(Path.Combine(_dir, "outpatient.csv"),
        [
            "BENE_ID|CLM_ID|CLM_FROM_DT|CLM_THRU_DT|ORG_NPI_NUM|AT_PHYSN_NPI|PRNCPAL_DGNS_CD|ICD_DGNS_CD1|CLM_FREQ_CD|REV_CNTR|HCPCS_CD|REV_CNTR_UNIT_CNT|REV_CNTR_TOT_CHRG_AMT",
            "-1001|O1|20220205|20220205|1111111111|2222222222|R0789|R0789|1|0450|99284|1|1200.00",
            "-1001|O1|20220205|20220205|1111111111|2222222222|R0789|R0789|1|0300|80053|1|150.00",
            "-1002|O2|20220206|20220206|1111111111|2222222222|R0789|R0789|1|||1|0",
        ]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RowsSharingAClaimId_BecomeOneClaimWithItsLines()
    {
        var result = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        var c1 = result.Claims.Single(c => c.ClaimId == "CMS-P-C1");
        Assert.Equal("Professional", c1.ClaimType);
        Assert.Equal(["99213", "83036"], c1.Lines.Select(l => l.ProcedureCode).ToArray());
        Assert.Equal(["25"], c1.Lines[0].Modifiers);
        Assert.Equal(175.50m, c1.TotalCharges);
        Assert.Equal("E11.9", c1.PrimaryDiagnosisCode);
        Assert.Equal(["I10"], c1.SecondaryDiagnosisCodes);
        Assert.Equal("11", c1.PlaceOfService);
        Assert.Equal(new DateTime(2022, 1, 10), c1.DateOfService);

        var o1 = result.Claims.Single(c => c.ClaimId == "CMS-O-O1");
        Assert.Equal("Institutional", o1.ClaimType);
        Assert.Equal("0131", o1.BillType);
        Assert.Equal(["0450", "0300"], o1.Lines.Select(l => l.RevenueCode!).ToArray());
    }

    [Fact]
    public void AClaimWithNoBillableLine_IsSkipped_AndCounted()
    {
        var result = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        Assert.DoesNotContain(result.Claims, c => c.ClaimId == "CMS-O-O2");
        Assert.Equal(1, result.ClaimsSkipped);
        Assert.Equal(3, result.ClaimsAvailable);
        Assert.Equal(6, result.RowsRead);
    }

    [Fact]
    public void ABeneficiarysClaims_ShareOneMember_WithTheFilesBirthDateAndSex()
    {
        var result = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        var mine = result.Claims.Where(c => c.ClaimId is "CMS-P-C1" or "CMS-O-O1").Select(c => c.Member).Distinct().ToList();
        var member = Assert.Single(mine);
        Assert.Equal(new DateTime(1950, 3, 14), member.DateOfBirth.Date);
        Assert.Equal("F", member.Gender);
        Assert.StartsWith("CMS", member.MemberId);
        Assert.Equal(2, result.Beneficiaries);
    }

    [Fact]
    public void ClaimsCarryNoExpectedOutcome_SoTheyScoreUnspecified()
    {
        var result = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        Assert.All(result.Claims, c =>
            Assert.Null(MccWorkflowValidation.ExpectedValidationFor(c).ExpectedOutcome));
    }

    [Fact]
    public void ASmallerRun_SamplesEachClaimTypeInProportion_Deterministically()
    {
        var first = CmsSyntheticCorpus.Load(_dir, 2, seed: 7, TextWriter.Null);
        var again = CmsSyntheticCorpus.Load(_dir, 2, seed: 7, TextWriter.Null);

        Assert.Equal(2, first.Claims.Count);
        Assert.Equal(1, first.Professional);
        Assert.Equal(1, first.Outpatient);
        Assert.Equal(first.Claims.Select(c => c.ClaimId), again.Claims.Select(c => c.ClaimId));
    }

    [Fact]
    public void ResetMemberCoverage_GivesEachMemberOneOpenCoverageSpan()
    {
        var result = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        CmsSyntheticCorpus.ResetMemberCoverage(result.Claims, new DateTime(2026, 11, 23));

        Assert.All(result.Claims.Select(c => c.Member).Distinct(), m =>
        {
            Assert.Equal(new DateTime(2024, 1, 1), m.CoverageEffectiveDate.Date);
            Assert.Null(m.CoverageTermDate);
            Assert.Empty(m.Coverages);
            Assert.Equal(m.MemberId, m.SubscriberId);
        });
    }

    [Fact]
    public void AMissingLineColumn_FailsWithTheHeader()
    {
        File.WriteAllLines(Path.Combine(_dir, "inpatient.csv"), ["BENE_ID|CLM_ID|CLM_FROM_DT|SOMETHING_ELSE", "-1|I1|20220101|x"]);

        var ex = Assert.Throws<InvalidOperationException>(() => CmsSyntheticCorpus.Load(_dir, 100, 42, TextWriter.Null));

        Assert.Contains("SOMETHING_ELSE", ex.Message);
    }

    [Fact]
    public void GzippedFiles_ReadTheSameAsPlainOnes()
    {
        var plain = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);
        foreach (var file in Directory.GetFiles(_dir))
        {
            using (var input = File.OpenRead(file))
            using (var output = new System.IO.Compression.GZipStream(File.Create(file + ".gz"), System.IO.Compression.CompressionLevel.Fastest))
            {
                input.CopyTo(output);
            }

            File.Delete(file);
        }

        var zipped = CmsSyntheticCorpus.Load(_dir, 100, seed: 42, TextWriter.Null);

        Assert.Equal(plain.Claims.Select(c => (c.ClaimId, c.TotalCharges, c.Lines.Count)), zipped.Claims.Select(c => (c.ClaimId, c.TotalCharges, c.Lines.Count)));
        Assert.Equal(plain.Beneficiaries, zipped.Beneficiaries);
        Assert.Equal(new DateTime(1950, 3, 14), zipped.Claims.Single(c => c.ClaimId == "CMS-P-C1").Member.DateOfBirth.Date);
    }

    [Theory]
    [InlineData("20220110", 2022, 1, 10)]
    [InlineData("2022-01-10", 2022, 1, 10)]
    [InlineData("10-Jan-2022", 2022, 1, 10)]
    public void Dates_ReadInTheFormatsCmsFilesUse(string raw, int y, int m, int d)
    {
        Assert.Equal(new DateTime(y, m, d), CmsSyntheticCorpus.ParseDate(raw));
    }
}
