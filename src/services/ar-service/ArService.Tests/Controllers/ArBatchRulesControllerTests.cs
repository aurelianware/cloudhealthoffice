using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using ArService.Controllers;
using ArService.Models;
using ArService.Repositories;

namespace ArService.Tests.Controllers;

public class ArBatchRulesControllerTests
{
    private readonly Mock<IArBatchRuleRepository> _batchRuleRepo;
    private readonly ArBatchRulesController _controller;

    public ArBatchRulesControllerTests()
    {
        _batchRuleRepo = new Mock<IArBatchRuleRepository>();
        var logger = new Mock<ILogger<ArBatchRulesController>>();
        _controller = new ArBatchRulesController(_batchRuleRepo.Object, logger.Object);
    }

    private static ArBatchRule CreateRule(
        string id = "rule-1",
        string ruleCode = "PREM-BILL-COM",
        string ruleName = "Premium Billing - Commercial",
        BatchRuleTrigger trigger = BatchRuleTrigger.PremiumBillingRunComplete,
        BatchRuleStatus status = BatchRuleStatus.Active,
        BatchRuleSplitBehavior splitBehavior = BatchRuleSplitBehavior.NoSplit,
        decimal? autoApproveThreshold = null,
        string debitAccountId = "acct-ar",
        string creditAccountId = "acct-rev",
        string tenantId = "tenant-1",
        string createdBy = "admin") => new()
    {
        Id = id,
        TenantId = tenantId,
        RuleCode = ruleCode,
        RuleName = ruleName,
        Trigger = trigger,
        DebitAccountId = debitAccountId,
        CreditAccountId = creditAccountId,
        SplitBehavior = splitBehavior,
        AutoApproveThreshold = autoApproveThreshold,
        ExecutionOrder = 10,
        Status = status,
        EffectiveDate = new DateTime(2026, 1, 1),
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = createdBy
    };

    #region SearchBatchRules

    [Fact]
    public async Task SearchBatchRules_NoFilters_ReturnsAll()
    {
        var rules = new List<ArBatchRule> { CreateRule(), CreateRule("rule-2", "CAP-RUN-MED") };
        _batchRuleRepo.Setup(r => r.SearchAsync(null, null, 1, 50)).ReturnsAsync(rules);

        var result = await _controller.SearchBatchRules();

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as IEnumerable<ArBatchRule>)!.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchBatchRules_WithFilters_PassesThrough()
    {
        _batchRuleRepo.Setup(r => r.SearchAsync(BatchRuleTrigger.CapitationRunComplete,
            BatchRuleStatus.Active, 1, 50))
            .ReturnsAsync(new List<ArBatchRule>());

        await _controller.SearchBatchRules(
            trigger: BatchRuleTrigger.CapitationRunComplete,
            status: BatchRuleStatus.Active);

        _batchRuleRepo.Verify(r => r.SearchAsync(BatchRuleTrigger.CapitationRunComplete,
            BatchRuleStatus.Active, 1, 50), Times.Once);
    }

    #endregion

    #region GetBatchRuleById

    [Fact]
    public async Task GetBatchRuleById_Found_ReturnsOk()
    {
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(CreateRule());

        var result = await _controller.GetBatchRuleById("rule-1");

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        (ok!.Value as ArBatchRule)!.RuleCode.Should().Be("PREM-BILL-COM");
    }

