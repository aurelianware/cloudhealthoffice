using System.Reflection;
using CapitationService.Controllers;
using CapitationService.Models;
using CapitationService.Repositories;
using CapitationService.Tests.Support;
using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace CapitationService.Tests.Controllers;

/// <summary>
/// The receiving end of provider-contracts-service's sync-children, which used
/// to answer 501 and leave every capitation rate config with the contract
/// number, NPI, provider name and line of business it was created with.
/// </summary>
public class InternalRateConfigSyncControllerTests
{
    private readonly Mock<ICapitationContractRepository> _repo = new();

    private InternalRateConfigSyncController Controller(string actor = "provider-contracts-service") =>
        new(_repo.Object, new TestActor(actor, isService: true), NullLogger<InternalRateConfigSyncController>.Instance);

    private static ParentContractFields Fields() => new()
    {
        ContractNumber = "PC-1234567890-2026-A1",
        ProviderNpi = "1234567890",
        ProviderName = "Lone Star Pediatrics",
        LineOfBusiness = LineOfBusiness.Medicaid
    };

    [Fact]
    public void OnlyProviderContractsServiceTokenIsAdmitted()
    {
        var attribute = typeof(InternalRateConfigSyncController).GetCustomAttribute<RequireServiceClientAttribute>();
        attribute.Should().NotBeNull();
        attribute!.Policy.Should().Be(RequireServiceClientAttribute.PolicyPrefix + "provider-contracts-service");
    }

    [Fact]
    public async Task Sync_AppliesParentFieldsToTheContractsRateConfigs()
    {
        ParentContractFields? applied = null;
        _repo.Setup(r => r.SyncParentFieldsAsync("pc-1", It.IsAny<ParentContractFields>(), It.IsAny<DateTime>()))
            .Callback<string, ParentContractFields, DateTime>((_, f, _) => applied = f)
            .ReturnsAsync((string _, ParentContractFields _, DateTime at) => new ParentFieldsSyncResult(3, at));

        var result = await Controller().Sync("pc-1", Fields());

        var ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<ParentFieldsSyncResult>().Which.RateConfigsUpdated.Should().Be(3);
        applied!.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
        applied.ProviderName.Should().Be("Lone Star Pediatrics");
        applied.SyncedBy.Should().Be("provider-contracts-service", "the writer is the token subject, not the body");
    }

    [Theory]
    [InlineData("", "1234567890")]
    [InlineData("PC-1", "")]
    [InlineData("PC-1", "12345678901")]
    public async Task Sync_RefusesMissingOrOversizedFields(string number, string npi)
    {
        var fields = Fields();
        fields.ContractNumber = number;
        fields.ProviderNpi = npi;

        var result = await Controller().Sync("pc-1", fields);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _repo.Verify(r => r.SyncParentFieldsAsync(It.IsAny<string>(), It.IsAny<ParentContractFields>(), It.IsAny<DateTime>()), Times.Never);
    }
}
