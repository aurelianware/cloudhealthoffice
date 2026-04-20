using System.Text;
using System.Text.Json;
using EligibilityService.Services;

namespace CloudHealthOffice.EligibilityService.Tests;

public class ServiceBusBatchQueueTests
{
    [Fact]
    public async Task Enqueue_JsonSerializesWithCorrelationId()
    {
        var fake = new FakeSender();
        var queue = new ServiceBusBatchQueue(fake);

        await queue.EnqueueAsync(new BatchQueueMessage("tenant-X", "JOB-1"));

        Assert.Single(fake.Sent);
        Assert.Equal("JOB-1", fake.Sent[0].CorrelationId);

        var body = Encoding.UTF8.GetString(fake.Sent[0].Body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("tenant-X", doc.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal("JOB-1", doc.RootElement.GetProperty("jobId").GetString());
    }

    [Fact]
    public void ReadAllAsync_Unsupported()
    {
        var queue = new ServiceBusBatchQueue(new FakeSender());
        using var cts = new CancellationTokenSource();
        Assert.Throws<NotSupportedException>(() => queue.ReadAllAsync(cts.Token));
    }

    [Fact]
    public void NullSender_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceBusBatchQueue(null!));
    }

    private class FakeSender : IBatchQueueSender
    {
        public List<(byte[] Body, string CorrelationId)> Sent { get; } = new();

        public Task SendAsync(byte[] body, string correlationId, CancellationToken ct)
        {
            Sent.Add((body, correlationId));
            return Task.CompletedTask;
        }
    }
}