    [Fact]
    public async Task GetBatchRuleById_NotFound_Returns404()
    {
        _batchRuleRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArBatchRule?)null);

        var result = await _controller.GetBatchRuleById("missing");

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region CreateBatchRule

    [Fact]
    public async Task CreateBatchRule_Returns201WithCreatedObject()
    {
        var rule = CreateRule();
        _batchRuleRepo.Setup(r => r.CreateAsync(It.IsAny<ArBatchRule>()))
            .ReturnsAsync((ArBatchRule r) => r);

        var result = await _controller.CreateBatchRule(rule);

        var created = result.Result as CreatedAtActionResult;
        created.Should().NotBeNull();
        created!.StatusCode.Should().Be(201);
        (created.Value as ArBatchRule)!.RuleCode.Should().Be("PREM-BILL-COM");
    }

    [Fact]
    public async Task CreateBatchRule_CorrectRouteValues()
    {
        var rule = CreateRule();
        _batchRuleRepo.Setup(r => r.CreateAsync(It.IsAny<ArBatchRule>()))
            .ReturnsAsync((ArBatchRule r) => r);

        var result = await _controller.CreateBatchRule(rule);

        var created = result.Result as CreatedAtActionResult;
        created!.ActionName.Should().Be(nameof(ArBatchRulesController.GetBatchRuleById));
        created.RouteValues!["id"].Should().Be(rule.Id);
    }

    #endregion

    #region UpdateBatchRule

    [Fact]
    public async Task UpdateBatchRule_Found_PreservesTenantIdCreatedAtCreatedBy()
    {
        var existing = CreateRule(tenantId: "original-tenant", createdBy: "original-creator");
        existing.CreatedAt = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(existing);
        _batchRuleRepo.Setup(r => r.UpdateAsync(It.IsAny<ArBatchRule>()))
            .ReturnsAsync((ArBatchRule r) => r);

        var incoming = CreateRule(tenantId: "attacker-tenant", createdBy: "attacker");
        incoming.RuleName = "Updated Rule Name";

        var result = await _controller.UpdateBatchRule("rule-1", incoming);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var saved = ok!.Value as ArBatchRule;
        saved!.Id.Should().Be("rule-1");
        saved.TenantId.Should().Be("original-tenant");
        saved.CreatedAt.Should().Be(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        saved.CreatedBy.Should().Be("original-creator");
        saved.RuleName.Should().Be("Updated Rule Name");
    }

    [Fact]
    public async Task UpdateBatchRule_NotFound_Returns404()
    {
        _batchRuleRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArBatchRule?)null);

        var result = await _controller.UpdateBatchRule("missing", CreateRule());

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    #endregion

    #region TestBatchRule

    [Fact]
    public async Task TestBatchRule_NoSplit_ReturnsBalancedDebitCredit()
    {
        var rule = CreateRule(splitBehavior: BatchRuleSplitBehavior.NoSplit);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 25000.00m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        ok.Should().NotBeNull();
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.SampleAmount.Should().Be(25000.00m);
        testResult.ProjectedDebitAmount.Should().Be(25000.00m);
        testResult.ProjectedCreditAmount.Should().Be(25000.00m);
        testResult.DebitAccountId.Should().Be("acct-ar");
        testResult.CreditAccountId.Should().Be("acct-rev");
    }

    [Fact]
    public async Task TestBatchRule_SplitByAccountConfig_ReturnsFullAmountBothSides()
    {
        var rule = CreateRule(splitBehavior: BatchRuleSplitBehavior.SplitByAccountConfig);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 10000.00m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.ProjectedDebitAmount.Should().Be(10000.00m);
        testResult.ProjectedCreditAmount.Should().Be(10000.00m);
        testResult.SplitBehavior.Should().Be(BatchRuleSplitBehavior.SplitByAccountConfig);
    }

    [Fact]
    public async Task TestBatchRule_BelowAutoApproveThreshold_AutoApprovedIsTrue()
    {
        var rule = CreateRule(autoApproveThreshold: 5000.00m);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 4999.99m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.AutoApproved.Should().BeTrue();
    }

    [Fact]
    public async Task TestBatchRule_AtAutoApproveThreshold_AutoApprovedIsTrue()
    {
        var rule = CreateRule(autoApproveThreshold: 5000.00m);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 5000.00m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.AutoApproved.Should().BeTrue();
    }

    [Fact]
    public async Task TestBatchRule_AboveAutoApproveThreshold_AutoApprovedIsFalse()
    {
        var rule = CreateRule(autoApproveThreshold: 5000.00m);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 5000.01m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.AutoApproved.Should().BeFalse();
    }

    [Fact]
    public async Task TestBatchRule_NoAutoApproveThreshold_AutoApprovedIsFalse()
    {
        var rule = CreateRule(autoApproveThreshold: null);
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 1.00m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.AutoApproved.Should().BeFalse();
    }

    [Fact]
    public async Task TestBatchRule_NotFound_Returns404()
    {
        _batchRuleRepo.Setup(r => r.GetByIdAsync("missing")).ReturnsAsync((ArBatchRule?)null);

        var result = await _controller.TestBatchRule("missing",
            new TestBatchRuleRequest { SampleAmount = 100m });

        result.Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task TestBatchRule_ReturnsCorrectRuleMetadata()
    {
        var rule = CreateRule(
            ruleCode: "FFS-PAY-MED",
            trigger: BatchRuleTrigger.FfsPaymentRunComplete,
            debitAccountId: "acct-expense",
            creditAccountId: "acct-payable");
        _batchRuleRepo.Setup(r => r.GetByIdAsync("rule-1")).ReturnsAsync(rule);

        var request = new TestBatchRuleRequest { SampleAmount = 50000m };
        var result = await _controller.TestBatchRule("rule-1", request);

        var ok = result.Result as OkObjectResult;
        var testResult = ok!.Value as TestBatchRuleResult;
        testResult!.RuleId.Should().Be("rule-1");
        testResult.RuleCode.Should().Be("FFS-PAY-MED");
        testResult.Trigger.Should().Be(BatchRuleTrigger.FfsPaymentRunComplete);
        testResult.DebitAccountId.Should().Be("acct-expense");
        testResult.CreditAccountId.Should().Be("acct-payable");
    }

    #endregion
}
