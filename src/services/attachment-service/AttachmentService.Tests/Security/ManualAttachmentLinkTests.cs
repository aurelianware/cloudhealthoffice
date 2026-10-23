using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AttachmentService.Models;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;

namespace AttachmentService.Tests.Security;

/// <summary>
/// A held 275 can be filed on a claim by hand when the control numbers
/// disagree but a person can see where it belongs. The billing NPI still has
/// to agree: that check is the only thing standing between a sequential
/// control number and one provider's document landing on another provider's
/// patient's claim, and a person reading a screen cannot see further than the
/// matcher can.
/// </summary>
[Collection(nameof(AttachmentServiceCollection))]
public class ManualAttachmentLinkTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string User = "examiner-1";
    private const string Npi = "1234567893";
    private const string OtherNpi = "1987654324";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AttachmentServiceFactory _factory;

    public ManualAttachmentLinkTests(AttachmentServiceFactory factory)
    {
        _factory = factory;
        _factory.Reset();
    }

    private HttpClient Client(string tenant = Tenant, params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(User, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", tenant);
        return client;
    }

    /// <summary>A 275 held under its provider, as the intake path leaves one.</summary>
    private Attachment Held(string tenant = Tenant, string providerId = Npi, string status = "Unmatched")
    {
        var attachment = _factory.Attachments.Seed(tenant, claimId: null);
        attachment.ProviderId = providerId;
        attachment.Status = status;
        attachment.AttachmentControlNumber = "ACN-TYPO";
        attachment.DocumentId = null;
        return attachment;
    }

    private static Task<HttpResponseMessage> Link(HttpClient client, string id, string? claimId, string? reason = null) =>
        client.PostAsJsonAsync($"/api/attachments/{id}/link", new { ClaimId = claimId, Reason = reason });

    [Fact]
    public async Task A_held_275_is_filed_on_a_claim_of_the_same_provider()
    {
        var held = Held();
        _factory.Parents.AddClaim(Tenant, "CLM-42", Npi);

        var response = await Link(Client(), held.Id, "CLM-42", "Provider mistyped the control number.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<Attachment>(Json))!;
        Assert.Equal("CLM-42", body.ClaimId);
        Assert.Equal("Linked", body.Status);

        var stored = _factory.Attachments.Items[held.Id];
        Assert.Equal("CLM-42", stored.ClaimId);
        Assert.Equal("Linked", stored.Status);
        Assert.Equal(User, stored.LastUpdatedBy);
    }

    [Fact]
    public async Task The_reason_is_kept_on_the_attachment()
    {
        var held = Held();
        _factory.Parents.AddClaim(Tenant, "CLM-42", Npi);

        await Link(Client(), held.Id, "CLM-42", "Matched by patient and date of service.");

        Assert.Contains("Matched by patient and date of service.", _factory.Attachments.Items[held.Id].Notes);
    }

    [Fact]
    public async Task A_claim_billed_by_another_provider_is_refused()
    {
        var held = Held(providerId: Npi);
        _factory.Parents.AddClaim(Tenant, "CLM-OTHER", OtherNpi);

        var response = await Link(Client(), held.Id, "CLM-OTHER");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var stored = _factory.Attachments.Items[held.Id];
        Assert.Null(stored.ClaimId);
        Assert.Equal("Unmatched", stored.Status);
    }

    [Fact]
    public async Task Another_tenants_claim_is_refused()
    {
        var held = Held();
        _factory.Parents.AddClaim(OtherTenant, "CLM-ELSEWHERE", Npi);

        var response = await Link(Client(), held.Id, "CLM-ELSEWHERE");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unmatched", _factory.Attachments.Items[held.Id].Status);
    }

    [Fact]
    public async Task Another_tenants_attachment_is_not_found()
    {
        var held = Held(OtherTenant);
        _factory.Parents.AddClaim(Tenant, "CLM-42", Npi);

        var response = await Link(Client(), held.Id, "CLM-42");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_attachment_that_is_already_linked_is_refused()
    {
        var held = Held(status: "Linked");
        _factory.Parents.AddClaim(Tenant, "CLM-42", Npi);

        var response = await Link(Client(), held.Id, "CLM-42");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_claim_that_does_not_exist_is_refused()
    {
        var held = Held();

        var response = await Link(Client(), held.Id, "CLM-NOPE");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unmatched", _factory.Attachments.Items[held.Id].Status);
    }

    [Fact]
    public async Task A_missing_claim_id_is_refused()
    {
        var held = Held();

        var response = await Link(Client(), held.Id, claimId: "  ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Claims_service_being_unreachable_links_nothing()
    {
        var held = Held();
        _factory.Parents.AddClaim(Tenant, "CLM-42", Npi);
        _factory.Parents.FailWith = HttpStatusCode.InternalServerError;

        var response = await Link(Client(), held.Id, "CLM-42");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Unmatched", _factory.Attachments.Items[held.Id].Status);
    }
}
