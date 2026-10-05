using CloudHealthOffice.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PremiumBillingService.Services;
using Stripe;

namespace PremiumBillingService.Tests.Services;

/// <summary>
/// A Stripe ACH debit is created with an idempotency key from its draft, and
/// only a definitive Stripe refusal is reported as "failed": any other error
/// means the debit may exist, so it is thrown (the draft becomes PaymentUnknown).
/// </summary>
public class StripeAchIdempotencyTests
{
    private readonly Mock<IStripePaymentIntentClient> _client = new();
    private readonly StripeAchService _service;

    public StripeAchIdempotencyTests()
    {
        var actor = new Mock<ICurrentActor>();
        actor.SetupGet(a => a.TenantId).Returns("tenant-1");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"] = "sk_test_fake",
        }).Build();
        _service = new StripeAchService(config, actor.Object, Mock.Of<ILogger<StripeAchService>>(), _client.Object);
    }

    private Task<StripeAchDraftResult> Create(string draftId)
        => _service.CreateAchDraftAsync("cus_1", "pm_1", 100m, "INV-1", "GRP001", draftId);

    [Fact]
    public async Task IdempotencyKey_IsDerivedFromTheDraft_AndTheSameOnRetry()
    {
        var keys = new List<string?>();
        PaymentIntentCreateOptions? options = null;
        _client.Setup(c => c.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>()))
            .Callback<PaymentIntentCreateOptions, RequestOptions>((o, r) => { options = o; keys.Add(r?.IdempotencyKey); })
            .ReturnsAsync(new PaymentIntent { Id = "pi_1", Status = "processing", Amount = 10000, Metadata = new Dictionary<string, string>() });

        await Create("draft-42");
        await Create("draft-42");
        await Create("draft-43");

        keys[0].Should().Be(StripeAchService.PaymentIntentIdempotencyKey("draft-42")).And.Contain("draft-42");
        keys[1].Should().Be(keys[0], "a retry of the same draft must reuse the key, so Stripe returns the first PaymentIntent");
        keys[2].Should().NotBe(keys[0]);
        options!.Metadata.Should().ContainKey("draft_id").WhoseValue.Should().Be("draft-43");
    }

    [Fact]
    public async Task DefinitiveRefusal_IsReportedAsFailed()
    {
        _client.Setup(c => c.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>()))
            .ThrowsAsync(new StripeException(System.Net.HttpStatusCode.PaymentRequired, new StripeError { Type = "card_error" }, "declined"));

        var result = await Create("draft-1");

        result.Status.Should().Be("failed");
        result.ErrorMessage.Should().Be("declined");
    }

    public static TheoryData<Exception> OutcomeUnknownErrors() => new()
    {
        new HttpRequestException("connection reset"),
        new TaskCanceledException("timeout"),
        new IOException("broken pipe"),
        new StripeException(System.Net.HttpStatusCode.InternalServerError, new StripeError { Type = "api_error" }, "server error"),
        new StripeException(System.Net.HttpStatusCode.Conflict, new StripeError { Type = "idempotency_error" }, "in flight"),
        new StripeException("no response from Stripe"),
    };

    [Theory]
    [MemberData(nameof(OutcomeUnknownErrors))]
    public async Task ErrorWithoutAStripeRefusal_IsThrown_NotReportedAsFailed(Exception error)
    {
        _client.Setup(c => c.CreateAsync(It.IsAny<PaymentIntentCreateOptions>(), It.IsAny<RequestOptions>()))
            .ThrowsAsync(error);

        await FluentActions.Awaiting(() => Create("draft-1")).Should().ThrowAsync<Exception>();
    }
}
