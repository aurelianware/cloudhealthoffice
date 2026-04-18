using EnrollmentImportService.Models;
using EnrollmentImportService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnrollmentImportService.Tests.Services;

public class EnrollmentEventPublisherTests
{
    private static EnrollmentEventPublisher Build(out InMemoryEnrollmentEventRepository repo)
    {
        repo = new InMemoryEnrollmentEventRepository();
        return new EnrollmentEventPublisher(repo, NullLogger<EnrollmentEventPublisher>.Instance);
    }

    private static EnrollmentEvent Sample(string eventId = "e1") => new()
    {
        TenantId = "t1",
        MemberId = "M-001",
        EventId = eventId,
        EventType = EnrollmentEventType.Enrolled,
        OccurredAt = DateTime.UtcNow,
        Source = "edi834"
    };

    [Fact]
    public async Task PublishAsync_AssignsVersion_AndStoresEvent()
    {
        var publisher = Build(out var repo);
        var stored = await publisher.PublishAsync(Sample());

        stored.Version.Should().Be(1);
        stored.PartitionKey.Should().Be("t1:M-001");
        repo.AllEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_SameEventId_IsIdempotent()
    {
        var publisher = Build(out var repo);
        var first = await publisher.PublishAsync(Sample("dup"));
        var second = await publisher.PublishAsync(Sample("dup"));

        second.Version.Should().Be(first.Version);
        repo.AllEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_RetriesOnVersionConflict_AndSucceeds()
    {
        var publisher = Build(out var repo);
        repo.VersionConflictsToInject = 1;

        var stored = await publisher.PublishAsync(Sample("retry-e1"));
        stored.Version.Should().Be(1);
        repo.AllEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task PublishAsync_ExhaustsRetries_ThrowsConcurrencyException()
    {
        var publisher = Build(out var repo);
        repo.VersionConflictsToInject = 100;

        Func<Task> act = async () => await publisher.PublishAsync(Sample("never"));
        await act.Should().ThrowAsync<EnrollmentConcurrencyException>();
    }

    [Fact]
    public async Task PublishAsync_MissingEventId_Throws()
    {
        var publisher = Build(out _);
        var bad = Sample();
        bad.EventId = string.Empty;
        Func<Task> act = async () => await publisher.PublishAsync(bad);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
