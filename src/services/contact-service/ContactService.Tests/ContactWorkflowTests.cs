using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using ContactService.Controllers;
using ContactService.Models;
using ContactService.Repositories;
using ContactService.Services;
using Microsoft.AspNetCore.Mvc;

namespace ContactService.Tests;

public class ContactWorkflowTests
{
    private readonly Mock<IContactRepository> _store = new();
    private readonly Mock<IContactWorkflowGateway> _gateway = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly Contact _contact = new() {
        Id = "contact-1", TenantId = "tenant-1", Revision = 1, MemberId = "synthetic-member",
        StartedAt = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc),
        Classification = ContactClassification.Grievance, Summary = "Synthetic access complaint",
        Caller = new ContactCaller { Type = CallerType.Member }
    };

    public ContactWorkflowTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns("tenant-1");
        _actor.SetupGet(a => a.UserId).Returns("rep-1");
        _store.Setup(s => s.GetByIdAsync(_contact.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_contact);
        _store.Setup(s => s.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact c, CancellationToken _) => { c.Revision++; return c; });
    }
    private ContactWorkflowController Controller() => new(_store.Object, _gateway.Object, _actor.Object);
    private OpenContactCaseRequest Request() => new() { Revision = _contact.Revision,
        Intent = new ContactCaseIntent { State = "CA", LineOfBusiness = "Medicaid" } };
    private ContactCaseResult Receipt() => new() {
        Id = ContactIntakeIdentity.For("tenant-1", _contact.Id), Kind = "Grievance",
        ReceivedAt = _contact.StartedAt, Status = "Received"
    };

    [Fact]
    public async Task Intent_is_saved_before_remote_delivery_and_retry_reuses_it()
    {
        var savedFirst = false;
        _store.Setup(s => s.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Contact c, CancellationToken _) => { savedFirst = true; c.Revision++; return c; });
        _gateway.Setup(g => g.OpenCaseAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
            .Callback(() => { savedFirst.Should().BeTrue(); _contact.CaseIntent.Should().NotBeNull(); })
            .ThrowsAsync(new HttpRequestException());
        (await Controller().OpenCase(_contact.Id, Request(), default)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(503);
        _contact.CaseIntent!.State.Should().Be("CA");
        _contact.OpenedCase.Should().BeNull();
        _gateway.Setup(g => g.OpenCaseAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>())).ReturnsAsync(Receipt());
        var retry = Request(); retry.Intent.State = "TX";
        (await Controller().OpenCase(_contact.Id, retry, default)).Result.Should().BeOfType<OkObjectResult>();
        _contact.CaseIntent.State.Should().Be("CA");
        _contact.OpenedCase!.ReceivedAt.Should().Be(_contact.StartedAt);
    }

    [Fact]
    public async Task Accepted_case_retry_never_calls_owner_again()
    {
        _contact.OpenedCase = Receipt();
        (await Controller().OpenCase(_contact.Id, Request(), default)).Result.Should().BeOfType<OkObjectResult>();
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_stale_revision_cannot_dispatch_a_case()
    {
        var r = Request(); r.Revision--;
        (await Controller().OpenCase(_contact.Id, r, default)).Result.Should().BeOfType<ConflictObjectResult>();
        _gateway.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(CallerType.Provider)]
    [InlineData(CallerType.Representative)]
    public async Task Non_member_tracks_do_not_get_member_clocks(CallerType caller)
    {
        _contact.Caller.Type = caller;
        (await Controller().OpenCase(_contact.Id, Request(), default)).Result.Should().BeOfType<BadRequestObjectResult>();
        _gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_response_with_a_different_receipt_is_not_accepted()
    {
        var receipt = Receipt(); receipt.ReceivedAt = receipt.ReceivedAt.AddHours(1);
        _gateway.Setup(g => g.OpenCaseAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>())).ReturnsAsync(receipt);
        (await Controller().OpenCase(_contact.Id, Request(), default)).Result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(502);
        _contact.OpenedCase.Should().BeNull();
    }

    [Fact]
    public async Task Follow_up_timeout_leaves_retryable_intent_and_success_links_once()
    {
        var key = Guid.NewGuid().ToString("N");
        var r = new CreateContactFollowUpRequest { Revision = 1, Key = key, Title = "Synthetic callback", DueAt = DateTime.UtcNow.AddDays(2) };
        _gateway.Setup(g => g.CreateFollowUpAsync(_contact.Id, key, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        (await Controller().FollowUp(_contact.Id, r, default)).Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        _contact.FollowUps.Should().ContainSingle();
        r.Revision = _contact.Revision;
        _gateway.Setup(g => g.CreateFollowUpAsync(_contact.Id, key, It.IsAny<CancellationToken>())).ReturnsAsync("work-1");
        (await Controller().FollowUp(_contact.Id, r, default)).Result.Should().BeOfType<OkObjectResult>();
        (await Controller().FollowUp(_contact.Id, r, default)).Result.Should().BeOfType<OkObjectResult>();
        _contact.FollowUpWorkItemIds.Should().Equal("work-1");
    }

    [Fact]
    public async Task A_failed_reservation_never_calls_the_owner()
    {
        _store.Setup(s => s.UpdateAsync(It.IsAny<Contact>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ContactConcurrencyException(_contact.Id, 1));
        (await Controller().OpenCase(_contact.Id, Request(), default)).Result.Should().BeOfType<ConflictObjectResult>();
        _gateway.VerifyNoOtherCalls();
    }
}
