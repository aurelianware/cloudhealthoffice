using System.Net;
using System.Net.Http.Json;
using EncounterSubmissionService.Models;
using EncounterSubmissionService.StateRoutes;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using static CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes.StateRouteTestData;

namespace CloudHealthOffice.EncounterSubmissionService.Tests.StateRoutes;

public class StateEncounterBatchServiceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FixedControlNumbers : IEncounterControlNumberSource
    {
        private long _next = 100;
        public Task<long> NextInterchangeControlNumberAsync(string tenantId, string stateCode, CancellationToken ct = default) =>
            Task.FromResult(_next++);
    }

    private sealed class Harness
    {
        public required StateEncounterBatchService Service { get; init; }
        public required Mock<IMongoCollection<EncounterSubmission>> Submissions { get; init; }
        public required List<StateEncounterFile> Files { get; init; }
        public required Handler Http { get; init; }
        public int SubmissionUpdates { get; set; }
    }

    private static object ClaimJson(string id, string memberId = "M1", decimal paid = 120m) => new
    {
        id,
        tenantId = "tenant-1",
        claimNumber = "CLM-" + id,
        memberId,
        subscriberId = "512345678",
        billingProviderNPI = BillingNpi,
        renderingProviderNPI = RenderingNpi,
        renderingProviderName = "SMITH, JANE",
        placeOfServiceCode = "11",
        claimType = "Professional",
        totalChargeAmount = 250m,
        serviceDateFrom = "2026-10-03T00:00:00Z",
        serviceDateTo = "2026-10-03T00:00:00Z",
        adjudicatedDate = "2026-10-20T15:00:00Z",
        lastUpdatedDate = "2026-10-20T15:00:00Z",
        diagnosisCodes = new[] { new { code = "J45.909", pointerNumber = 1 } },
        adjudicationResult = new { payerPayment = paid },
        claimLines = new object[]
        {
            new
            {
                lineNumber = 1, procedureCode = "99213", units = 1m, chargeAmount = 250m,
                diagnosisPointers = new[] { 1 },
                serviceDateFrom = "2026-10-03T00:00:00Z", serviceDateTo = "2026-10-03T00:00:00Z",
                adjudicationResult = new
                {
                    paidAmount = paid,
                    adjustmentReasons = new[] { new { groupCode = "CO", reasonCode = "45", amount = 250m - paid } }
                }
            }
        }
    };

    private static object MemberJson(bool withAddress = true) => new
    {
        memberId = "M1",
        groupNumber = "STAR",
        firstName = "ALEX",
        lastName = "DOE",
        dateOfBirth = "2015-04-02T00:00:00Z",
        gender = "F",
        address = withAddress ? "100 MAIN ST" : null,
        city = "AUSTIN",
        state = "TX",
        zipCode = "78701"
    };

    private static readonly object ProviderJson = new
    {
        npi = BillingNpi,
        taxId = "741234567",
        organizationName = "AUSTIN PEDIATRICS PA",
        taxonomyCode = "208000000X",
        address = "200 CONGRESS AVE",
        city = "AUSTIN",
        state = "TX",
        zipCode = "787014000"
    };

    private static Func<HttpRequestMessage, HttpResponseMessage> Respond(
        bool memberHasAddress = true, HttpStatusCode claimStatus = HttpStatusCode.OK) => r =>
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.StartsWith("/api/claims/"))
            return claimStatus != HttpStatusCode.OK
                ? new HttpResponseMessage(claimStatus)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ClaimJson(path.Split('/')[^1])) };
        if (path.StartsWith("/api/v1/members/"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(MemberJson(memberHasAddress)) };
        if (path.StartsWith("/api/v1/providers/npi/"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ProviderJson) };
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static Harness Create(Func<HttpRequestMessage, HttpResponseMessage> respond,
        IStateEncounterRouteRegistry? registry = null)
    {
        var submissions = new Mock<IMongoCollection<EncounterSubmission>>();
        var files = new Mock<IMongoCollection<StateEncounterFile>>();
        var stored = new List<StateEncounterFile>();
        files.Setup(f => f.InsertOneAsync(It.IsAny<StateEncounterFile>(), It.IsAny<InsertOneOptions>(), It.IsAny<CancellationToken>()))
            .Callback((StateEncounterFile f, InsertOneOptions _, CancellationToken _) => stored.Add(f))
            .Returns(Task.CompletedTask);

        var database = new Mock<IMongoDatabase>();
        database.Setup(d => d.GetCollection<EncounterSubmission>("encounter_submissions", null)).Returns(submissions.Object);
        database.Setup(d => d.GetCollection<StateEncounterFile>("encounter_state_files", null)).Returns(files.Object);

        var handler = new Handler(respond);
        var factory = new Mock<IHttpClientFactory>();
        foreach (var name in new[] { "ClaimsService", "MemberService", "ProviderService" })
            factory.Setup(f => f.CreateClient(name)).Returns(new HttpClient(handler) { BaseAddress = new Uri("http://svc") });

        var harness = new Harness
        {
            Service = new StateEncounterBatchService(database.Object, factory.Object,
                registry ?? Registry(tx: TexasOptions(), ca: CaliforniaOptions()),
                new FixedControlNumbers(), NullLogger<StateEncounterBatchService>.Instance),
            Submissions = submissions,
            Files = stored,
            Http = handler
        };

        submissions.Setup(c => c.UpdateOneAsync(It.IsAny<FilterDefinition<EncounterSubmission>>(),
                It.IsAny<UpdateDefinition<EncounterSubmission>>(), It.IsAny<UpdateOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => harness.SubmissionUpdates++)
            .ReturnsAsync(new UpdateResult.Acknowledged(1, 1, null));
        return harness;
    }

    private static EncounterSubmission Submission(string id, string state = "TX", string tenant = "tenant-1") => new()
    {
        Id = $"{tenant}:{state}:{id}",
        ClaimId = id,
        TenantId = tenant,
        StateCode = state,
        Status = EncounterSubmissionStatus.Pending
    };

    [Fact]
    public async Task Texas_WritesOne837P_StoresIt_AndMarksEachEncounterBatched()
    {
        var h = Create(Respond());

        var result = await h.Service.BuildBatchAsync(StateEncounterRoute.Texas,
            new[] { Submission("c1"), Submission("c2") }, "tenant-1");

        result.Files.Should().ContainSingle();
        var file = h.Files.Should().ContainSingle().Subject;
        file.StateCode.Should().Be("TX");
        file.Receiver.Should().Be("TMHP");
        file.FormType.Should().Be("837P");
        file.UsageIndicator.Should().Be("T");
        file.FileName.Should().MatchRegex(@"^TMHP_4A_837P_TEST_\d{14}_000000100\.x12$");
        file.TransactionCount.Should().Be(2);
        file.Content.Should().StartWith("ISA*00*").And.Contain("NM1*40*2*TMHP*****46*TMHPTESTRCV");
        file.Content.Should().Contain("NM1*IL*1*DOE*ALEX****MI*512345678");
        file.Content.Should().Contain("CLM*CLM-c1*250***11:B:1*Y*A*Y*Y");
        result.BatchedSubmissionIds.Should().Equal("tenant-1:TX:c1", "tenant-1:TX:c2");
        result.HeldBack.Should().BeEmpty();
        h.SubmissionUpdates.Should().Be(2);
    }

    [Fact]
    public async Task California_UsesTheDhcsIdentities()
    {
        var h = Create(Respond());

        await h.Service.BuildBatchAsync(StateEncounterRoute.California, new[] { Submission("c1", "CA") }, "tenant-1");

        var file = h.Files.Should().ContainSingle().Subject;
        file.FileName.Should().StartWith("DHCS_4A_837P_TEST_");
        file.Content.Should().Contain("NM1*40*2*DHCS*****46*DHCSTESTRCV");
        file.Content.Should().Contain("NM1*PR*2*CA DHCS*****PI*CAMCD");
    }

    [Fact]
    public async Task EncounterFailingValidation_IsHeldBack_AndNotWritten()
    {
        var h = Create(Respond(memberHasAddress: false));

        var result = await h.Service.BuildBatchAsync(StateEncounterRoute.Texas, new[] { Submission("c1") }, "tenant-1");

        result.Files.Should().BeEmpty();
        h.Files.Should().BeEmpty();
        result.HeldBack.Should().ContainKey("tenant-1:TX:c1")
            .WhoseValue.Should().Contain(e => e.Contains("member address"));
        h.SubmissionUpdates.Should().Be(1, "the held-back record is marked ValidationFailed");
    }

    [Fact]
    public async Task ClaimReadFailure_RefusesTheWholeBatch()
    {
        var h = Create(Respond(claimStatus: HttpStatusCode.ServiceUnavailable));

        var act = () => h.Service.BuildBatchAsync(StateEncounterRoute.Texas,
            new[] { Submission("c1"), Submission("c2") }, "tenant-1");

        await act.Should().ThrowAsync<HttpRequestException>();
        h.Files.Should().BeEmpty();
        h.SubmissionUpdates.Should().Be(0);
    }

    [Theory]
    [InlineData("tenant-2", "TX")]
    [InlineData("tenant-1", "CA")]
    [InlineData("tenant-1", "FL")]
    public async Task WrongTenantOrState_IsRefusedBeforeAnyRead(string tenant, string state)
    {
        var h = Create(Respond());

        var act = () => h.Service.BuildBatchAsync(StateEncounterRoute.Texas,
            new[] { Submission("c1", state, tenant) }, "tenant-1");

        await act.Should().ThrowAsync<Exception>();
        h.Http.Paths.Should().BeEmpty();
        h.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task UnconfiguredTenant_IsRefusedBeforeAnyRead()
    {
        var h = Create(Respond(), Registry(tx: TexasOptions(withTenant: false)));

        var act = () => h.Service.BuildBatchAsync(StateEncounterRoute.Texas, new[] { Submission("c1") }, "tenant-1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enrolled*");
        h.Http.Paths.Should().BeEmpty();
    }

    [Fact]
    public async Task Florida_IsNotAnX12Route()
    {
        var h = Create(Respond());

        var act = () => h.Service.BuildBatchAsync(StateEncounterRoute.Florida, new[] { Submission("c1", "FL") }, "tenant-1");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SubmissionRecord_UsesTheStateDeadline_AndADeterministicId()
    {
        var h = Create(Respond());
        EncounterSubmission? inserted = null;
        h.Submissions.Setup(c => c.InsertOneAsync(It.IsAny<EncounterSubmission>(), It.IsAny<InsertOneOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback((EncounterSubmission s, InsertOneOptions _, CancellationToken _) => inserted = s)
            .Returns(Task.CompletedTask);

        var adjudicated = new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
        var record = await h.Service.CreateSubmissionRecordAsync(StateEncounterRoute.Texas, "claim-9", "tenant-1", adjudicated);

        record.Id.Should().Be("tenant-1:TX:claim-9");
        record.StateCode.Should().Be("TX");
        record.Receiver.Should().Be("TMHP");
        record.SubmissionDeadline.Should().Be(new DateTime(2026, 11, 30, 23, 59, 59, DateTimeKind.Utc));
        inserted.Should().BeSameAs(record);
    }
}
