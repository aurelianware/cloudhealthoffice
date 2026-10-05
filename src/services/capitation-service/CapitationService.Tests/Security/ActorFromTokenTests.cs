using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Services;
using CapitationService.Tests.Services;
using CapitationService.Tests.Support;

namespace CapitationService.Tests.Security;

/// <summary>
/// Every write records the token subject as its actor. Body-supplied actor
/// fields (CreatedBy, InitiatedBy) and server-owned state are ignored.
/// </summary>
public class ActorFromTokenTests
{
    private const string BodyActor = "someone-else";

    [Fact]
    public async Task CreateRun_RecordsTokenActor_NotBodyCreatedBy()
    {
        var runService = new Mock<ICapitationRunService>();
        runService.Setup(s => s.CreateRunAsync(It.IsAny<CreateCapitationRunRequest>(), It.IsAny<string?>()))
            .ReturnsAsync(new CapitationRun { Id = "run-1" });
        var controller = new CapitationRunsController(
            runService.Object, Mock.Of<ICapitationStatementRepository>(), new TestActor(),
            Mock.Of<ILogger<CapitationRunsController>>());

        await controller.CreateRun(new CreateCapitationRunRequest
        {
            CapitationPeriod = new DateTime(2026, 3, 1),
            CreatedBy = BodyActor
        });

        runService.Verify(s => s.CreateRunAsync(It.IsAny<CreateCapitationRunRequest>(), TestActor.DefaultUserId), Times.Once);
        runService.Verify(s => s.CreateRunAsync(It.IsAny<CreateCapitationRunRequest>(), BodyActor), Times.Never);
    }

    [Fact]
    public async Task CreateContract_RecordsTokenActor_AndServerTimestamps()
    {
        var repo = new Mock<ICapitationContractRepository>();
        CapitationContract? saved = null;
        repo.Setup(r => r.CreateAsync(It.IsAny<CapitationContract>()))
            .Callback<CapitationContract>(c => saved = c)
            .ReturnsAsync((CapitationContract c) => c);
        var controller = new CapitationContractsController(repo.Object, new TestActor(),
            Mock.Of<ILogger<CapitationContractsController>>());

        await controller.CreateContract(new CapitationContract
        {
            ContractId = "pc-1",
            CreatedBy = BodyActor,
            LastUpdatedBy = BodyActor,
            CreatedAt = new DateTime(2001, 1, 1)
        });

        saved.Should().NotBeNull();
        saved!.CreatedBy.Should().Be(TestActor.DefaultUserId);
        saved.LastUpdatedBy.Should().Be(TestActor.DefaultUserId);
        saved.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task UpdateContract_CannotChangeStatus_AndRecordsTokenActor()
    {
        var existing = new CapitationContract
        {
            Id = "c-1", TenantId = "tenant-1", ContractId = "pc-1",
            Status = CapitationRateConfigStatus.Draft, CreatedBy = "creator"
        };
        var repo = new Mock<ICapitationContractRepository>();
        repo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(existing);
        CapitationContract? saved = null;
        repo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>()))
            .Callback<CapitationContract>(c => saved = c)
            .ReturnsAsync((CapitationContract c) => c);
        var controller = new CapitationContractsController(repo.Object, new TestActor(),
            Mock.Of<ILogger<CapitationContractsController>>());

        // A full-model PUT used to activate a Draft contract without the activate step.
        await controller.UpdateContract("c-1", new CapitationContract
        {
            ContractId = "pc-1",
            Status = CapitationRateConfigStatus.Active,
            LastUpdatedBy = BodyActor
        });

