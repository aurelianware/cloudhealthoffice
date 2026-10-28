using System.Runtime.CompilerServices;
using CloudHealthOffice.DocumentStore;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.BulkExport;
using FhirService.Services.ProviderAccess;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudHealthOffice.FhirService.Tests.Services.BulkExport;

public class BulkExportWorkerTests
{
    private const string Tenant = "tenant-a";
    private const string Provider = "prov-1";

    private readonly MutableTime _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryBulkExportJobStore _jobs = new();
    private readonly InMemoryDocumentStore _documents = new();
    private readonly FakeResourceSource _source = new();
    private readonly BulkExportOptions _options = new();
    private readonly HashSet<string> _panel = ["m1", "m2", "m3"];
    private readonly HashSet<string> _denied = [];

    private DocumentStoreBulkExportFileStore Files => new(_documents);

    private DurableBulkExportService Service()
        => new(_jobs, Files, Options.Create(_options), _time);

    private BulkExportWorker Worker()
    {
        var attribution = new Mock<IProviderAttributionSource>();
        attribution.Setup(a => a.ListAttributedAsync(Tenant, Provider, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _panel.ToList());

        var authorization = new Mock<IProviderAccessAuthorizationService>();
        authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ProviderAccessRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderAccessRequest r, CancellationToken _) => new ProviderAccessDecision
            {
                Allowed = r.ProviderId == Provider && _panel.Contains(r.MemberId) && !_denied.Contains(r.MemberId),
                Reason = ProviderAccessDenialReason.None,
                TenantId = r.TenantId,
                MemberId = r.MemberId,
                ProviderId = r.ProviderId,
                EvaluatedAtUtc = DateTime.UtcNow,
            });

        var services = new ServiceCollection()
            .AddSingleton(attribution.Object)
            .AddSingleton(authorization.Object)
            .AddSingleton<IBulkExportResourceSource>(_source)
            .BuildServiceProvider();

