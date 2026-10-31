using ClaimsService.Import.Cpcds;
using FluentAssertions;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;

public class CpcdsClaimFileParserTests
{
    [Fact]
    public void Sample_file_groups_rows_into_claims_in_file_order()
    {
        var parsed = CpcdsClaimFileParser.Parse(CpcdsTestFiles.Sample(), columnMap: null);

        parsed.FileErrors.Should().BeEmpty();
        parsed.UnrecognizedColumns.Should().BeEmpty();
        parsed.Delimiter.Should().Be(',');
        parsed.RowsRead.Should().Be(6);
        parsed.Claims.Select(c => c.ClaimId).Should().Equal("PRO-1001", "PRO-1002", "INP-2001", "DEN-3001");
        parsed.Claims[0].Rows.Should().HaveCount(2);
        parsed.Claims[0].Rows[0].LineNumber.Should().Be(2, "line 1 is the header");
    }

    [Fact]
    public void Quoted_fields_keep_their_delimiters_quotes_and_newlines()
    {
        var content = "Claim unique identifier,Member id,Claim type,Claim service start date,Claim billing provider NPI,Diagnosis description\n" +
                      "C-1,M-1,professional,2026-01-01,1234567893,\"Pain, \"\"acute\"\"\nsecond line\"\n";

        var parsed = CpcdsClaimFileParser.Parse(content, null);

        parsed.Claims.Should().ContainSingle();
        parsed.Claims[0].Rows[0].Get(CpcdsClaimColumn.DiagnosisDescription)
            .Should().Be("Pain, \"acute\"\nsecond line");
    }

    [Theory]
    [InlineData('|')]
    [InlineData('\t')]
    public void Pipe_and_tab_delimited_files_are_detected_from_the_header(char delimiter)
    {
        var d = delimiter.ToString();
        var content = string.Join(d, "CLAIM_UNIQUE_IDENTIFIER", "member id", "Claim Type", "claim service start date", "Claim billing provider NPI") + "\r\n" +
                      string.Join(d, "C-9", "M-9", "oral", "2026-01-01", "1234567893") + "\r\n";

        var parsed = CpcdsClaimFileParser.Parse(content, null);

        parsed.Delimiter.Should().Be(delimiter);
        parsed.FileErrors.Should().BeEmpty("header names match ignoring case, spaces and punctuation");
        parsed.Claims.Single().Rows.Single().Get(CpcdsClaimColumn.ClaimType).Should().Be("oral");
    }

    [Fact]
    public void A_leading_byte_order_mark_is_not_part_of_the_first_header()
    {
        var content = "﻿Claim unique identifier,Member id,Claim type,Claim service start date,Claim billing provider NPI\nC-1,M-1,oral,2026-01-01,1234567893\n";

        CpcdsClaimFileParser.Parse(content, null).FileErrors.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_required_column_is_a_file_error_naming_it()
    {
        var content = "Claim unique identifier,Claim type,Claim service start date,Claim billing provider NPI\nC-1,oral,2026-01-01,1234567893\n";

        var parsed = CpcdsClaimFileParser.Parse(content, null);

        parsed.FileErrors.Should().ContainSingle().Which.Should().Contain("\"Member id\"");
        parsed.Claims.Should().BeEmpty();
    }

    [Fact]
    public void A_column_map_names_the_files_own_headers()
    {
        var content = "CLM_ID,MBR_KEY,Claim type,Claim service start date,Claim billing provider NPI,RANDOM\nC-1,QNXT-77,oral,2026-01-01,1234567893,x\n";
        var map = new Dictionary<string, string>
        {
            ["Claim unique identifier"] = "CLM_ID",
            ["MemberId"] = "MBR_KEY",
        };

        var parsed = CpcdsClaimFileParser.Parse(content, map);

        parsed.FileErrors.Should().BeEmpty();
        parsed.UnrecognizedColumns.Should().Equal("RANDOM");
        parsed.Claims.Single().Rows.Single().Get(CpcdsClaimColumn.MemberId).Should().Be("QNXT-77");
    }

    [Fact]
    public void Rows_without_a_claim_id_are_reported_and_blank_lines_ignored()
    {
        var content = "Claim unique identifier,Member id,Claim type,Claim service start date,Claim billing provider NPI\n" +
                      "C-1,M-1,oral,2026-01-01,1234567893\n\n,,,,\n,M-2,oral,2026-01-01,1234567893\n";

        var parsed = CpcdsClaimFileParser.Parse(content, null);

        parsed.Claims.Should().ContainSingle();
        parsed.OrphanRows.Should().Equal(5);
    }

    [Fact]
    public void An_empty_file_is_a_file_error()
    {
        CpcdsClaimFileParser.Parse(string.Empty, null).FileErrors.Should().ContainSingle();
    }
}
