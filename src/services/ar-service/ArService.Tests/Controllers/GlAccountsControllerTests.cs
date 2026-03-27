using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

public class GlAccountsControllerTests
{
    private readonly Mock<IGlAccountRepository> _accountRepo;
    private readonly GlAccountsController _controller;

    public GlAccountsControllerTests()
    {
        _accountRepo = new Mock<IGlAccountRepository>();
        var logger = new Mock<ILogger<GlAccountsController>>();
        _controller = new GlAccountsController(_accountRepo.Object, logger.Object);
    }

    private static GlAccount CreateAccount(
        string id = "acct-1",
        string accountNumber = "4010",
        string accountName = "Premium Receivable - Commercial",
        GlAccountType accountType = GlAccountType.Asset,
        GlAccountStatus status = GlAccountStatus.Active,
        string tenantId = "tenant-1",
        string createdBy = "admin") => new()
    {
        Id = id,
        TenantId = tenantId,
        AccountNumber = accountNumber,
        AccountName = accountName,
        AccountType = accountType,
        NormalBalance = GlNormalBalance.Debit,
        Status = status,
        EffectiveDate = new DateTime(2026, 1, 1),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = createdBy
    };

    #region SearchAccounts

    [Fact]
    public async Task SearchAccounts_NoFilters_ReturnsAll()
    {
        var accounts = new List<GlAccount> { CreateAccount(), CreateAccount("acct-2", "4020") };
        _accountRepo.Setup(r => r.SearchAsync(null, null, null, 1, 50)).ReturnsAsync(accounts);

        var result = await _controller.SearchAccounts();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<GlAccount>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAccounts_WithFilters_PassesThrough()
    {
        _accountRepo.Setup(r => r.SearchAsync(GlAccountType.Asset, LineOfBusiness.Commercial,
            GlAccountStatus.Active, 2, 25))
            .ReturnsAsync(new List<GlAccount>());

        await _controller.SearchAccounts(
            accountType: GlAccountType.Asset,
            lob: LineOfBusiness.Commercial,
            status: GlAccountStatus.Active,
            page: 2,
            pageSize: 25);

        _accountRepo.Verify(r => r.SearchAsync(GlAccountType.Asset, LineOfBusiness.Commercial,
            GlAccountStatus.Active, 2, 25), Times.Once);
    }

    [Fact]
    public async Task SearchAccounts_ByAccountType_ReturnsFiltered()
    {
        var assetAccounts = new List<GlAccount> { CreateAccount(accountType: GlAccountType.Asset) };
        _accountRepo.Setup(r => r.SearchAsync(GlAccountType.Asset, null, null, 1, 50))
            .ReturnsAsync(assetAccounts);

        var result = await _controller.SearchAccounts(accountType: GlAccountType.Asset);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var returned = (ok!.Value as IEnumerable<GlAccount>)!.ToList();
        returned.Should().HaveCount(1);
        returned[0].AccountType.Should().Be(GlAccountType.Asset);
    }

    #endregion

    #region GetAccountById

    [Fact]
    public async Task GetAccountById_Found_ReturnsOk()
    {
        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(CreateAccount());

        var result = await _controller.GetAccountById("acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as GlAccount)!.AccountNumber.Should().Be("4010");
    }

    [Fact]
    public async Task GetAccountById_NotFound_Returns404()
    {
        _accountRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((GlAccount?)null);

        var result = await _controller.GetAccountById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateAccount

    [Fact]
    public async Task CreateAccount_ReturnsCreatedAtAction()
    {
        var account = CreateAccount();
        _accountRepo.Setup(r => r.CreateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => { a.Id = "new-id"; return a; });

        var result = await _controller.CreateAccount(account);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
        (created.Value as GlAccount)!.AccountNumber.Should().Be("4010");
    }

    [Fact]
    public async Task CreateAccount_Returns201WithCorrectRouteValues()
    {
        var account = CreateAccount();
        _accountRepo.Setup(r => r.CreateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var result = await _controller.CreateAccount(account);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.ActionName.Should().Be(nameof(GlAccountsController.GetAccountById));
        created.RouteValues!["id"].Should().Be(account.Id);
    }

    #endregion

    #region UpdateAccount

    [Fact]
    public async Task UpdateAccount_Found_PreservesTenantIdCreatedAtCreatedBy()
    {
        var existing = CreateAccount(tenantId: "original-tenant", createdBy: "original-creator");
        existing.CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(existing);
        _accountRepo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var incoming = CreateAccount(tenantId: "attacker-tenant", createdBy: "attacker");
        incoming.CreatedAt = DateTime.UtcNow;
        incoming.AccountName = "Updated Name";

        var result = await _controller.UpdateAccount("acct-1", incoming);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var saved = ok!.Value as GlAccount;
        saved!.Id.Should().Be("acct-1");
        saved.TenantId.Should().Be("original-tenant");
        saved.CreatedAt.Should().Be(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        saved.CreatedBy.Should().Be("original-creator");
        saved.AccountName.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateAccount_NotFound_Returns404()
    {
        _accountRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((GlAccount?)null);

        var result = await _controller.UpdateAccount("missing", CreateAccount());

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region DeactivateAccount

    [Fact]
    public async Task DeactivateAccount_ActiveAccount_SetsStatusInactive()
    {
        var account = CreateAccount(status: GlAccountStatus.Active);
        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(account);
        _accountRepo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var result = await _controller.DeactivateAccount("acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as GlAccount)!.Status.Should().Be(GlAccountStatus.Inactive);
    }

    [Fact]
    public async Task DeactivateAccount_NotFound_Returns404()
    {
        _accountRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((GlAccount?)null);

        var result = await _controller.DeactivateAccount("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task DeactivateAccount_AlreadyInactive_StillSetsInactive()
    {
        // Note: controller does NOT reject already-inactive accounts — it just sets Inactive again.
        // This test documents the actual behavior.
        var account = CreateAccount(status: GlAccountStatus.Inactive);
        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(account);
        _accountRepo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var result = await _controller.DeactivateAccount("acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as GlAccount)!.Status.Should().Be(GlAccountStatus.Inactive);
        _accountRepo.Verify(r => r.UpdateAsync(It.Is<GlAccount>(a => a.Status == GlAccountStatus.Inactive)), Times.Once);
    }

    #endregion

    #region ActivateAccount

    [Fact]
    public async Task ActivateAccount_InactiveAccount_SetsStatusActive()
    {
        var account = CreateAccount(status: GlAccountStatus.Inactive);
        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(account);
        _accountRepo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var result = await _controller.ActivateAccount("acct-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as GlAccount)!.Status.Should().Be(GlAccountStatus.Active);
    }

    [Fact]
    public async Task ActivateAccount_NotFound_Returns404()
    {
        _accountRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((GlAccount?)null);

        var result = await _controller.ActivateAccount("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ActivateAccount_SuspendedAccount_SetsStatusActive()
    {
        var account = CreateAccount(status: GlAccountStatus.Suspended);
        _accountRepo.Setup(r => r.GetByIdAsync("acct-1")).ReturnsAsync(account);
        _accountRepo.Setup(r => r.UpdateAsync(It.IsAny<GlAccount>()))
            .ReturnsAsync((GlAccount a) => a);

        var result = await _controller.ActivateAccount("acct-1");

        var ok = result.Result as OkObjectResult;
        (ok!.Value as GlAccount)!.Status.Should().Be(GlAccountStatus.Active);
    }

    #endregion
}