        return new BulkExportWorker(
            services.GetRequiredService<IServiceScopeFactory>(), _jobs, Files, Options.Create(_options), _time,
            NullLogger<BulkExportWorker>.Instance);
    }

    private async Task<BulkExportJob> StartAsync(string? types = null, string? since = null)
        => await Service().InitiateExportAsync(new BulkExportRequest
        {
            Level = BulkExportLevel.Group,
            GroupId = Provider,
            RequestedBy = Provider,
            Type = types,
            Since = since,
            RequestUrl = "https://fhir.test/fhir/r4/Group/prov-1/$export",
        }, Tenant);

    [Fact]
    public async Task CompletedExport_ContainsOnlyAuthorizedMembers_AndExpiresAfterFileLifetime()
    {
        _denied.Add("m2");
        var job = await StartAsync("Patient,Coverage");

        (await Worker().RunNextAsync()).Should().BeTrue();

        var record = await _jobs.GetAsync(Tenant, job.JobId);
        record!.Status.Should().Be(BulkExportStatus.Complete);
        record.TransactionTime.Should().Be(_time.GetUtcNow());
        record.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromHours(24));
        record.Files.Select(f => (f.Type, f.Name, f.Count)).Should().Equal(
            ("Patient", "Patient-1.ndjson", 2), ("Coverage", "Coverage-1.ndjson", 2));

        var lines = await ReadLinesAsync(job.JobId, "Patient-1.ndjson");
        lines.Should().Equal("""{"resourceType":"Patient","id":"m1"}""", """{"resourceType":"Patient","id":"m3"}""");
        _source.Reads.Should().NotContain(r => r.Member == "m2", "a member who is not authorized is never read");
    }

    [Fact]
    public async Task Files_RotateAtMaxResourcesPerFile()
    {
        _options.MaxResourcesPerFile = 2;
        var job = await StartAsync("Patient");

        await Worker().RunNextAsync();

        var record = await _jobs.GetAsync(Tenant, job.JobId);
        record!.Files.Select(f => (f.Name, f.Count)).Should().Equal(("Patient-1.ndjson", 2), ("Patient-2.ndjson", 1));
    }

    [Fact]
    public async Task TypeWithNoResources_WritesNoFile()
    {
        _source.Empty.Add("Claim");
        var job = await StartAsync("Patient,Claim");

        await Worker().RunNextAsync();

        (await _jobs.GetAsync(Tenant, job.JobId))!.Files.Select(f => f.Type).Should().Equal("Patient");
    }

    [Fact]
    public async Task NoWaitingJob_ReturnsFalse()
        => (await Worker().RunNextAsync()).Should().BeFalse();

    [Fact]
    public async Task SourceFailure_FailsJobWithGenericError_AndLeavesNoFiles()
    {
        _source.FailOn = "Coverage";
        var job = await StartAsync("Patient,Coverage");

        await Worker().RunNextAsync();

        var record = await _jobs.GetAsync(Tenant, job.JobId);
        record!.Status.Should().Be(BulkExportStatus.Error);
        record.Error.Should().Be(BulkExportWorker.FailureMessage);
        record.Files.Should().BeEmpty();
        (await _documents.ExistsAsync(BulkExportOptions.Container,
            DocumentStoreBulkExportFileStore.KeyFor(Tenant, job.JobId, "Patient-1.ndjson"))).Should().BeFalse();
    }

    [Fact]
    public async Task CancelledWhileRunning_StopsAndWritesNothing()
    {
        var job = await StartAsync("Patient");
        _source.OnRead = () => _jobs.CancelAsync(Tenant, job.JobId, _time.GetUtcNow());

        await Worker().RunNextAsync();

        var record = await _jobs.GetAsync(Tenant, job.JobId);
        record!.Status.Should().Be(BulkExportStatus.Cancelled);
        record.Files.Should().BeEmpty();
        _source.Reads.Should().HaveCount(1, "the worker stops at the first progress report after the cancel");
    }

    [Fact]
    public async Task CompletedExport_IsGoneAfterFileLifetime_AndPurgedAfterPurgeAfter()
    {
        var job = await StartAsync("Patient");
        await Worker().RunNextAsync();
        var service = Service();
        (await service.OpenFileAsync(job.JobId, "Patient-1.ndjson", Tenant)).Should().NotBeNull();

        _time.Advance(TimeSpan.FromHours(24));
        (await service.GetJobStatusAsync(job.JobId, Tenant)).Should().BeNull();
        (await service.OpenFileAsync(job.JobId, "Patient-1.ndjson", Tenant)).Should().BeNull();
        (await Worker().PurgeAsync()).Should().Be(0, "files are kept until PurgeAfter");

        _time.Advance(TimeSpan.FromDays(1));
        (await Worker().PurgeAsync()).Should().Be(1);
        (await _documents.ExistsAsync(BulkExportOptions.Container,
            DocumentStoreBulkExportFileStore.KeyFor(Tenant, job.JobId, "Patient-1.ndjson"))).Should().BeFalse();
        (await _jobs.GetAsync(Tenant, job.JobId))!.Purged.Should().BeTrue();
    }

    [Fact]
    public async Task CancelledCompletedExport_IsPurgedAtOnce()
    {
        var job = await StartAsync("Patient");
        await Worker().RunNextAsync();

        (await Service().CancelJobAsync(job.JobId, Tenant)).Should().BeTrue();
        (await Worker().PurgeAsync()).Should().Be(1);

        (await _documents.ExistsAsync(BulkExportOptions.Container,
            DocumentStoreBulkExportFileStore.KeyFor(Tenant, job.JobId, "Patient-1.ndjson"))).Should().BeFalse();
    }

    [Fact]
    public async Task OpenFile_OnlyServesAFileTheJobListed_InItsOwnTenant()
    {
        var job = await StartAsync("Patient");
        await Worker().RunNextAsync();
        var service = Service();

        (await service.OpenFileAsync(job.JobId, "Coverage-1.ndjson", Tenant)).Should().BeNull();
        (await service.OpenFileAsync(job.JobId, "../other/Patient-1.ndjson", Tenant)).Should().BeNull();
        (await service.OpenFileAsync(job.JobId, "Patient-1.ndjson", "tenant-b")).Should().BeNull();
    }

    [Fact]
    public async Task Since_IsPassedToTheSource()
    {
        await StartAsync("Patient", "2026-01-01T00:00:00Z");
        await Worker().RunNextAsync();
        _source.Reads.Should().OnlyContain(r => r.Since == new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Initiate_RefusesAnotherProvidersGroup_AndSystemExport()
    {
        var service = Service();
        await service.Invoking(s => s.InitiateExportAsync(
                new BulkExportRequest { Level = BulkExportLevel.Group, GroupId = "prov-2", RequestedBy = Provider }, Tenant))
            .Should().ThrowAsync<BulkExportGroupNotFoundException>();
        await service.Invoking(s => s.InitiateExportAsync(
                new BulkExportRequest { Level = BulkExportLevel.Group, GroupId = Provider, RequestedBy = null }, Tenant))
            .Should().ThrowAsync<BulkExportGroupNotFoundException>();
        await service.Invoking(s => s.InitiateExportAsync(
                new BulkExportRequest { RequestedBy = Provider }, Tenant))
            .Should().ThrowAsync<BulkExportNotImplementedException>();
        _jobs.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task Initiate_PatientExport_IsTheCallersPanel_WithNoGroup()
    {
        var job = await Service().InitiateExportAsync(
            new BulkExportRequest { Level = BulkExportLevel.Patient, GroupId = "ignored", RequestedBy = Provider }, Tenant);

        job.Level.Should().Be(BulkExportLevel.Patient);
        job.GroupId.Should().BeNull();
        job.RequestedBy.Should().Be(Provider);

        await Service().Invoking(s => s.InitiateExportAsync(
                new BulkExportRequest { Level = BulkExportLevel.Patient, RequestedBy = " " }, Tenant))
            .Should().ThrowAsync<BulkExportRequestException>();
    }

    [Theory]
    [InlineData("Patient,Observation", null, null)]
    [InlineData(null, "2026-01-01", null)]
    [InlineData(null, "2026-01-01T00:00:00", null)]
    [InlineData(null, "yesterday", null)]
    [InlineData(null, null, "application/fhir+json")]
    public async Task Initiate_RefusesMalformedRequests(string? type, string? since, string? format)
    {
        await Service().Invoking(s => s.InitiateExportAsync(new BulkExportRequest
            {
                Level = BulkExportLevel.Group, GroupId = Provider, RequestedBy = Provider, Type = type, Since = since,
                OutputFormat = format,
            }, Tenant))
            .Should().ThrowAsync<BulkExportRequestException>();
        _jobs.Jobs.Should().BeEmpty();
    }

    [Fact]
    public void Types_DefaultToAllSupported_AndAreWrittenInSupportedOrder()
    {
        DurableBulkExportService.ParseTypes(null).Should().Equal(BulkExportOptions.SupportedTypes);
        DurableBulkExportService.ParseTypes("Claim, Patient").Should().Equal("Patient", "Claim");
    }

    [Fact]
    public void Since_AcceptsInstantsWithAnOffset()
        => DurableBulkExportService.ParseSince("2026-01-01T05:00:00+05:00")
            .Should().Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private async Task<List<string>> ReadLinesAsync(string jobId, string name)
    {
        await using var stream = await Service().OpenFileAsync(jobId, name, Tenant);
        using var reader = new StreamReader(stream!);
        return (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private sealed class FakeResourceSource : IBulkExportResourceSource
    {
        public List<(string Member, string Type, DateTimeOffset? Since)> Reads { get; } = new();
        public HashSet<string> Empty { get; } = new();
        public string? FailOn { get; set; }
        public Func<Task>? OnRead { get; set; }

        public async IAsyncEnumerable<string> ReadAsync(
            string tenantId, string memberId, string resourceType, DateTimeOffset? since,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Reads.Add((memberId, resourceType, since));
            if (OnRead is not null)
                await OnRead();
            if (resourceType == FailOn)
                throw new HttpRequestException("upstream down");
            if (!Empty.Contains(resourceType))
                yield return $$"""{"resourceType":"{{resourceType}}","id":"{{memberId}}"}""";
        }
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
