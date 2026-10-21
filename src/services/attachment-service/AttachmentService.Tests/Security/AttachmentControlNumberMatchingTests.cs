using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AttachmentService.Models;
using AttachmentService.Services;
using AttachmentService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AttachmentService.Tests.Security;

/// <summary>
/// A 275 that names no record is matched by its attachment control number
/// (TRN02) to the claim whose 837 PWK06 announced it, or held under its
/// provider until that claim arrives, then rejected after the hold period.
/// </summary>
[Collection(nameof(AttachmentServiceCollection))]
public class AttachmentControlNumberMatchingTests
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string User = "provider-user-3";
    private const string Npi = "1234567893";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AttachmentServiceFactory _factory;

    public AttachmentControlNumberMatchingTests(AttachmentServiceFactory factory)
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

    private static MultipartFormDataContent Form(string controlNumber = "ACN-77", string providerId = Npi, bool withFile = true)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(controlNumber), "AttachmentControlNumber" },
            { new StringContent("PAYER-1"), "PayerId" },
            { new StringContent("Payer"), "PayerName" },
            { new StringContent(providerId), "ProviderId" },
            { new StringContent("SUB-1"), "SubscriberId" },
            { new StringContent("Medical Records"), "DocumentType" },
            { new StringContent("PDF"), "DocumentFormat" },
            { new StringContent("ISA*00*~ST*275*0001~TRN*1*ACN-77~SE*3*0001~"), "RawX12" },
        };
        if (withFile)
            form.Add(new ByteArrayContent("%PDF-1.4 records"u8.ToArray()), "file", "records.pdf");
        return form;
    }

    private static async Task<Attachment> Body(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Attachment>(Json))!;

    private UnmatchedAttachmentMatcher Matcher => _factory.Services.GetRequiredService<UnmatchedAttachmentMatcher>();

    [Fact]
    public async Task A275_ForAnAnnouncedControlNumber_IsFiledOnThatClaim()
    {
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-9", Npi);

        var response = await Client().PostAsync("/api/attachments", Form());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Body(response);
        Assert.Equal("CLM-9", created.ClaimId);
        Assert.Equal("Linked", created.Status);
        Assert.Equal("ACN-77", created.AttachmentControlNumber);
        var filed = Assert.Single(_factory.Documents.Files.Values);
        Assert.Equal(AttachmentParentType.Claim, filed.SubjectType);
        Assert.Equal("CLM-9", filed.SubjectId);
        Assert.Equal("ACN-77", filed.ExternalIds["AttachmentControlNumber"]);
        // claims-service named the claim; it was not looked up a second time.
        Assert.Single(_factory.Parents.Requests);
    }

    [Fact]
    public async Task ControlNumbers_AreMatchedOnlyWithinTheTokensTenant()
    {
        _factory.Parents.AddControlNumber(OtherTenant, "ACN-77", "CLM-9", Npi);

        var response = await Client().PostAsync("/api/attachments", Form());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await Body(response);
        Assert.Null(created.ClaimId);
        Assert.Equal("Unmatched", created.Status);
    }

    [Fact]
    public async Task A275_BeforeItsClaim_IsHeldUnderItsProvider_ThenLinkedWhenTheClaimArrives()
    {
        var response = await Client().PostAsync("/api/attachments", Form());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var held = await Body(response);
        Assert.Equal("Unmatched", held.Status);
        Assert.Null(held.ClaimId);
        var filed = Assert.Single(_factory.Documents.Files);
        Assert.Equal(AttachmentParentType.Provider, filed.Value.SubjectType);
        Assert.Equal(Npi, filed.Value.SubjectId);

        var unmatched = await Client().GetFromJsonAsync<List<Attachment>>("/api/attachments/unmatched", Json);
        Assert.Equal(held.Id, Assert.Single(unmatched!).Id);

        // The 837 arrives; the next matcher run links the 275 to it.
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-9", Npi);
        var (linked, expired) = await Matcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal((1, 0), (linked, expired));
        var stored = _factory.Attachments.Items[held.Id];
        Assert.Equal("CLM-9", stored.ClaimId);
        Assert.Equal("Linked", stored.Status);
        Assert.Contains((AttachmentParentType.Claim, "CLM-9"), _factory.Documents.LinksOf(filed.Key));

        // Now opened from its claim like any other attachment.
        var download = await Client().GetAsync($"/api/attachments/{held.Id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task AHeld275_CanBeOpenedFromItsProvider_BeforeItIsMatched()
    {
        var held = await Body(await Client().PostAsync("/api/attachments", Form()));

        var download = await Client(Tenant, ChoRolePermissions.ClaimsExaminer).GetAsync($"/api/attachments/{held.Id}/download");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task AControlNumberSeveralClaimsAnnounced_IsMatchedByTheProvider()
    {
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-OTHER", "1111111112");
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-9", Npi);

        var created = await Body(await Client().PostAsync("/api/attachments", Form()));

        Assert.Equal("CLM-9", created.ClaimId);
    }

    [Fact]
    public async Task AControlNumberOtherProvidersAnnounced_IsHeld_NotGuessed()
    {
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-A", "1111111112");
        _factory.Parents.AddControlNumber(Tenant, "ACN-77", "CLM-B", "2222222224");

        var created = await Body(await Client().PostAsync("/api/attachments", Form()));

        Assert.Equal("Unmatched", created.Status);
        Assert.Null(created.ClaimId);
        Assert.Contains("held for review", created.Notes);
    }

    [Fact]
    public async Task AHeld275_IsRejectedAfterTheHoldPeriod()
    {
        var held = await Body(await Client().PostAsync("/api/attachments", Form()));
        _factory.Attachments.Items[held.Id].CreatedDate = DateTime.UtcNow.AddDays(-31);

        var (linked, expired) = await Matcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 1), (linked, expired));
        var stored = _factory.Attachments.Items[held.Id];
        Assert.Equal("Failed", stored.Status);
        Assert.Equal(AttachmentRejectionCode.NoMatchingClaim, stored.RejectionCode);
        Assert.Equal("2", AttachmentRejectionCode.ToTed01ErrorTypeCode(stored.RejectionCode));
    }

    [Fact]
    public async Task WhenClaimsServiceRefuses_NothingIsStored()
    {
        // A refusal, not an outage: a 5xx would open the shared client's
        // circuit breaker for the tests that follow.
        _factory.Parents.FailWith = HttpStatusCode.Forbidden;

        var response = await Client().PostAsync("/api/attachments", Form());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(_factory.Attachments.Items);
        Assert.Equal(0, _factory.Documents.Count);
    }

    [Fact]
    public async Task TheMatcher_LeavesAnAttachmentForTheNextRun_WhenClaimsServiceFails()
    {
        var held = await Body(await Client().PostAsync("/api/attachments", Form()));
        _factory.Parents.FailWith = HttpStatusCode.Forbidden;

        var (linked, expired) = await Matcher.RunOnceAsync(CancellationToken.None);

        Assert.Equal((0, 0), (linked, expired));
        Assert.Equal("Unmatched", _factory.Attachments.Items[held.Id].Status);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("-leading")]
    [InlineData("x<script>")]
    public async Task AMalformedControlNumber_IsRejected(string controlNumber)
    {
        var response = await Client().PostAsync("/api/attachments", Form(controlNumber));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Attachments.Items);
        Assert.Empty(_factory.Parents.Requests);
    }

    [Fact]
    public async Task NoParentAndNoControlNumber_IsRejected()
    {
        var form = Form();
        var withoutAcn = new MultipartFormDataContent();
        foreach (var part in form.Where(p => p.Headers.ContentDisposition?.Name?.Trim('"') != "AttachmentControlNumber"))
            withoutAcn.Add(part);

        var response = await Client().PostAsync("/api/attachments", withoutAcn);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Attachments.Items);
    }

    [Fact]
    public async Task AHeld275_NeedsAProviderItCanBeFiledUnder()
    {
        var response = await Client().PostAsync("/api/attachments", Form(providerId: "NPI 123"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Attachments.Items);
    }

    [Fact]
    public async Task AnExplicitClaimId_StillWins_AndKeepsTheControlNumber()
    {
        _factory.Parents.Add(Tenant, AttachmentParentType.Claim, "CLM-1");
        var form = Form();
        form.Add(new StringContent("CLM-1"), "ClaimId");

        var created = await Body(await Client().PostAsync("/api/attachments", form));

        Assert.Equal("CLM-1", created.ClaimId);
        Assert.Equal("ACN-77", created.AttachmentControlNumber);
        Assert.DoesNotContain(_factory.Parents.Requests, r => r.RequestUri!.AbsolutePath.Contains("attachment-control-numbers"));
    }
}
