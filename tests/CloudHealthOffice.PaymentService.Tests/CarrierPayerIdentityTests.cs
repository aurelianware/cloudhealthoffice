using CloudHealthOffice.PlanConfiguration;
using PaymentService.Models;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

/// <summary>
/// Who an FFS payment run says is paying: the ACH file's originating entity and
/// the 835's payer (plan-configuration.md §12.3).
///
/// <para>
/// Both used to come from the deployment's own configuration — <c>Nacha:*</c>
/// for the file, <c>Payer:Name</c> and <c>Payer:Id</c> for the remittance — one
/// identity for every tenant in a deployment. The carrier is now asked first,
/// and configuration stands wherever it states nothing, so a deployment that
/// has not filled a carrier in is unchanged.
/// </para>
///
/// <para>
/// The constraint that matters more than either: a provider reassociates the
/// deposit with the remittance on the originating company id, which is the
/// 835's TRN03. A carrier that states a company id other than
/// <c>Era:OriginatingCompanyId</c> would break that reassociation for every
/// provider in the run, so the run is refused rather than sent.
/// </para>
/// </summary>
public class CarrierPayerIdentityTests
{
    private const string Npi = "1111111111";

    private static CarrierIdentity Carrier(
        string? companyId = null, string? origin = null, string? legalName = null, string? payerId = null) => new()
    {
        Id = $"{FfsRunHarness.Tenant}|HMO",
        Code = "HMO",
        Name = "the HMO",
        LegalName = legalName,
        NachaImmediateOrigin = origin,
        NachaCompanyIdentification = companyId,
        PayerIds = payerId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["835"] = payerId },
        IsDefault = true,
    };

    // The header fields this PR changes, read off the file the way the other
    // FFS EFT tests do: the file header's immediate origin and origin name, and
    // the batch header's company name and company id (= the 835's TRN03).
    private static string[] Records(FfsEftFileOutcome outcome) =>
        outcome.File!.Content.TrimEnd('\n').Split('\n');

    private static string ImmediateOrigin(string[] records) => records[0][13..23].Trim();
    private static string CompanyName(string[] records) => records[1][4..20].Trim();
    private static string CompanyId(string[] records) => records[1][40..50].Trim();

    private static FfsRunHarness Paying()
    {
        var h = new FfsRunHarness();
        h.Partner(Npi, "TP-A");
        h.Accounts.Eft(Npi);
        return h;
    }

    [Fact]
    public async Task The_file_is_originated_under_the_carriers_origin_and_letterhead()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant,
            Carrier(origin: "1742938475", legalName: "ACME HEALTH PLAN OF TEXAS INC"));
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var records = Records(await h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        Assert.Equal("1742938475", ImmediateOrigin(records));
        // The carrier's letterhead displaces the configured Nacha:CompanyName:
        // a stated carrier name is the more specific answer, and this is the
        // whole migration — the deployment's name stands only while no carrier
        // states one.
        // The batch header's company name is 16 characters, so a long legal
        // name is truncated there as any other would be.
        Assert.Equal("ACME HEALTH PLAN", CompanyName(records));
    }

    [Fact]
    public async Task A_carrier_that_states_nothing_originates_exactly_as_before()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant, Carrier());
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var records = Records(await h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        // With no Nacha:ImmediateOrigin configured either, the origin falls back
        // to the originating company id, as it did before carriers existed.
        Assert.Equal(FfsRunHarness.CompanyId, ImmediateOrigin(records));
        Assert.Equal(FfsRunHarness.CompanyId, CompanyId(records));
    }

    [Fact]
    public async Task A_carrier_company_id_equal_to_the_835s_is_accepted()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant, Carrier(companyId: FfsRunHarness.CompanyId));
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var records = Records(await h.EftFiles().GenerateAsync(run.Id, "approver-2"));

        Assert.Equal(FfsRunHarness.CompanyId, CompanyId(records));
    }

    [Fact]
    public async Task A_carrier_company_id_that_differs_from_the_835s_refuses_the_file()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant, Carrier(companyId: "1999999999"));
        var run = await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var generate = () => h.EftFiles().GenerateAsync(run.Id, "approver-2");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(generate);
        Assert.Contains("reassociate", ex.Message);
        Assert.Contains("HMO", ex.Message);
        Assert.Null((await h.Runs.GetByIdAsync(run.Id))?.EftFile);
    }

    [Fact]
    public async Task The_835_names_the_carrier_as_the_payer()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant,
            Carrier(legalName: "ACME HEALTH PLAN OF TEXAS INC", payerId: "66005"));

        await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var payment = Assert.Single(h.Payments.All);
        Assert.Equal("ACME HEALTH PLAN OF TEXAS INC", payment.PayerName);
        Assert.Equal("66005", payment.PayerId);
    }

    [Fact]
    public async Task A_carrier_with_no_835_payer_id_keeps_the_configured_one()
    {
        var h = Paying();
        h.Carriers.Add(FfsRunHarness.Tenant, Carrier(legalName: "ACME HEALTH PLAN OF TEXAS INC"));

        await h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        var payment = Assert.Single(h.Payments.All);
        Assert.Equal("ACME HEALTH PLAN OF TEXAS INC", payment.PayerName);
        Assert.Equal("CHO", payment.PayerId);
    }

    [Fact]
    public async Task An_unreachable_tenant_service_fails_the_run_rather_than_paying_under_the_configured_payer()
    {
        var h = Paying();
        h.Carriers.Unavailable = true;

        var execute = () => h.ExecuteRunAsync(FfsRunHarness.Claim("c1", Npi, 100m));

        await Assert.ThrowsAnyAsync<HttpRequestException>(execute);
    }
}
