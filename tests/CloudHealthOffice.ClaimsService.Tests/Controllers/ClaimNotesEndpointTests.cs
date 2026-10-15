using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimsService.Models;
using ClaimsService.Repositories;
using CloudHealthOffice.Infrastructure.Security;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests;

/// <summary>An in-memory <see cref="IClaimNoteRepository"/> for the API tests.</summary>
public sealed class InMemoryClaimNoteRepository : IClaimNoteRepository
{
    private readonly ConcurrentQueue<ClaimNote> _notes = new();

    public IReadOnlyCollection<ClaimNote> All => _notes.ToArray();

    public Task<ClaimNote> AppendAsync(ClaimNote note, CancellationToken ct = default)
    {
        _notes.Enqueue(note);
        return Task.FromResult(note);
    }

    public Task<IReadOnlyList<ClaimNote>> ListByClaimAsync(string claimId, string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClaimNote>>(_notes
            .Where(n => n.ClaimId == claimId && n.TenantId == tenantId)
            .OrderBy(n => n.CreatedAt)
            .ToList());
}

/// <summary>
/// <c>GET/POST /api/claims/{id}/notes</c>: append-only notes, with the author
/// and time taken from the caller's token and the server clock.
/// </summary>
public class ClaimNotesEndpointTests : IClassFixture<ClaimsApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ClaimsApiFactory _factory;

    public ClaimNotesEndpointTests(ClaimsApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string subject = "examiner-7", params string[] roles)
    {
        var client = _factory.CreateDefaultClient(new ChoDevelopmentTokenHandler(subject, roles));
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        return client;
    }

    private string SeedClaim()
    {
        var id = "note-claim-" + Guid.NewGuid().ToString("N")[..8];
        _factory.ClaimRepository.GetByIdAsync(id).Returns(new Claim
        {
            Id = id,
            TenantId = "test-tenant",
            ClaimNumber = "CLM-" + id,
            Status = ClaimStatus.Pended,
            MemberId = "m1",
            BillingProviderNPI = "1234567890",
        });
        return id;
    }

    [Fact]
    public async Task Add_StampsAuthorAndTime_FromTheCaller_AndListsInOrder()
    {
        var id = SeedClaim();
        var client = Client("examiner-7", ChoRolePermissions.ClaimsExaminer);
        var before = DateTime.UtcNow;

        var first = await client.PostAsJsonAsync($"/api/claims/{id}/notes",
            new { category = "providercontact", text = "  Called the office; corrected claim coming.  ", authorId = "someone-else" }, Json);
        var second = await client.PostAsJsonAsync($"/api/claims/{id}/notes",
            new { category = "Decision", text = "Paying line 1; second unit of 66984 denied per MUE." }, Json);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var created = (await first.Content.ReadFromJsonAsync<ClaimNote>(Json))!;
        Assert.Equal("examiner-7", created.AuthorId);
        Assert.Equal("ProviderContact", created.Category);
        Assert.Equal("Called the office; corrected claim coming.", created.Text);
        Assert.Equal("CLM-" + id, created.ClaimNumber);
        Assert.Equal("test-tenant", created.TenantId);
        Assert.True(created.CreatedAt >= before);

        var listed = (await client.GetFromJsonAsync<List<ClaimNote>>($"/api/claims/{id}/notes", Json))!;
        Assert.Equal(new[] { "ProviderContact", "Decision" }, listed.Select(n => n.Category));
    }

    [Fact]
    public async Task Add_DefaultsToInternal_WhenNoCategoryIsGiven()
    {
        var id = SeedClaim();

        var response = await Client().PostAsJsonAsync($"/api/claims/{id}/notes", new { text = "FYI" }, Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Internal", (await response.Content.ReadFromJsonAsync<ClaimNote>(Json))!.Category);
    }

    [Theory]
    [InlineData("Gossip", "ok")]
    [InlineData("Internal", "")]
    [InlineData("Internal", "   ")]
    public async Task Add_RejectsAnUnknownCategoryOrEmptyText(string category, string text)
    {
        var id = SeedClaim();

        var response = await Client().PostAsJsonAsync($"/api/claims/{id}/notes", new { category, text }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(_factory.NoteRepository.All, n => n.ClaimId == id);
    }

    [Fact]
    public async Task Add_RejectsTextOverTheLimit()
    {
        var id = SeedClaim();

        var response = await Client().PostAsJsonAsync($"/api/claims/{id}/notes",
            new { text = new string('x', 4001) }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Add_ToAClaimThatDoesNotExist_Is404()
    {
        var response = await Client().PostAsJsonAsync("/api/claims/no-such-claim/notes", new { text = "hello" }, Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Add_NeedsClaimsWork_ButReadingNeedsOnlyClaimsRead()
    {
        var id = SeedClaim();
        var viewer = Client("viewer-1", ChoRolePermissions.MemberServices);

        var add = await viewer.PostAsJsonAsync($"/api/claims/{id}/notes", new { text = "hello" }, Json);
        var list = await viewer.GetAsync($"/api/claims/{id}/notes");

        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task ThereIsNoEditOrDelete()
    {
        var id = SeedClaim();
        var client = Client();
        var created = (await (await client.PostAsJsonAsync($"/api/claims/{id}/notes", new { text = "v1" }, Json))
            .Content.ReadFromJsonAsync<ClaimNote>(Json))!;

        var put = await client.PutAsJsonAsync($"/api/claims/{id}/notes/{created.Id}", new { text = "v2" }, Json);
        var delete = await client.DeleteAsync($"/api/claims/{id}/notes/{created.Id}");

        Assert.Contains(put.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Contains(delete.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Equal("v1", Assert.Single(_factory.NoteRepository.All, n => n.ClaimId == id).Text);
    }
}
