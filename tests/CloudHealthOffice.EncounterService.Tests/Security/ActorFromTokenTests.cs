using CloudHealthOffice.EncounterService.Tests.Support;
using EncounterService.Controllers;
using EncounterService.Models;
using EncounterService.Repositories;
using EncounterService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.EncounterService.Tests.Security;

/// <summary>
/// Every encounter write records the token subject (ICurrentActor.UserId).
/// createdBy / lastUpdatedBy sent in a request body are ignored.
/// </summary>
public class ActorFromTokenTests
{
    private const string Tenant = "t1";
    private const string UserId = "encounter-user-7";

    private readonly Mock<IEncounterRepository> _repo = new();
    private readonly List<Encounter> _created = new();
    private readonly List<Encounter> _updated = new();

    private EncountersController Build()
    {
        _repo.Setup(r => r.CreateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) => { _created.Add(e); return e; });
        _repo.Setup(r => r.UpdateAsync(It.IsAny<Encounter>()))
            .ReturnsAsync((Encounter e) => { _updated.Add(e); return e; });
        var ctl = new EncountersController(
            _repo.Object,
            Mock.Of<IEncounter837Service>(),
            new ConfigurationBuilder().Build(),
            new TestActor(UserId, Tenant),
            NullLogger<EncountersController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return ctl;
    }

    private static Encounter Existing(EncounterStatus status = EncounterStatus.Rejected) => new()
    {
        Id = "enc-1", TenantId = Tenant, EncounterControlNumber = "ENC-1", ClaimId = "CLM-1",
        MemberId = "M1", BillingProviderNPI = "1234567890", PayerId = "PAYER1",
        LineOfBusiness = LineOfBusiness.Medicaid, Status = status,
        ServiceDateFrom = DateTime.UtcNow.Date, ServiceDateTo = DateTime.UtcNow.Date,
        CreatedBy = "original-creator", LastUpdatedBy = "earlier-updater",
        ServiceLines = { new EncounterServiceLine { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 100m } }
    };

    private static Encounter BodyClaimingAnotherActor() => new()
    {
        EncounterControlNumber = "ENC-BODY", ClaimId = "CLM-2", MemberId = "M1",
        BillingProviderNPI = "1234567890", PayerId = "PAYER1",
        CreatedBy = "someone-else", LastUpdatedBy = "someone-else",
        ServiceLines = { new EncounterServiceLine { LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 50m } }
    };

    [Fact]
    public async Task SubmitEncounter_RecordsCreatorFromToken_IgnoringBodyActor()
    {
        var ctl = Build();

        await ctl.SubmitEncounter(BodyClaimingAnotherActor());

        _created.Should().ContainSingle();
        _created[0].CreatedBy.Should().Be(UserId);
        _created[0].LastUpdatedBy.Should().Be(UserId);
    }

    [Fact]
    public async Task UpdateEncounterStatus_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync("enc-1")).ReturnsAsync(Existing(EncounterStatus.Submitted));

        await ctl.UpdateEncounterStatus("enc-1", new EncounterStatusUpdate { Status = EncounterStatus.Accepted });

        _updated.Should().ContainSingle();
        _updated[0].LastUpdatedBy.Should().Be(UserId);
        _updated[0].CreatedBy.Should().Be("original-creator");
    }

    [Fact]
    public async Task DispatchBatch_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetPendingByPayerAsync("PAYER1", null, null, It.IsAny<int>()))
            .ReturnsAsync(new[] { Existing(EncounterStatus.Pending) });

        await ctl.DispatchBatch(new BatchDispatchRequest { PayerId = "PAYER1" });

        _updated.Should().ContainSingle();
        _updated[0].Status.Should().Be(EncounterStatus.Queued);
        _updated[0].LastUpdatedBy.Should().Be(UserId);
    }

    [Fact]
    public async Task MarkBatchSubmitted_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.SearchAsync(null, null, "BATCH-1", null, null, EncounterStatus.Queued, null, null, 1, 5000))
            .ReturnsAsync(new[] { Existing(EncounterStatus.Queued) });

        await ctl.MarkBatchSubmitted("BATCH-1");

        _updated.Should().ContainSingle();
        _updated[0].Status.Should().Be(EncounterStatus.Submitted);
        _updated[0].LastUpdatedBy.Should().Be(UserId);
    }

    [Fact]
    public async Task SubmitCorrection_RecordsActorFromToken_OnOriginalVoidAndReplacement()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync("enc-1")).ReturnsAsync(Existing(EncounterStatus.Rejected));

        await ctl.SubmitCorrection("enc-1", new CorrectionRequest
        {
            CorrectedEncounter = BodyClaimingAnotherActor(),
            CorrectionReason = "wrong NPI"
        });

        _updated.Should().ContainSingle();
        _updated[0].Status.Should().Be(EncounterStatus.CorrectionSubmitted);
        _updated[0].LastUpdatedBy.Should().Be(UserId);

        _created.Should().HaveCount(2);
        _created.Should().OnlyContain(e => e.CreatedBy == UserId && e.LastUpdatedBy == UserId);
    }

    [Fact]
    public async Task ResubmitEncounter_RecordsCreatorFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync("enc-1")).ReturnsAsync(Existing(EncounterStatus.Rejected));

        await ctl.ResubmitEncounter("enc-1");

        _created.Should().ContainSingle();
        _created[0].SubmissionType.Should().Be(SubmissionType.Resubmission);
        _created[0].CreatedBy.Should().Be(UserId);
        _created[0].LastUpdatedBy.Should().Be(UserId);
    }

    [Fact]
    public async Task VoidEncounter_RecordsUpdaterFromToken()
    {
        var ctl = Build();
        _repo.Setup(r => r.GetByIdAsync("enc-1")).ReturnsAsync(Existing(EncounterStatus.Accepted));

        await ctl.VoidEncounter("enc-1");

        _updated.Should().ContainSingle();
        _updated[0].Status.Should().Be(EncounterStatus.Voided);
        _updated[0].LastUpdatedBy.Should().Be(UserId);
    }
}
