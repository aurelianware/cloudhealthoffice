using System.Net;
using CloudHealthOffice.Infrastructure.Gateways.Stedi;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers.Stedi;
using CloudHealthOffice.Infrastructure.Tests.Gateways.Stedi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Tests.ReferenceData.Payers;

public class StediPayerDirectorySynchronizerTests
{
    private const string OnePayer = """
        {"items":[{"stediId":"ZZZZZ","displayName":"Synthetic Zulu","primaryPayerId":"70001","aliases":["ZULU"],"transactionSupport":{"eligibilityCheck":"SUPPORTED","claimStatus":"NOT_SUPPORTED","claimSubmission":"NOT_SUPPORTED","claimPayment":"NOT_SUPPORTED","coordinationOfBenefits":"NOT_SUPPORTED","dentalClaimSubmission":"NOT_SUPPORTED","institutionalClaimSubmission":"NOT_SUPPORTED","professionalClaimSubmission":"NOT_SUPPORTED","unsolicitedClaimAttachment":"NOT_SUPPORTED"}}]}
        """;

    private const string UpdatedPayer = """
        {"items":[{"stediId":"ZZZZZ","displayName":"Synthetic Zulu Updated","primaryPayerId":"70001","aliases":["ZULU","ZULU2"],"transactionSupport":{"eligibilityCheck":"SUPPORTED","claimStatus":"SUPPORTED","claimSubmission":"NOT_SUPPORTED","claimPayment":"NOT_SUPPORTED","coordinationOfBenefits":"NOT_SUPPORTED","dentalClaimSubmission":"NOT_SUPPORTED","institutionalClaimSubmission":"NOT_SUPPORTED","professionalClaimSubmission":"NOT_SUPPORTED","unsolicitedClaimAttachment":"NOT_SUPPORTED"}}]}
        """;

    private const string MalformedOnly = """
        {"items":[{"displayName":"Missing Id"}]}
        """;

    [Fact]
    public async Task InitialSync_AddsPayers()
    {
        var (sync, store) = Create(OnePayer);

        var result = await sync.SynchronizeAsync();

        result.Succeeded.Should().BeTrue();
        result.Received.Should().Be(1);
        result.Added.Should().Be(1);
        result.Updated.Should().Be(0);
        (await store.GetByIdAsync("ZZZZZ", CancellationToken.None))!.Name.Should().Be("Synthetic Zulu");
    }

    [Fact]
    public async Task SecondSync_UpdatesExistingPayer()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, OnePayer)
            .EnqueueJson(HttpStatusCode.OK, UpdatedPayer);
        var (sync, store) = Create(handler);

        await sync.SynchronizeAsync();
        var result = await sync.SynchronizeAsync();

        result.Added.Should().Be(0);
        result.Updated.Should().Be(1);
        (await store.GetByIdAsync("ZZZZZ", CancellationToken.None))!.Name.Should().Be("Synthetic Zulu Updated");
    }

    [Fact]
    public async Task StalePayer_IsDisabledNotDeleted()
    {
        var store = PayerTestHarness.CreateStore(seed: false);
        await store.UpsertAsync(new PayerReference
        {
            Id = "OLD",
            Name = "Removed From Source",
            Active = true,
            Provenance = new PayerReferenceProvenance { Source = "stedi", LastSyncedAt = DateTimeOffset.UnixEpoch }
        }, CancellationToken.None);

        var (sync, _) = Create(OnePayer, store);
        var result = await sync.SynchronizeAsync();

        result.Disabled.Should().Be(1);
        var stale = await store.GetByIdAsync("OLD", CancellationToken.None);
        stale.Should().NotBeNull();
        stale!.Active.Should().BeFalse();
        (await store.GetByIdAsync("ZZZZZ", CancellationToken.None))!.Active.Should().BeTrue();
    }

    [Fact]
    public async Task StediApiFailure_IsRecordedWithoutThrowing()
    {
        var handler = new StubHttpMessageHandler().EnqueueStatus(HttpStatusCode.InternalServerError);
        var (sync, _) = Create(handler, maxRetries: 0);

        var result = await sync.SynchronizeAsync();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("temporarily unavailable");
    }

    [Fact]
    public async Task MalformedRecords_AreSkipped()
    {
        var (sync, store) = Create(MalformedOnly);

        var result = await sync.SynchronizeAsync();

        result.Succeeded.Should().BeTrue();
        result.SkippedMalformed.Should().Be(1);
        result.Added.Should().Be(0);
        (await store.CountAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task MissingApiKey_FailsConfigurationWithoutCallingHttp()
    {
        var handler = new StubHttpMessageHandler();
        var gateway = Options.Create(new StediGatewayOptions
        {
            ApiKey = null,
            BaseUrl = "https://healthcare.test",
            Environment = "sandbox"
        });
        var client = new StediPayerDirectoryClient(
            new StubHttpClientFactory(handler, "https://payers.test"),
            gateway,
            Options.Create(new PayerReferenceOptions()),
            NullLogger<StediPayerDirectoryClient>.Instance,
            delay: (_, _) => Task.CompletedTask);
        var store = PayerTestHarness.CreateStore(seed: false);
        var sync = new StediPayerDirectorySynchronizer(
            client, store, gateway, NullLogger<StediPayerDirectorySynchronizer>.Instance);

        var result = await sync.SynchronizeAsync();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not configured");
        handler.CallCount.Should().Be(0);
    }

    private static (StediPayerDirectorySynchronizer Sync, InMemoryPayerReferenceStore Store) Create(
        string json, InMemoryPayerReferenceStore? store = null)
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, json);
        return Create(handler, store: store);
    }

    private static (StediPayerDirectorySynchronizer Sync, InMemoryPayerReferenceStore Store) Create(
        StubHttpMessageHandler handler,
        int maxRetries = 2,
        InMemoryPayerReferenceStore? store = null)
    {
        store ??= PayerTestHarness.CreateStore(seed: false);
        var gateway = Options.Create(new StediGatewayOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://healthcare.test",
            PayerDirectoryPath = "/2024-04-01/payers",
            Environment = "sandbox",
            MaxRetries = maxRetries
        });
        var client = new StediPayerDirectoryClient(
            new StubHttpClientFactory(handler, "https://payers.test"),
            gateway,
            Options.Create(new PayerReferenceOptions()),
            NullLogger<StediPayerDirectoryClient>.Instance,
            delay: (_, _) => Task.CompletedTask);
        var sync = new StediPayerDirectorySynchronizer(
            client, store, gateway, NullLogger<StediPayerDirectorySynchronizer>.Instance);
        return (sync, store);
    }
}