        saved!.Status.Should().Be(CapitationRateConfigStatus.Draft);
        saved.LastUpdatedBy.Should().Be(TestActor.DefaultUserId);
        saved.CreatedBy.Should().Be("creator");
    }

    [Fact]
    public async Task ActivateAndTerminateContract_RecordTokenActor()
    {
        var contract = new CapitationContract { Id = "c-1", Status = CapitationRateConfigStatus.Draft };
        var repo = new Mock<ICapitationContractRepository>();
        repo.Setup(r => r.GetByIdAsync("c-1")).ReturnsAsync(contract);
        repo.Setup(r => r.UpdateAsync(It.IsAny<CapitationContract>())).ReturnsAsync((CapitationContract c) => c);
        var controller = new CapitationContractsController(repo.Object, new TestActor("activator"),
            Mock.Of<ILogger<CapitationContractsController>>());

        await controller.ActivateContract("c-1");
        contract.LastUpdatedBy.Should().Be("activator");

        contract.LastUpdatedBy = null;
        await controller.TerminateContract("c-1", new TerminateContractRequest { Reason = "end" });
        contract.LastUpdatedBy.Should().Be("activator");
    }

    [Fact]
    public async Task InitiateDisbursement_RecordsTokenActor_NotBodyInitiatedBy()
    {
        var service = new Mock<ICapitationDisbursementService>();
        InitiateDisbursementRequest? seen = null;
        service.Setup(s => s.InitiateDisbursementAsync(It.IsAny<InitiateDisbursementRequest>()))
            .Callback<InitiateDisbursementRequest>(r => seen = r)
            .ReturnsAsync(new CapitationDisbursement { Id = "d-1" });
        var controller = new CapitationDisbursementsController(service.Object, new TestActor(),
            Mock.Of<ILogger<CapitationDisbursementsController>>());

        await controller.InitiateDisbursement(new InitiateDisbursementRequest { StatementId = "s-1", InitiatedBy = BodyActor });

        seen!.InitiatedBy.Should().Be(TestActor.DefaultUserId);
    }

    [Fact]
    public async Task InitiateBatchDisbursement_RecordsTokenActor_NotBodyInitiatedBy()
    {
        var service = new Mock<ICapitationDisbursementService>();
        InitiateBatchDisbursementRequest? seen = null;
        service.Setup(s => s.InitiateBatchDisbursementAsync(It.IsAny<InitiateBatchDisbursementRequest>()))
            .Callback<InitiateBatchDisbursementRequest>(r => seen = r)
            .ReturnsAsync(new BatchDisbursementResult());
        var controller = new CapitationDisbursementsController(service.Object, new TestActor(),
            Mock.Of<ILogger<CapitationDisbursementsController>>());

        await controller.InitiateBatchDisbursement(new InitiateBatchDisbursementRequest
        {
            StatementIds = ["s-1"],
            InitiatedBy = BodyActor
        });

        seen!.InitiatedBy.Should().Be(TestActor.DefaultUserId);
    }

    [Fact]
    public async Task StatementDecisions_PassTokenActorToService()
    {
        var runService = new Mock<ICapitationRunService>();
        runService.Setup(s => s.ApproveStatementAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new CapitationStatement());
        runService.Setup(s => s.VoidStatementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new CapitationStatement());
        runService.Setup(s => s.HoldStatementAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new CapitationStatement());
        var controller = new CapitationStatementsController(runService.Object,
            Mock.Of<ICapitationStatementRepository>(), Mock.Of<ICapitationContractRepository>(),
            Mock.Of<ICapitationEraService>(), new TestActor("approver"),
            Mock.Of<ILogger<CapitationStatementsController>>());

        await controller.ApproveStatement("s-1");
        await controller.VoidStatement("s-2", new ReasonRequest { Reason = "dup" });
        await controller.HoldStatement("s-3", new ReasonRequest { Reason = "review" });

        runService.Verify(s => s.ApproveStatementAsync("s-1", "approver"), Times.Once);
        runService.Verify(s => s.VoidStatementAsync("s-2", "dup", "approver"), Times.Once);
        runService.Verify(s => s.HoldStatementAsync("s-3", "review", "approver"), Times.Once);
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("void")]
    [InlineData("hold")]
    public async Task RunService_StatementDecisions_StampActor(string decision)
    {
        var statement = new CapitationStatement
        {
            Id = "stmt-1", Status = CapitationStatementStatus.Generated, LastUpdatedBy = "capitation-run"
        };
        var statements = new Mock<ICapitationStatementRepository>();
        statements.Setup(r => r.GetByIdAsync("stmt-1")).ReturnsAsync(statement);
        statements.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>())).ReturnsAsync((CapitationStatement s) => s);
        var service = new CapitationRunService(Mock.Of<ICapitationRunRepository>(),
            Mock.Of<ICapitationContractRepository>(), statements.Object, Mock.Of<IHttpClientFactory>(),
            TestSeparationOfDuties.Create(), Mock.Of<ILogger<CapitationRunService>>());

        var result = decision switch
        {
            "approve" => await service.ApproveStatementAsync("stmt-1", "approver-7"),
            "void" => await service.VoidStatementAsync("stmt-1", "dup", "approver-7"),
            _ => await service.HoldStatementAsync("stmt-1", "review", "approver-7"),
        };

        result.LastUpdatedBy.Should().Be("approver-7");
        if (decision == "approve")
        {
            result.ApprovedBy.Should().Be("approver-7");
            result.ApprovedAt.Should().NotBeNull();
        }
    }
}

