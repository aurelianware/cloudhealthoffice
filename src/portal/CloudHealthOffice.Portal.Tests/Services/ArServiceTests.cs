using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

public class ArServiceTests
{
    private readonly Mock<ILogger<ArServiceImpl>> _logger = new();
    private readonly IConfiguration _configuration;

    public ArServiceTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Services:ArService"] = "http://localhost:6001"
            })
            .Build();
    }

    private ArServiceImpl CreateService(HttpClient? httpClient = null)
    {
        httpClient ??= new HttpClient(new FakeHandler(HttpStatusCode.InternalServerError));
        return new ArServiceImpl(httpClient, _configuration, _logger.Object);
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // ════════════════════════════════════════════════════════════════
    // GL Accounts — error paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAccountsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAccountsAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task GetAccountByIdAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAccountByIdAsync("GL-1"));
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task CreateAccountAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.CreateAccountAsync(new GlAccountSummary()));
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task UpdateAccountAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => sut.UpdateAccountAsync("GL-1", new GlAccountSummary()));
        ex.ServiceName.Should().Be("AR Service");
    }

    // ════════════════════════════════════════════════════════════════
    // Balances / Cash Posting / Adjustments / Batch Rules — error paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetBalancesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBalancesAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task GetAgingSummaryAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAgingSummaryAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task GetCashPostingsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetCashPostingsAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task GetAdjustmentsAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetAdjustmentsAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    [Fact]
    public async Task GetBatchRulesAsync_WhenApiFails_ThrowsServiceUnavailableException()
    {
        var sut = CreateService();
        var ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => sut.GetBatchRulesAsync());
        ex.ServiceName.Should().Be("AR Service");
    }

    // ════════════════════════════════════════════════════════════════
    // GL Accounts — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAccountsAsync_WhenApiReturns200_DeserializesAccountList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "GL-1", accountNumber = "1100", accountName = "Premium Receivable",
                  accountType = "Asset", normalBalance = "Debit", status = "Active",
                  effectiveDate = "2025-01-01", isReconciliationAccount = true },
            new { id = "GL-2", accountNumber = "4100", accountName = "Premium Revenue",
                  accountType = "Revenue", normalBalance = "Credit", status = "Active",
                  effectiveDate = "2025-01-01", isReconciliationAccount = false }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAccountsAsync();

        result.Should().HaveCount(2);
        result[0].AccountNumber.Should().Be("1100");
        result[0].IsReconciliationAccount.Should().BeTrue();
        result[1].NormalBalance.Should().Be("Credit");
    }

    [Fact]
    public async Task GetAccountsAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetAccountsAsync(accountType: "Asset", lob: "Commercial", status: "Active");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("accountType=Asset");
        url.Should().Contain("lob=Commercial");
        url.Should().Contain("status=Active");
    }

    [Fact]
    public async Task GetAccountByIdAsync_WhenApiReturns200_DeserializesAccount()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "GL-1", accountNumber = "1100", accountName = "Premium Receivable",
            accountType = "Asset", status = "Active", effectiveDate = "2025-01-01"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAccountByIdAsync("GL-1");

        result.Should().NotBeNull();
        result!.AccountName.Should().Be("Premium Receivable");
    }

    [Fact]
    public async Task GetAccountByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetAccountByIdAsync("GL-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAccountAsync_WhenApiReturns200_ExtractsAccountId()
    {
        var json = JsonSerializer.Serialize(new { id = "GL-NEW-1", accountNumber = "1200" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateAccountAsync(new GlAccountSummary
        {
            AccountNumber = "1200", AccountName = "Claims Payable", AccountType = "Liability"
        });

        result.Should().Be("GL-NEW-1");
    }

    [Fact]
    public async Task CreateAccountAsync_VerifyPostBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { id = "GL-X" }, JsonOpts));
        var sut = CreateService(new HttpClient(handler));

        await sut.CreateAccountAsync(new GlAccountSummary
        {
            AccountNumber = "1200", AccountName = "Claims Payable"
        });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/accounts");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("1200");
        body.Should().Contain("Claims Payable");
    }

    [Fact]
    public async Task UpdateAccountAsync_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateAccountAsync("GL-1", new GlAccountSummary { AccountName = "Updated" });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/accounts/GL-1");
    }

    [Fact]
    public async Task ActivateAccountAsync_SendsPutToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ActivateAccountAsync("GL-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/accounts/GL-1/activate");
    }

    [Fact]
    public async Task DeactivateAccountAsync_SendsPutToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.DeactivateAccountAsync("GL-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/accounts/GL-1/deactivate");
    }

    // ════════════════════════════════════════════════════════════════
    // Balances — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetBalancesAsync_WhenApiReturns200_DeserializesBalanceList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "BAL-1", glAccountId = "GL-1", accountNumber = "1100",
                  openingBalance = 10000m, totalDebits = 5000m, totalCredits = 3000m,
                  closingBalance = 12000m, current = 8000m, days31To60 = 2000m,
                  days61To90 = 1000m, days91To120 = 500m, over120Days = 500m,
                  isReconciled = false }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetBalancesAsync();

        result.Should().ContainSingle();
        result[0].ClosingBalance.Should().Be(12000m);
        result[0].Current.Should().Be(8000m);
        result[0].IsReconciled.Should().BeFalse();
    }

    [Fact]
    public async Task GetBalancesAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBalancesAsync(accountId: "GL-1", isReconciled: false);

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("accountId=GL-1");
        url.Should().Contain("isReconciled=False");
    }

    [Fact]
    public async Task GetBalancesByAccountAsync_UrlContainsAccountId()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBalancesByAccountAsync("GL-1");

        handler.CapturedUrls.Single().Should().Contain("/v1/ar/balances/account/GL-1");
    }

    [Fact]
    public async Task GetAgingSummaryAsync_WhenApiReturns200_DeserializesAgingSummary()
    {
        var json = JsonSerializer.Serialize(new
        {
            current = 50000m, days31To60 = 15000m, days61To90 = 8000m,
            days91To120 = 3000m, over120Days = 2000m, totalOutstanding = 78000m
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAgingSummaryAsync();

        result.Current.Should().Be(50000m);
        result.TotalOutstanding.Should().Be(78000m);
        result.Over120Days.Should().Be(2000m);
    }

    [Fact]
    public async Task ReconcileBalanceAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ReconcileBalanceAsync("BAL-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/balances/BAL-1/reconcile");
    }

    // ════════════════════════════════════════════════════════════════
    // Cash Posting — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetCashPostingsAsync_WhenApiReturns200_DeserializesCashPostingList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "CP-1", postingNumber = "POST-001", receiptDate = "2026-03-15",
                  amount = 25000m, paymentMethod = "Check", checkNumber = "10042",
                  payerType = "Sponsor", payerName = "Acme Corp",
                  appliedAmount = 20000m, unappliedAmount = 5000m, status = "Applied" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetCashPostingsAsync();

        result.Should().ContainSingle();
        result[0].Amount.Should().Be(25000m);
        result[0].UnappliedAmount.Should().Be(5000m);
        result[0].CheckNumber.Should().Be("10042");
    }

    [Fact]
    public async Task GetCashPostingsAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetCashPostingsAsync(payerType: "Sponsor", status: "Pending");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("payerType=Sponsor");
        url.Should().Contain("status=Pending");
    }

    [Fact]
    public async Task GetCashPostingByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetCashPostingByIdAsync("CP-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateCashPostingAsync_WhenApiReturns200_ExtractsCashPostingId()
    {
        var json = JsonSerializer.Serialize(new { id = "CP-NEW-1" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateCashPostingAsync(new CashPostingSummary
        {
            Amount = 10000m, PayerType = "Sponsor", PaymentMethod = "ACH"
        });

        result.Should().Be("CP-NEW-1");
    }

    [Fact]
    public async Task ApplyCashPostingAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ApplyCashPostingAsync("CP-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/cash-postings/CP-1/apply");
    }

    [Fact]
    public async Task VoidCashPostingAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.VoidCashPostingAsync("CP-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/cash-postings/CP-1/void");
    }

    // ════════════════════════════════════════════════════════════════
    // Adjustments — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetAdjustmentsAsync_WhenApiReturns200_DeserializesAdjustmentList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "ADJ-1", adjustmentNumber = "ADJ-001",
                  adjustmentType = "ManualCorrection", glAccountId = "GL-1",
                  amount = 500m, direction = "Debit", reasonCode = "WRITEOFF",
                  status = "Approved" }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAdjustmentsAsync();

        result.Should().ContainSingle();
        result[0].Amount.Should().Be(500m);
        result[0].ReasonCode.Should().Be("WRITEOFF");
    }

    [Fact]
    public async Task GetAdjustmentsAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetAdjustmentsAsync(type: "ManualCorrection", status: "Pending", accountId: "GL-1");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("type=ManualCorrection");
        url.Should().Contain("status=Pending");
        url.Should().Contain("glAccountId=GL-1");
    }

    [Fact]
    public async Task GetAdjustmentByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetAdjustmentByIdAsync("ADJ-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAdjustmentAsync_WhenApiReturns200_ExtractsAdjustmentId()
    {
        var json = JsonSerializer.Serialize(new { id = "ADJ-NEW-1" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateAdjustmentAsync(new ArAdjustmentSummary
        {
            Amount = 250m, Direction = "Credit", ReasonCode = "REFUND"
        });

        result.Should().Be("ADJ-NEW-1");
    }

    [Fact]
    public async Task ApproveAdjustmentAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ApproveAdjustmentAsync("ADJ-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/adjustments/ADJ-1/approve");
    }

    [Fact]
    public async Task RejectAdjustmentAsync_SendsPostWithReasonInBody()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.RejectAdjustmentAsync("ADJ-1", "Insufficient documentation");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/adjustments/ADJ-1/reject");
        var body = await handler.CapturedRequests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("Insufficient documentation");
    }

    [Fact]
    public async Task PostAdjustmentAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.PostAdjustmentAsync("ADJ-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/adjustments/ADJ-1/post");
    }

    [Fact]
    public async Task ReverseAdjustmentAsync_SendsPostToCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.ReverseAdjustmentAsync("ADJ-1");

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/adjustments/ADJ-1/reverse");
    }

    // ════════════════════════════════════════════════════════════════
    // Batch Rules — happy paths
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetBatchRulesAsync_WhenApiReturns200_DeserializesBatchRuleList()
    {
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "BR-1", ruleCode = "PREM-REC", ruleName = "Premium Recognition",
                  trigger = "PremiumInvoice", debitAccountId = "GL-1",
                  creditAccountId = "GL-2", status = "Active",
                  effectiveDate = "2025-01-01", executionOrder = 1 }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetBatchRulesAsync();

        result.Should().ContainSingle();
        result[0].RuleCode.Should().Be("PREM-REC");
        result[0].Trigger.Should().Be("PremiumInvoice");
    }

    [Fact]
    public async Task GetBatchRulesAsync_WithFilters_BuildsCorrectQueryString()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var sut = CreateService(new HttpClient(handler));

        await sut.GetBatchRulesAsync(trigger: "ClaimPayment", status: "Active");

        var url = handler.CapturedUrls.Single();
        url.Should().Contain("trigger=ClaimPayment");
        url.Should().Contain("status=Active");
    }

    [Fact]
    public async Task GetBatchRuleByIdAsync_WhenApiReturnsNull_ReturnsNull()
    {
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, "null")));
        var result = await sut.GetBatchRuleByIdAsync("BR-NONE");
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateBatchRuleAsync_WhenApiReturns200_ExtractsBatchRuleId()
    {
        var json = JsonSerializer.Serialize(new { id = "BR-NEW-1" }, JsonOpts);
        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));

        var result = await sut.CreateBatchRuleAsync(new ArBatchRuleSummary
        {
            RuleCode = "NEW-RULE", RuleName = "New Rule", Trigger = "Manual"
        });

        result.Should().Be("BR-NEW-1");
    }

    [Fact]
    public async Task UpdateBatchRuleAsync_SendsPutWithCorrectUrl()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var sut = CreateService(new HttpClient(handler));

        await sut.UpdateBatchRuleAsync("BR-1", new ArBatchRuleSummary { RuleName = "Updated" });

        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Put);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/batch-rules/BR-1");
    }

    [Fact]
    public async Task TestBatchRuleAsync_WhenApiReturns200_DeserializesTestResult()
    {
        var json = JsonSerializer.Serialize(new
        {
            ruleCode = "PREM-REC", sampleAmount = 1000m,
            debitAccountId = "GL-1", debitAmount = 1000m,
            creditAccountId = "GL-2", creditAmount = 1000m,
            sponsorSplitAmount = 800m, memberSplitAmount = 200m
        }, JsonOpts);

        var handler = new FakeHandler(HttpStatusCode.OK, json);
        var sut = CreateService(new HttpClient(handler));

        var result = await sut.TestBatchRuleAsync("BR-1", 1000m);

        result.DebitAmount.Should().Be(1000m);
        result.CreditAmount.Should().Be(1000m);
        result.SponsorSplitAmount.Should().Be(800m);
        result.MemberSplitAmount.Should().Be(200m);
        handler.CapturedRequests[0].Method.Should().Be(HttpMethod.Post);
        handler.CapturedUrls[0].Should().Contain("/v1/ar/batch-rules/BR-1/test");
    }

    // ── ArAdjustmentSummary – remaining properties ────────────────────────────

    [Fact]
    public async Task GetAdjustmentByIdAsync_WhenApiReturns200_DeserializesAllArAdjustmentSummaryProperties()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "ADJ-FULL", adjustmentNumber = "ADJ-2026-001",
            adjustmentType = "ManualCorrection",
            glAccountId = "GL-100", arBalanceId = "BAL-50",
            period = "2026-01-01T00:00:00Z",
            amount = 1500m, direction = "Credit",
            reasonCode = "OVERSTATE",
            narrative = "Correcting January premium overstatement",
            authorizedBy = "cfo@healthplan.com",
            authorizedAt = "2026-02-01T14:00:00Z",
            sourceType = "Manual",
            sourceReferenceId = "MEMO-2026-0201",
            status = "Posted"
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAdjustmentByIdAsync("ADJ-FULL");

        result.Should().NotBeNull();
        result!.Narrative.Should().Be("Correcting January premium overstatement");
        result.AuthorizedBy.Should().Be("cfo@healthplan.com");
        result.AuthorizedAt.Should().NotBeNull();
        result.SourceType.Should().Be("Manual");
        result.SourceReferenceId.Should().Be("MEMO-2026-0201");
        // Verify that Period was deserialized (exercising get_Period)
        result.Period.Should().NotBe(default);
    }

    // ── PremiumSplitSummary via GlAccountSummary ──────────────────────────────

    [Fact]
    public async Task GetAccountByIdAsync_WhenApiReturnsAccountWithPremiumSplit_DeserializesPremiumSplit()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "GL-SPLIT", accountNumber = "2100", accountName = "Member Premium",
            accountType = "Liability", status = "Active",
            effectiveDate = "2025-01-01T00:00:00Z",
            premiumSplit = new
            {
                sponsorPercentage = 80.0m,
                memberPercentage = 20.0m,
                isPlanSpecific = false
            }
        }, JsonOpts);

        var sut = CreateService(new HttpClient(new FakeHandler(HttpStatusCode.OK, json)));
        var result = await sut.GetAccountByIdAsync("GL-SPLIT");

        result.Should().NotBeNull();
        result!.PremiumSplit.Should().NotBeNull();
        result.PremiumSplit!.SponsorPercentage.Should().Be(80.0m);
        result.PremiumSplit.MemberPercentage.Should().Be(20.0m);
        result.PremiumSplit.IsPlanSpecific.Should().BeFalse();
    }
}
