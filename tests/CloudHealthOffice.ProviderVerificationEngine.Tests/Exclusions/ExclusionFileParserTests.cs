using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Xunit;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

public class ExclusionFileParserTests
{
    [Fact]
    public void CsvReader_HandlesQuotedCommasEscapedQuotesAndEmbeddedNewlines()
    {
        var csv = "a,b,c\r\n\"x, y\",\"say \"\"hi\"\"\",\"line1\nline2\"\n1,,3";
        var rows = CsvStreamReader.ReadRows(new StringReader(csv)).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(["x, y", "say \"hi\"", "line1\nline2"], rows[1]);
        Assert.Equal(["1", "", "3"], rows[2]);
    }

    [Fact]
    public void Leie_ParsesFields_QuotedCommas_AndZeroSentinels()
    {
        var csv = ExclusionTestData.Leie(
            ExclusionTestData.LeieRow(last: "SMITH, JR", first: "JOHN", npi: "1234567893", dob: "19650412"),
            ExclusionTestData.LeieRow(bus: "ACME HOME HEALTH, L.L.C.", npi: "0000000000"));

        var stats = new ExclusionParseStats();
        var records = LeieCsvParser.Parse(new StringReader(csv), stats).ToList();

        Assert.Equal(2, records.Count);
        var person = records[0];
        Assert.Equal(ExclusionScreeningSource.OigLeie, person.Source);
        Assert.Equal("SMITH, JR", person.LastName);
        Assert.Equal("SMITH", person.NormalizedLastName);   // suffix dropped
        Assert.Equal("JOHN", person.NormalizedFirstName);
        Assert.Equal("1234567893", person.Npi);
        Assert.Equal("19650412", person.DobKey);
        Assert.Equal("100 MAIN ST, STE 2", person.Address); // quoted comma stays in field
        Assert.Equal("78701", person.Zip);                  // later columns not shifted
        Assert.Equal("1128a1", person.ExclusionType);
        Assert.Equal(new DateTime(2020, 1, 15), person.ExclusionDate);
        Assert.Null(person.EndDate);                         // 00000000
        Assert.Null(person.WaiverDate);

        var org = records[1];
        Assert.Null(org.Npi);                                // 0000000000
        Assert.Equal("ACME HOME HEALTH", org.NormalizedBusinessName);
        Assert.Equal(2, stats.RowsRead);
    }

    [Fact]
    public void Leie_MapsByHeaderName_WhenColumnsAreReordered()
    {
        var csv = "NPI,EXCLTYPE,BUSNAME,LASTNAME,FIRSTNAME,DOB,EXCLDATE,REINDATE\n" +
                  "1234567893,1128b4,,DOE,JANE,19700101,20210301,00000000\n";

        var record = Assert.Single(LeieCsvParser.Parse(new StringReader(csv)));

        Assert.Equal("1234567893", record.Npi);
        Assert.Equal("DOE", record.NormalizedLastName);
        Assert.Equal("JANE", record.NormalizedFirstName);
        Assert.Equal("19700101", record.DobKey);
        Assert.Equal("1128b4", record.ExclusionType);
    }

    [Fact]
    public void Leie_RejectsFileWithoutExpectedHeader()
    {
        var html = "<html><body>Service unavailable</body></html>\n";
        Assert.Throws<ExclusionFileFormatException>(() => LeieCsvParser.Parse(new StringReader(html)).ToList());
    }

    [Fact]
    public void SamExtract_MapsByHeaderName_IndividualsFirmsAndStatus()
    {
        // Column order deliberately differs from the published file.
        var csv =
            "NPI,Record Status,Classification,Name,First,Middle,Last,Exclusion Type,Exclusion Program,Excluding Agency,Active Date,Termination Date,Unique Entity ID,CAGE\n" +
            "1234567893,Active,Individual,\"DOE, JOHN\",JOHN,Q,DOE,Prohibition/Restriction,Reciprocal,HHS,01/15/2021,Indefinite,,\n" +
            ",Active,Firm,\"Acme Medical Supply, Inc.\",,,,Ineligible (Proceedings Pending),Reciprocal,HHS,2022-03-01,12/31/2099,ABC123DEF456,1A2B3\n" +
            "1497758544,Inactive,Individual,,MARY,,ROE,Prohibition/Restriction,Reciprocal,HHS,01/01/2015,01/01/2016,,\n";

        var records = SamExtractCsvParser.Parse(new StringReader(csv)).ToList();

        Assert.Equal(3, records.Count);
        var john = records[0];
        Assert.Equal(ExclusionScreeningSource.SamGov, john.Source);
        Assert.Equal("1234567893", john.Npi);
        Assert.Equal("DOE", john.NormalizedLastName);
        Assert.Null(john.NormalizedBusinessName);
        Assert.Null(john.EndDate);                 // Indefinite
        Assert.Equal(new DateTime(2021, 1, 15), john.ExclusionDate);
        Assert.True(john.IsActive);

        var firm = records[1];
        Assert.Equal("ACME MEDICAL SUPPLY", firm.NormalizedBusinessName);
        Assert.Equal("ABC123DEF456", firm.UeiSam);
        Assert.Equal(new DateTime(2099, 12, 31), firm.EndDate);

        Assert.False(records[2].IsActive);
    }

    [Fact]
    public void SamExtract_NameOnlyIndividual_ParsesLastCommaFirstForm()
    {
        var csv = "Classification,Name,Exclusion Type,NPI\n" +
                  "Individual,\"DOE, JOHN Q\",Prohibition/Restriction,\n" +
                  "Individual,MARY ROE,Prohibition/Restriction,\n";

        var records = SamExtractCsvParser.Parse(new StringReader(csv)).ToList();

        Assert.Equal("DOE", records[0].NormalizedLastName);
        Assert.Equal("JOHN", records[0].NormalizedFirstName);
        Assert.Equal("Q", records[0].MiddleName);
        Assert.Null(records[0].NormalizedBusinessName);
        Assert.Equal("ROE", records[1].NormalizedLastName);
        Assert.Equal("MARY", records[1].NormalizedFirstName);
    }

    [Fact]
    public void SamExtract_RejectsUnexpectedContent()
    {
        var json = "{\"error\":{\"code\":\"API_KEY_INVALID\"}}";
        Assert.Throws<ExclusionFileFormatException>(() => SamExtractCsvParser.Parse(new StringReader(json)).ToList());
    }

    [Theory]
    [InlineData("Acme Home Health, LLC", "ACME HOME HEALTH")]
    [InlineData("ACME HOME HEALTH L.L.C.", "ACME HOME HEALTH")]
    [InlineData("The Acme Home-Health Inc.", "ACME HOME HEALTH")]
    [InlineData("Acme Home Health Co., Inc", "ACME HOME HEALTH")]
    [InlineData("Smith & Jones Medical P.C.", "SMITH AND JONES MEDICAL")]
    [InlineData("Clínica Peña, PLLC", "CLINICA PENA")]
    public void BusinessNameNormalization_StripsPunctuationAndEntitySuffixes(string raw, string expected)
    {
        Assert.Equal(expected, ExclusionNameNormalizer.NormalizeBusinessName(raw));
    }

    [Theory]
    [InlineData("O'Brien", "OBRIEN")]
    [InlineData("de la Cruz", "DELACRUZ")]
    [InlineData("Smith Jr.", "SMITH")]
    [InlineData("Muñoz", "MUNOZ")]
    public void PersonNameNormalization(string raw, string expected)
    {
        Assert.Equal(expected, ExclusionNameNormalizer.NormalizePersonName(raw, stripSuffixes: true));
    }
}
