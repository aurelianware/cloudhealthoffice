using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationService.Models;
using Authorization = AuthorizationService.Models.Authorization;
using NSubstitute;

namespace CloudHealthOffice.AuthorizationService.Tests;

public class AuthorizationLifecycleTests : IClassFixture<AuthorizationApiFactory>
{
    private readonly AuthorizationApiFactory _factory;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };

    public AuthorizationLifecycleTests(AuthorizationApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-ID", "test-tenant");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.IssueToken());
    }

    private Authorization Seed(AuthorizationStatus status)
    {
        var authorization = new Authorization
        {
            Id = Guid.NewGuid().ToString(), TenantId = "test-tenant", Status = status,
            AuthorizationNumber = "LIFECYCLE-" + Guid.NewGuid().ToString("N"),
            RequestedServices = new() { new() { ProcedureCode = "72148" } },
        };
        _factory.AuthorizationRepository.GetByIdAsync(authorization.Id).Returns(authorization);
        _factory.AuthorizationRepository.UpdateAsync(Arg.Is<Authorization>(a => a.Id == authorization.Id)).Returns(authorization);
        return authorization;
    }

    [Theory]
    [InlineData(AuthorizationStatus.Cancelled, "response")]
    [InlineData(AuthorizationStatus.Expired, "response")]
    [InlineData(AuthorizationStatus.Denied, "response")]
    [InlineData(AuthorizationStatus.Modified, "response")]
    [InlineData(AuthorizationStatus.Cancelled, "status")]
    [InlineData(AuthorizationStatus.Expired, "status")]
    [InlineData(AuthorizationStatus.Denied, "status")]
    public async Task FinalRecordsCannotBecomeApprovedThroughOrdinaryDecisionEndpoints(AuthorizationStatus status, string route)
    {
        var authorization = Seed(status);
        var body = new { controlNumber = "forbidden", status = "Approved", reviewDecision = "A1" };
        var response = route == "response"
            ? await _client.PostAsJsonAsync($"/api/authorizations/{authorization.Id}/response", body, Json)
            : await _client.PutAsJsonAsync($"/api/authorizations/{authorization.Id}/status", body, Json);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(status, authorization.Status);
        Assert.Empty(authorization.StatusHistory);
        Assert.Null(authorization.EDI278ResponseControlNumber);
    }

    [Fact]
    public async Task CancellationIsAuditedAndRetryDoesNotDuplicateHistory()
    {
        var authorization = Seed(AuthorizationStatus.Pended);
        var path = $"/api/authorizations/{authorization.Id}";
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(path)).StatusCode);
        Assert.Equal(AuthorizationStatus.Cancelled, authorization.Status);
        Assert.Equal(AuthorizationStatus.Cancelled, Assert.Single(authorization.StatusHistory).Status);
        Assert.False(string.IsNullOrWhiteSpace(authorization.StatusHistory[0].ChangedBy));
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(path)).StatusCode);
        Assert.Single(authorization.StatusHistory);
    }

    [Fact]
    public async Task ExpiredApprovalKeepsItsClinicalCodeButCannotValidateAClaim()
    {
        var authorization = Seed(AuthorizationStatus.Expired);
        authorization.ReviewDecision = "A1";
        authorization.ApprovedServiceDateFrom = DateTime.UtcNow.AddDays(-1);
        authorization.ApprovedServiceDateTo = DateTime.UtcNow.AddDays(1);
        authorization.RequestedServices[0].ServiceStatus = "A1";
        _factory.AuthorizationRepository.GetByAuthorizationNumberAsync(authorization.AuthorizationNumber).Returns(authorization);
        var result = await _client.GetFromJsonAsync<AuthorizationValidationResponse>(
            $"/api/authorizations/{authorization.AuthorizationNumber}/validate?procedureCode=72148", Json);
        Assert.False(result!.IsValid);
        Assert.Equal(AuthorizationStatus.Expired, result.Status);
    }

    [Fact]
    public async Task ApprovalCannotBeMarkedExpiredBeforeItsInclusiveEndDate()
    {
        var authorization = Seed(AuthorizationStatus.Approved);
        authorization.ApprovedServiceDateTo = DateTime.UtcNow.Date;
        var response = await _client.PutAsJsonAsync($"/api/authorizations/{authorization.Id}/status", new { status = "Expired" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(AuthorizationStatus.Approved, authorization.Status);
    }
}
