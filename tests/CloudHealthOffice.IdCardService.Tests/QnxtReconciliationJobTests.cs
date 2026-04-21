using IdCardService.Models;
using IdCardService.Repositories;
using IdCardService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.IdCardService.Tests;

public class QnxtReconciliationJobTests
{
    [Fact]
    public async Task ReplaysRecentNonRevokedRecords()
    {
        var records = new InMemoryIdCardRecordRepository();
        var queue = new InMemoryQnxtMirrorQueue();

        await records.UpsertAsync(new IdCardRecord
        {
            TenantId = TestFixtures.TenantId,
            MemberId = TestFixtures.MemberId,
            OrderId = "o1",
            CardId = "card-live",
            Platform = "qnxt",
            IssuedAt = DateTime.UtcNow.AddHours(-2)
        });
        await records.UpsertAsync(new IdCardRecord
        {
            TenantId = TestFixtures.TenantId,
            MemberId = TestFixtures.MemberId,
            OrderId = "o2",
            CardId = "card-revoked",
            Platform = "qnxt",
            IssuedAt = DateTime.UtcNow.AddHours(-3),
            RevokedAt = DateTime.UtcNow.AddHours(-1),
            RevocationReason = IdCardRevocationReason.Replaced
        });
        // CHO-issued record — reconciliation must skip it.
        await records.UpsertAsync(new IdCardRecord
        {
            TenantId = TestFixtures.TenantId,
            MemberId = TestFixtures.MemberId,
            OrderId = "o3",
            CardId = "card-cho",
            Platform = "cho",
            IssuedAt = DateTime.UtcNow.AddHours(-2)
        });

        var services = new ServiceCollection()
            .AddSingleton<IIdCardRecordRepository>(records)
            .AddSingleton<IQnxtMirrorQueue>(queue)
            .BuildServiceProvider();

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["IdCard:Reconciliation:IntervalHours"] = "24" })
            .Build();

        var job = new QnxtMirrorReconciliationJob(services, cfg, NullLogger<QnxtMirrorReconciliationJob>.Instance);
        await job.RunOnceAsync(CancellationToken.None);

        var enqueued = queue.PeekEnqueued();
        Assert.Single(enqueued);
        Assert.Equal("card-live", enqueued.First().CardId);
    }
}