/// <summary>Money-path integrity on the disbursement and webhook services.</summary>
public class DisbursementIntegrityTests
{
    private static (CapitationDisbursementService Service, Mock<ICapitationDisbursementRepository> Disbursements)
        CreateService(CapitationStatement statement)
    {
        var statements = new Mock<ICapitationStatementRepository>();
        statements.Setup(r => r.GetByIdAsync(statement.Id)).ReturnsAsync(statement);
        statements.Setup(r => r.UpdateAsync(It.IsAny<CapitationStatement>())).ReturnsAsync((CapitationStatement s) => s);
        var disbursements = new Mock<ICapitationDisbursementRepository>();
        disbursements.Setup(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()))
            .ReturnsAsync((CapitationDisbursement d) => d);
        ReleaseClaims.Uncontended(statements, disbursements);

        var handler = new MockHttpMessageHandler<ProviderBankAccountDto>(_ => new ProviderBankAccountDto
        {
            EftEnabled = true, PreferredDisbursementMethod = "Check"
        });
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("ProviderService"))
            .Returns(new HttpClient(handler) { BaseAddress = new Uri("http://provider-service") });

        var service = new CapitationDisbursementService(disbursements.Object, statements.Object,
            Mock.Of<ICapitationRunRepository>(), Mock.Of<INachaCreditFileService>(),
            Mock.Of<IStripeConnectService>(), factory.Object,
            new ConfigurationBuilder().Build(), TestSeparationOfDuties.Create(), new FactoryBackedProviderBankAccountSource(factory.Object),
            new RecordingNachaDispatcher(), Mock.Of<ILogger<CapitationDisbursementService>>());
        return (service, disbursements);
    }

    private static CapitationStatement Approved(decimal netPayable) => new()
    {
        Id = "stmt-1", ProviderNPI = "1234567890", Status = CapitationStatementStatus.Approved, NetPayable = netPayable
    };

    [Theory]
    [InlineData(5000.01)]
    [InlineData(1000000)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task AmountOverride_OutsideApprovedNetPayable_IsRejected(decimal amount)
    {
        var (service, disbursements) = CreateService(Approved(5000m));

        var act = () => service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1", Amount = amount
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        disbursements.Verify(r => r.CreateAsync(It.IsAny<CapitationDisbursement>()), Times.Never);
    }

    [Fact]
    public async Task AmountOverride_WithinApprovedNetPayable_IsAccepted()
    {
        var (service, _) = CreateService(Approved(5000m));

        var result = await service.InitiateDisbursementAsync(new InitiateDisbursementRequest
        {
            StatementId = "stmt-1", Amount = 1200m
        });

        result.Amount.Should().Be(1200m);
    }

    [Fact]
    public async Task StripeWebhook_WithoutConfiguredSecret_IsRejectedBeforeParsing()
    {
        // The webhook is reachable without a CHO token; with an empty secret an
        // attacker could sign any body with the empty key.
        var client = new Mock<IStripeTransferClient>();
        client.Setup(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(new Event { Type = "payout.paid", Data = new EventData { Object = new Payout { Id = "po_1" } } });
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:SecretKey"] = "sk_test_fake",
                ["Stripe:ConnectWebhookSecret"] = "",
                ["Stripe:WebhookSecret"] = "",
            })
            .Build();
        var service = new StripeConnectService(client.Object, configuration, Mock.Of<ILogger<StripeConnectService>>());

        var act = () => service.ProcessWebhookAsync("{}", "t=1,v1=forged");

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Verify(c => c.ConstructWebhookEvent(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
