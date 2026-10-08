using System.IO.Compression;
using System.Net;
using System.Text;
using CloudHealthOffice.ProviderVerificationEngine.DataSources;
using CloudHealthOffice.ProviderVerificationEngine.DataSources.Exclusions;
using CloudHealthOffice.ProviderVerificationEngine.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using static CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions.ExclusionTestData;

namespace CloudHealthOffice.ProviderVerificationEngine.Tests.Exclusions;

public class ExclusionDatasetSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private const string SamExtractCsv =
        "Classification,Name,Prefix,First,Middle,Last,Suffix,Address 1,City,State / Province,Zip Code,Unique Entity ID,Exclusion Program,Excluding Agency,CT Code,Exclusion Type,Additional Comments,Active Date,Termination Date,Record Status,CAGE,NPI,Creation_Date\n" +
        "Individual,\"DOE, JANE\",,JANE,,DOE,,\"1 ELM ST, APT 3\",DALLAS,TX,75201,,Reciprocal,HHS,,Prohibition/Restriction,\"Comment, with comma\",03/01/2022,Indefinite,Active,,1497758544,03/01/2022\n" +
        "Firm,\"Acme Durable Medical, Inc.\",,,,,,9 OAK AVE,HOUSTON,TX,77001,ZZZ111222333,Reciprocal,HHS,,Prohibition/Restriction,,05/05/2023,Indefinite,Active,,,05/05/2023\n";

    private readonly InMemoryExclusionRecordStore _store = new();
    private readonly FixedTimeProvider _time = new(Now);

    [Fact]
    public async Task LeieSync_DownloadsParsesAndLoads_ThenScreeningIsCurrent()
    {
        var options = Options(o => o.Leie.DownloadUrl = "https://example.test/UPDATED.csv");
        var handler = new StubHandler((_, _) => StubHandler.Text(Leie(
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"),
            LeieRow(bus: "ACME CLINIC, INC"))));
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<LeieDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal("https://example.test/UPDATED.csv", Assert.Single(handler.Requests).ToString());
        var status = await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie);
        Assert.Equal(Now, status!.LastSuccessfulSyncAt);
        Assert.Equal(2, status.RecordCount);

        var screener = new LocalExclusionListScreener(ExclusionScreeningSource.OigLeie, _store, Wrap(options),
            NullLogger<LocalExclusionListScreener>.Instance, _time);
        var outcome = await screener.ScreenAsync(new ProviderScreeningRequest { Npi = "1234567893" }, default);
        Assert.True(outcome.Status.WasScreened);
        Assert.True(outcome.IsExcluded);
    }

    [Fact]
    public async Task LeieSync_TooFewRecords_IsRejected_AndPreviousDatasetKept()
    {
        await SeedLeieAsync(_store, Now.AddDays(-2),
            LeieRow(last: "DOE", first: "JOHN", npi: "1234567893"),
            LeieRow(last: "ROE", first: "MARY", npi: "1497758544"));
        var options = Options(o => o.Leie.MinimumRecordCount = 2);
        var handler = new StubHandler((_, _) => StubHandler.Text(Leie(LeieRow(last: "X", first: "Y"))));
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<LeieDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.False(result.Succeeded);
        var status = await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie);
        Assert.Equal(Now.AddDays(-2), status!.LastSuccessfulSyncAt); // timestamp not refreshed
        Assert.Equal(2, status.RecordCount);
        Assert.NotNull(status.LastError);
        Assert.Single(await _store.FindByNpiAsync(ExclusionScreeningSource.OigLeie, status.ActiveSyncId!, "1234567893"));
    }

    [Fact]
    public async Task LeieSync_ShrunkDataset_IsRejected()
    {
        await SeedLeieAsync(_store, Now.AddDays(-2),
            LeieRow(last: "A", first: "A"), LeieRow(last: "B", first: "B"),
            LeieRow(last: "C", first: "C"), LeieRow(last: "D", first: "D"));
        var handler = new StubHandler((_, _) => StubHandler.Text(Leie(LeieRow(last: "E", first: "E"))));
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(Options()),
            NullLogger<LeieDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(4, (await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie))!.RecordCount);
    }

    [Fact]
    public async Task LeieSync_HtmlErrorPage_FailsWithoutTouchingDataset()
    {
        var handler = new StubHandler((_, _) => StubHandler.Text("<html>Access denied</html>"));
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(Options()),
            NullLogger<LeieDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.False(result.Succeeded);
        var status = await _store.GetSyncStatusAsync(ExclusionScreeningSource.OigLeie);
        Assert.Null(status!.LastSuccessfulSyncAt);
    }

    [Fact]
    public async Task SamExtractSync_UnzipsCsv_AndAppendsApiKey()
    {
        var zip = Zip("SAM_Exclusions_Public_Extract_V2_26281.CSV", SamExtractCsv);
        var handler = new StubHandler((_, _) => StubHandler.Bytes(zip));
        var options = Options(o => { o.Sam.Enabled = true; o.Sam.ApiKey = "k3y"; });
        var sync = new SamExtractDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<SamExtractDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(
            "https://api.sam.gov/data-services/v1/extracts?fileType=EXCLUSION&api_key=k3y",
            Assert.Single(handler.Requests).ToString());
        var status = await _store.GetSyncStatusAsync(ExclusionScreeningSource.SamGov);
        Assert.DoesNotContain("k3y", status!.SourceUrl);

        var screener = new LocalExclusionListScreener(ExclusionScreeningSource.SamGov, _store, Wrap(options),
            NullLogger<LocalExclusionListScreener>.Instance, _time);
        var jane = await screener.ScreenAsync(new ProviderScreeningRequest { Npi = "1497758544" }, default);
        Assert.True(jane.IsExcluded);
        var acme = await screener.ScreenAsync(
            new ProviderScreeningRequest { Npi = "1234567893", OrganizationName = "ACME DURABLE MEDICAL LLC" }, default);
        Assert.False(acme.IsExcluded);
        Assert.Equal("BUSINESS_NAME", Assert.Single(acme.Matches).MatchBasis);
    }

    [Fact]
    public async Task SamExtractSync_RejectedKey_WarnsWithoutLoggingKey()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{\"error\":\"API_KEY_INVALID\"}", HttpStatusCode.Unauthorized));
        var logger = new ListLogger<SamExtractDatasetSync>();
        var options = Options(o => { o.Sam.Enabled = true; o.Sam.ApiKey = "super-secret-sam-key"; });
        var sync = new SamExtractDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options), logger, _time);

        var result = await sync.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("rejected or expired", result.Error);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("90 days"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("super-secret-sam-key"));
        Assert.DoesNotContain("super-secret-sam-key", (await _store.GetSyncStatusAsync(ExclusionScreeningSource.SamGov))!.LastError);
    }

    [Fact]
    public async Task SamExtractSync_WithoutKey_DoesNotDownload()
    {
        var handler = new StubHandler((_, _) => StubHandler.Text(SamExtractCsv));
        var options = Options(o => { o.Sam.Enabled = true; o.Sam.ApiKey = null; });
        var sync = new SamExtractDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<SamExtractDatasetSync>.Instance, _time);

        var result = await sync.SyncAsync();

        Assert.False(result.Succeeded);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task HostedService_SyncsOnlyWhenDue()
    {
        var handler = new StubHandler((_, _) => StubHandler.Text(Leie(LeieRow(last: "DOE", first: "JOHN"))));
        var options = Options();
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<LeieDatasetSync>.Instance, _time);
        var worker = new ExclusionDatasetSyncHostedService([sync], _store, Wrap(options),
            NullLogger<ExclusionDatasetSyncHostedService>.Instance, _time);

        await worker.RunDueSyncsAsync(default);
        await worker.RunDueSyncsAsync(default);       // fresh: not due
        Assert.Single(handler.Requests);

        _time.Now = Now.AddDays(2);                     // past SyncInterval
        await worker.RunDueSyncsAsync(default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HostedService_BacksOffAfterFailure()
    {
        var handler = new StubHandler((_, _) => StubHandler.Text("", HttpStatusCode.InternalServerError));
        var options = Options();
        var sync = new LeieDatasetSync(new StubHttpClientFactory(handler), _store, Wrap(options),
            NullLogger<LeieDatasetSync>.Instance, _time);
        var worker = new ExclusionDatasetSyncHostedService([sync], _store, Wrap(options),
            NullLogger<ExclusionDatasetSyncHostedService>.Instance, _time);

        await worker.RunDueSyncsAsync(default);
        _time.Now = Now.AddHours(1);
        await worker.RunDueSyncsAsync(default);
        Assert.Single(handler.Requests);

        _time.Now = Now.AddHours(5);                    // past SyncRetryDelay (4h)
        await worker.RunDueSyncsAsync(default);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task HostedService_RetriesIndexCreationBeforeSyncing_UntilItSucceeds()
    {
        var store = NSubstitute.Substitute.For<IExclusionRecordStore>();
        var calls = 0;
        store.EnsureIndexesAsync(NSubstitute.Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? Task.FromException(new TimeoutException("mongo down")) : Task.CompletedTask);
        var sync = NSubstitute.Substitute.For<IExclusionDatasetSync>();
        sync.Source.Returns(ExclusionScreeningSource.OigLeie);
        sync.SyncAsync(NSubstitute.Arg.Any<CancellationToken>())
            .Returns(new ExclusionDatasetSyncResult { Source = ExclusionScreeningSource.OigLeie, Succeeded = true });
        var worker = new ExclusionDatasetSyncHostedService([sync], store, Wrap(Options()),
            NullLogger<ExclusionDatasetSyncHostedService>.Instance, _time);

        await worker.RunDueSyncsAsync(default);   // index creation fails: no sync yet
        await sync.DidNotReceive().SyncAsync(NSubstitute.Arg.Any<CancellationToken>());

        await worker.RunDueSyncsAsync(default);   // retried, succeeds, then syncs
        await worker.RunDueSyncsAsync(default);   // not retried again once ensured
        Assert.Equal(2, calls);
        await sync.Received(2).SyncAsync(NSubstitute.Arg.Any<CancellationToken>());
    }

    private static byte[] Zip(string entryName, string content)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return ms.ToArray();
    }
}
