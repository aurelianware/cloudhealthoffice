using System.Text.Json;
using System.Text.Json.Serialization;
using CloudHealthOffice.PlanConfiguration;
using TenantService.Hierarchy;

namespace CloudHealthOffice.TenantService.Tests.Hierarchy;

/// <summary>
/// The carrier as the services acting in its name read it
/// (plan-configuration.md §12.3). Small decisions, but each one is a
/// consumer's fallback, so getting one wrong changes what goes out on a file or
/// a letter without anyone asking for it.
/// </summary>
public class CarrierIdentityTests
{
    private static CarrierIdentity Identity(string? correspondence = null, string? legal = null) => new()
    {
        Id = "t1|HMO", Code = "HMO", Name = "the HMO",
        CorrespondenceName = correspondence, LegalName = legal,
    };

    [Fact]
    public void Letterhead_prefers_the_correspondence_name_then_the_legal_name()
    {
        Identity(correspondence: "Acme Health Plan", legal: "ACME HEALTH PLAN OF TEXAS INC")
            .LetterheadName.Should().Be("Acme Health Plan");
        Identity(legal: "ACME HEALTH PLAN OF TEXAS INC")
            .LetterheadName.Should().Be("ACME HEALTH PLAN OF TEXAS INC");
    }

    [Fact]
    public void Letterhead_is_never_the_name_staff_call_the_carrier()
    {
        // Name is filled in for every carrier, the default one included, where
        // it is the tenant's own organization name. A consumer falling back on
        // it would start putting that name on files and letters the day carriers
        // existed; the point of step 2b is that nothing changes until someone
        // states a letterhead name.
        Identity().LetterheadName.Should().BeNull();
        Identity(correspondence: "   ").LetterheadName.Should().BeNull("blank is not stated");
    }

    [Fact]
    public void Letterhead_is_trimmed_because_a_pasted_name_carries_whitespace()
    {
        Identity(correspondence: "  Acme Health Plan \n").LetterheadName.Should().Be("Acme Health Plan");
    }

    [Fact]
    public void A_payer_id_is_per_transaction_and_absent_rather_than_empty()
    {
        var carrier = Identity() with
        {
            PayerIds = new Dictionary<string, string> { ["835"] = "66005", ["270"] = " 66005E ", ["837"] = "  " },
        };

        carrier.PayerIdFor("835").Should().Be("66005");
        carrier.PayerIdFor("270").Should().Be("66005E", "a pasted id carries whitespace");
        carrier.PayerIdFor("837").Should().BeNull("a blank id is not an id");
        carrier.PayerIdFor("834").Should().BeNull();
        carrier.PayerIdFor("835P").Should().BeNull("835P is a different transaction, not a spelling of 835");
    }

    [Fact]
    public void A_carrier_record_becomes_the_identity_consumers_read()
    {
        var carrier = new Carrier
        {
            Id = Carrier.IdFor("t1", "HMO"), TenantId = "t1", Code = "HMO", Name = "the HMO",
            LegalName = "ACME HEALTH PLAN OF TEXAS INC", TaxId = "742938475", NaicCode = "12345",
            NachaImmediateOrigin = "1742938475", NachaCompanyIdentification = "1742938475",
            CorrespondenceName = "Acme Health Plan",
            CorrespondenceAddress = new global::TenantService.Models.Address { City = "Austin", State = "TX" },
            PayerIds = new Dictionary<string, string> { ["835"] = "66005" },
            IsDefault = true,
        };

        var identity = carrier.ToIdentity();

        identity.Should().Match<CarrierIdentity>(i =>
            i.Id == carrier.Id && i.Code == "HMO" && i.LegalName == carrier.LegalName
            && i.TaxId == "742938475" && i.NachaImmediateOrigin == "1742938475"
            && i.NachaCompanyIdentification == "1742938475" && i.CorrespondenceName == "Acme Health Plan"
            && i.IsDefault && i.Active);
        identity.CorrespondenceAddress!.City.Should().Be("Austin");
        identity.PayerIdFor("835").Should().Be("66005");
    }

    [Fact]
    public void An_identity_does_not_share_the_records_payer_ids()
    {
        // The record is mutable and the directory caches the identity, so a
        // later write to the record must not reach a cached answer.
        var carrier = new Carrier { Id = "t1|HMO", TenantId = "t1", Code = "HMO", Name = "the HMO" };
        carrier.PayerIds["835"] = "66005";

        var identity = carrier.ToIdentity();
        carrier.PayerIds["835"] = "99999";

        identity.PayerIdFor("835").Should().Be("66005");
    }

    [Fact]
    public void A_serialized_carrier_reads_back_as_the_identity_the_record_describes()
    {
        // The directory reads `api/v1/carriers`, which serves the whole carrier
        // record, and deserializes it into CarrierIdentity. Nothing in the
        // compiler keeps those two shapes together, so this is what does: a
        // field renamed on Carrier without renaming it here would otherwise
        // reach every consumer as a carrier that silently states nothing.
        var carrier = new Carrier
        {
            Id = Carrier.IdFor("t1", "HMO"), TenantId = "t1", Code = "HMO", Name = "the HMO",
            LegalName = "ACME HEALTH PLAN OF TEXAS INC", TaxId = "742938475", NaicCode = "12345",
            NachaImmediateOrigin = "1742938475", NachaCompanyIdentification = "1742938475",
            CorrespondenceName = "Acme Health Plan",
            CorrespondenceAddress = new global::TenantService.Models.Address
            {
                Street = "100 Congress Ave", City = "Austin", State = "TX", ZipCode = "78701",
            },
            PayerIds = new Dictionary<string, string> { ["835"] = "66005" },
            IsDefault = true,
        };

        // The options the directory reads with: web defaults (camelCase, so the
        // record's own JsonPropertyName spellings line up) and string enums.
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var overTheWire = JsonSerializer.Deserialize<CarrierIdentity>(
            JsonSerializer.Serialize(carrier, json), json)!;

        overTheWire.Should().BeEquivalentTo(carrier.ToIdentity());
        overTheWire.CorrespondenceAddress!.Street.Should().Be("100 Congress Ave");
        overTheWire.PayerIdFor("835").Should().Be("66005");
    }

    [Fact]
    public async Task The_in_memory_directory_answers_the_default_carrier_and_none_for_another_tenant()
    {
        var directory = new InMemoryCarrierDirectory();
        directory.Add("t1", Identity(legal: "SECOND INC") with { Id = "t1|INS", Code = "INS", IsDefault = false });
        directory.Add("t1", Identity(legal: "FIRST INC") with { IsDefault = true });

        (await directory.DefaultAsync("t1"))!.LetterheadName.Should().Be("FIRST INC");
        (await directory.GetAsync("t1", "t1|INS"))!.Code.Should().Be("INS");
        (await directory.DefaultAsync("t2")).Should().BeNull();
    }

    [Fact]
    public async Task An_unavailable_directory_throws_rather_than_answering_that_there_is_no_carrier()
    {
        var directory = new InMemoryCarrierDirectory { Unavailable = true };

        await directory.Invoking(d => d.DefaultAsync("t1")).Should().ThrowAsync<HttpRequestException>();
    }
}
