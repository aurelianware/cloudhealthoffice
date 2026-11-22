using ClaimsService.Extracts;
using ClaimsService.Extracts.Cpcds;
using ClaimsService.Import.Cpcds;
using ClaimsService.Models;
using CloudHealthOffice.ClaimsService.Tests.Import.Cpcds;
using FluentAssertions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Extracts.Cpcds;

/// <summary>
/// The CPCDS export read back by CHO's own CPCDS importer: every sample claim
/// (professional, institutional, oral, pharmacy) imported, exported and
/// imported again comes back the same claim, with no column map.
/// </summary>
public class CpcdsExportRoundTripTests
{
    private static List<Claim> Import(string content)
    {
        var parsed = CpcdsClaimFileParser.Parse(content, null);
        parsed.FileErrors.Should().BeEmpty();
        parsed.UnrecognizedColumns.Should().BeEmpty("the export writes only columns the importer reads");
        return parsed.Claims.Select(record =>
        {
            var mapped = CpcdsClaimMapper.Map(
                record, record.Rows[0].Get(CpcdsClaimColumn.MemberId)!, new CpcdsImportOptions(), "importer");
            mapped.Errors.Should().BeEmpty($"{record.ClaimId} should import");
            return mapped.Claim!;
        }).ToList();
    }

