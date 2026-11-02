using CloudHealthOffice.Testing.Mongo;
using EnrollmentImportService.Models;
using EnrollmentImportService.Repositories;
using MongoDB.Driver;

namespace EnrollmentImportService.Tests.Repositories;

/// <summary>Rate cell inputs on enrollment events against a real mongod.</summary>
[Collection(MongoRunnerFixture.CollectionName)]
public sealed class EnrollmentEventRateCellMongoTests(MongoRunnerFixture mongo) : IAsyncLifetime
{
    private IMongoDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = mongo.CreateDatabase("enrollment_rate_cells");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => mongo.DropDatabaseAsync(_db);

    private static EnrollmentEvent Event(string member, int version, string? riskGroup, string tenant = "t1") => new()
    {
        TenantId = tenant, MemberId = member, EventId = $"{member}-{version}", Version = version,
        EventType = EnrollmentEventType.Enrolled, MaintenanceType = "021",
        EventDate = new DateTime(2026, version, 1, 0, 0, 0, DateTimeKind.Utc),
        RateCellInputs = riskGroup is null ? null : new EnrollmentRateCellInputs
        {
            ReportingCategories = { new RateCellCategory { Name = "RISK GROUP", Qualifier = "ZZ", Value = riskGroup, Date = $"2026{version:00}01" } },
            CoverageLines = { new RateCellCoverageLine { InsuranceLineCode = "HLT", PlanCoverageDescription = "STAR" } },
        },
    };

    [Fact]
    public async Task Pages_by_member_oldest_first_skipping_events_without_inputs_and_other_tenants()
    {
        var repo = new EnrollmentEventRepository(_db);
        foreach (var e in new[]
                 {
                     Event("M2", 1, "ADULT"), Event("M1", 2, "CHILD"), Event("M1", 1, "INFANT"),
                     Event("M1", 3, null), Event("M3", 1, "SSI"), Event("M0", 1, "X", tenant: "t2"),
                 })
            await repo.AppendAsync(e);

        var first = await repo.ListRateCellInputsAsync("t1", null, 2);
        var second = await repo.ListRateCellInputsAsync("t1", first[^1].MemberId, 2);

        first.Select(m => m.MemberId).Should().Equal("M1", "M2");
        first[0].Snapshots.Select(s => s.Inputs.ReportingCategories[0].Value).Should().Equal("INFANT", "CHILD");
        first[0].Snapshots[0].EventDate.Should().Be(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        second.Select(m => m.MemberId).Should().Equal("M3");
        second[0].Snapshots.Single().Inputs.CoverageLines.Single().PlanCoverageDescription.Should().Be("STAR");
    }
}
