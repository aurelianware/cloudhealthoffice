using AuthorizationService.Backends;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using CloudHealthOffice.Infrastructure.Contacts;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AuthorizationService.Tests.Controllers;

public class ContactAuthorizationTests
{
    private readonly Mock<IContactIntakeReader> _contacts = new();
    private readonly Mock<IAuthorizationRepository> _repo = new();
    private readonly Mock<IAuthorizationBackendSelector> _selector = new();
    private readonly Mock<ICurrentActor> _actor = new();
    private readonly ContactIntakeReceipt _contact = new() {
        Id = "synthetic-contact", TenantId = "tenant-1", MemberId = "synthetic-member",
        Classification = "CoverageRequest", Caller = new() { Type = "Member" },
        StartedAt = new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc),
        CaseIntent = new() { State = "CA", LineOfBusiness = "Medicaid", Coverage = new() {
            FirstName = "Synthetic", LastName = "Patient", DateOfBirth = new DateTime(1990, 1, 1),
            ProviderNpi = "1234567890", ServiceTypeCode = "1", ProcedureCode = "99213", Units = 2,
            ServiceDate = new DateTime(2026, 11, 1), Expedited = true
        } }
    };
    public ContactAuthorizationTests()
    {
        _actor.SetupGet(a => a.TenantId).Returns("tenant-1");
        _actor.SetupGet(a => a.UserId).Returns("rep-1");
        _contacts.Setup(c => c.GetAsync(_contact.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_contact);
        _selector.Setup(s => s.Resolve()).Returns(new ChoAuthorizationBackend(_repo.Object));
        _repo.Setup(r => r.CreateAsync(It.IsAny<Authorization>())).ReturnsAsync((Authorization a) => a);
    }
    private ContactAuthorizationController Controller() => new(_contacts.Object, _repo.Object, _selector.Object, _actor.Object);

    [Fact]
    public async Task Native_receipt_and_deadline_use_the_saved_contact_start()
    {
        Authorization? saved = null;
        _repo.Setup(r => r.CreateAsync(It.IsAny<Authorization>())).Callback((Authorization a) => saved = a)
            .ReturnsAsync((Authorization a) => a);
        var result = await Controller().Create(_contact.Id, default);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(_contact.StartedAt, saved!.SubmittedDate);
        Assert.Equal(_contact.StartedAt.AddHours(72), saved.DecisionDueAt);
        Assert.Equal(ContactIntakeIdentity.For("tenant-1", _contact.Id), saved.Id);
        Assert.Equal(2, saved.RequestedServices.Single().RequestedUnits);
        Assert.Null(saved.ReviewDecision);
        Assert.Equal(ContactIntakeIdentity.Hash(_contact.CaseIntent!), saved.SourceContactIntakeHash);
        var receipt = Assert.IsType<ContactCaseResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(saved.SourceContactIntakeHash, receipt.IntakeHash);
    }

    [Fact]
    public async Task Missing_clinical_details_cannot_create_an_authorization()
    {
        _contact.CaseIntent!.Coverage!.ProcedureCode = "";
        Assert.IsType<BadRequestObjectResult>((await Controller().Create(_contact.Id, default)).Result);
        _repo.Verify(r => r.CreateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task Wrong_tenant_contact_is_not_found()
    {
        _contact.TenantId = "other";
        Assert.IsType<NotFoundResult>((await Controller().Create(_contact.Id, default)).Result);
        _repo.Verify(r => r.CreateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task Existing_authorization_is_not_recreated()
    {
        _repo.Setup(r => r.GetByIdAsync(It.IsAny<string>())).ReturnsAsync(new Authorization {
            Id = "saved", SubmittedDate = _contact.StartedAt, SourceContactIntakeHash = "original-intent"
        });
        var receipt = Assert.IsType<ContactCaseResult>(Assert.IsType<OkObjectResult>(
            (await Controller().Create(_contact.Id, default)).Result).Value);
        Assert.Equal("original-intent", receipt.IntakeHash);
        _repo.Verify(r => r.CreateAsync(It.IsAny<Authorization>()), Times.Never);
    }
}