    private static string Export(IEnumerable<Claim> claims)
    {
        var lines = new List<string> { ClaimExtractRows.Record(CpcdsClaimExportRows.Headers) };
        foreach (var claim in claims)
            lines.AddRange(CpcdsClaimExportRows.Rows(claim).Select(ClaimExtractRows.Record));
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static TheoryData<string> Samples => new() { "claims", "pharmacy" };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_claim_survives_export_and_reimport(string sample)
    {
        var original = Import(sample == "claims" ? CpcdsTestFiles.Sample() : CpcdsTestFiles.PharmacySample());
        original.Should().NotBeEmpty();

        var exported = original.Where(CpcdsClaimExportRows.Exports).ToList();
        exported.Should().HaveCount(original.Count, "every imported sample claim is adjudicated");
        var reloaded = Import(Export(exported)).ToDictionary(c => c.ClaimNumber);

        foreach (var before in exported)
        {
            var after = reloaded[before.ClaimNumber];
            after.ClaimType.Should().Be(before.ClaimType, before.ClaimNumber);
            after.Status.Should().Be(before.Status, before.ClaimNumber);
            after.MemberId.Should().Be(before.MemberId);
            after.BillingProviderNPI.Should().Be(before.BillingProviderNPI);
            after.RenderingProviderNPI.Should().Be(before.RenderingProviderNPI);
            after.ServiceDateFrom.Should().Be(before.ServiceDateFrom);
            after.ServiceDateTo.Should().Be(before.ServiceDateTo);
            after.PaidDate.Should().Be(before.PaidDate);
            after.TotalChargeAmount.Should().Be(before.TotalChargeAmount);
            after.OriginalClaimReference.Should().Be(before.OriginalClaimReference);

            after.AdjudicationResult!.PayerPayment.Should().Be(before.AdjudicationResult!.PayerPayment, before.ClaimNumber);
            after.AdjudicationResult.AllowedAmount.Should().Be(before.AdjudicationResult.AllowedAmount);
            after.AdjudicationResult.PatientResponsibility.Should().Be(before.AdjudicationResult.PatientResponsibility);
            after.AdjudicationResult.DeductibleAmount.Should().Be(before.AdjudicationResult.DeductibleAmount);
            after.AdjudicationResult.CopayAmount.Should().Be(before.AdjudicationResult.CopayAmount);
            after.AdjudicationResult.CoinsuranceAmount.Should().Be(before.AdjudicationResult.CoinsuranceAmount);

            after.DiagnosisCodes.Select(d => (d.Code, d.CodeQualifier, d.PresentOnAdmission))
                .Should().Equal(before.DiagnosisCodes.Select(d => (d.Code, d.CodeQualifier, d.PresentOnAdmission)), before.ClaimNumber);

            after.ClaimLines.Select(l => (l.LineNumber, l.ProcedureCode, l.RevenueCode, l.NationalDrugCode, l.ChargeAmount, l.Units,
                    l.DrugQuantity, string.Join(":", l.Modifiers), l.PlaceOfServiceCode))
                .Should().Equal(before.ClaimLines.Select(l => (l.LineNumber, l.ProcedureCode, l.RevenueCode, l.NationalDrugCode,
                    l.ChargeAmount, l.Units, l.DrugQuantity, string.Join(":", l.Modifiers), l.PlaceOfServiceCode)), before.ClaimNumber);
            after.ClaimLines.Select(l => l.Teeth.FirstOrDefault()?.ToothCode)
                .Should().Equal(before.ClaimLines.Select(l => l.Teeth.FirstOrDefault()?.ToothCode));

            if (before.Institutional is { } inst)
            {
                var back = after.Institutional!;
                back.FacilityTypeCode.Should().Be(inst.FacilityTypeCode);
                back.AdmissionDate.Should().Be(inst.AdmissionDate);
                back.DischargeDate.Should().Be(inst.DischargeDate);
                back.DrgCode.Should().Be(inst.DrgCode);
                back.PatientStatusCode.Should().Be(inst.PatientStatusCode);
                back.PrincipalProcedure?.Code.Should().Be(inst.PrincipalProcedure?.Code);
                back.OtherProcedures.Select(p => p.Code).Should().Equal(inst.OtherProcedures.Select(p => p.Code));
            }

            if (before.Pharmacy is { } rx)
                after.Pharmacy.Should().BeEquivalentTo(rx, before.ClaimNumber);
        }
    }

    [Fact]
    public void A_claim_still_in_adjudication_or_superseded_is_not_exported()
    {
        var claim = Import(CpcdsTestFiles.Sample()).First();

        claim.Status = ClaimStatus.Pended;
        CpcdsClaimExportRows.Rows(claim).Should().BeEmpty();

        claim.Status = ClaimStatus.Paid;
        claim.SupersededAt = DateTime.UtcNow;
        CpcdsClaimExportRows.Rows(claim).Should().BeEmpty();

        claim.SupersededAt = null;
        claim.VersionState = ClaimVersionState.Draft;
        CpcdsClaimExportRows.Rows(claim).Should().BeEmpty();
    }

    [Fact]
    public void A_voided_claim_is_exported_as_cancelled()
    {
        var claim = Import(CpcdsTestFiles.Sample()).First();
        claim.Status = ClaimStatus.Voided;

        var row = CpcdsClaimExportRows.Rows(claim).First();
        var at = (CpcdsClaimColumn c) => row[CpcdsClaimExportRows.Columns.ToList().IndexOf(c)];

        at(CpcdsClaimColumn.ClaimProcessingStatusCode).Should().Be("cancelled");
        at(CpcdsClaimColumn.ClaimPaymentStatusCode).Should().Be("paid");
    }

    [Fact]
    public void More_diagnoses_than_lines_ride_on_extra_rows_of_the_first_line()
    {
        var claim = Import(CpcdsTestFiles.Sample()).First();
        claim.ClaimLines = [claim.ClaimLines[0]];
        claim.DiagnosisCodes =
        [
            new DiagnosisCode { Code = "E119", PointerNumber = 1, CodeQualifier = "ABK" },
            new DiagnosisCode { Code = "I10", PointerNumber = 2, CodeQualifier = "ABF" },
            new DiagnosisCode { Code = "Z794", PointerNumber = 3, CodeQualifier = "ABF" },
        ];

        var reloaded = Import(Export([claim])).Single();

        reloaded.ClaimLines.Should().ContainSingle();
        reloaded.DiagnosisCodes.Select(d => d.Code).Should().Equal("E119", "I10", "Z794");
    }

    [Fact]
    public void Every_export_column_is_one_the_importer_reads_by_its_own_name()
    {
        CpcdsClaimExportRows.Headers.Should().OnlyHaveUniqueItems();
        var (positions, unrecognized) = CpcdsClaimColumns.Resolve(CpcdsClaimExportRows.Headers.ToArray(), null);
        unrecognized.Should().BeEmpty();
        positions.Keys.Should().BeEquivalentTo(CpcdsClaimExportRows.Columns);
    }
}
