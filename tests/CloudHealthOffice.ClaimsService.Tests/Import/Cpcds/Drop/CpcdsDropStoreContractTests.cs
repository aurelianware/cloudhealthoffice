using System.Net;
using System.Text;
using ClaimsService.Import.Cpcds.Drop;
using FluentAssertions;
using Google;
using Google.Cloud.Storage.V1;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Import.Cpcds.Drop;

/// <summary>
/// What every drop store must do, whatever it stores files in: list only
/// settled files, give each file to exactly one run, and move a run's files
/// out of processing when it ends.
/// </summary>
public abstract class CpcdsDropStoreContractTests : IAsyncLifetime
{
    protected string Tenant { get; } = "plan-" + Guid.NewGuid().ToString("N")[..8];

    protected ICpcdsDropStore Store { get; private set; } = null!;

    /// <summary>Later than anything written in the test, so every file is settled.</summary>
    private static DateTime Later => DateTime.UtcNow.AddHours(1);

    protected abstract Task<ICpcdsDropStore> CreateStoreAsync();

    /// <summary>Writes a file into the tenant's inbound folder, as an uploader would.</summary>
    protected abstract Task DropAsync(string name, string content);

    /// <summary>Replaces an inbound file, as an uploader rewriting it would.</summary>
    protected virtual Task RewriteAsync(string name, string content) => DropAsync(name, content);

    /// <summary>The names under one of the tenant's folders, relative to it.</summary>
    protected abstract Task<IReadOnlyList<string>> NamesAsync(params string[] folder);

    /// <summary>Whether the store can tell a rewritten file from the one it listed.</summary>
    protected virtual bool DetectsRewrites => false;

    public async Task InitializeAsync() => Store = await CreateStoreAsync();

    /// <summary>Skips the test when the store's backing service is not available here.</summary>
    protected virtual void SkipIfUnavailable()
    {
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task Lists_settled_files_in_name_order_and_skips_partial_uploads()
    {
        SkipIfUnavailable();
        await DropAsync("CPCDS_Members.csv", "a\n1\n");
        await DropAsync("CPCDS_Claims.csv", "a\n1\n");
        await DropAsync("CPCDS_Claims.csv.part", "a\n");
        await DropAsync(".hidden", "a\n");

        var ready = await Store.ListReadyAsync(Tenant, TimeSpan.FromMinutes(2), Later, default);

        ready.Select(f => f.Name).Should().Equal("CPCDS_Claims.csv", "CPCDS_Members.csv");
        ready[0].Length.Should().Be(4);
    }

    [SkippableFact]
    public async Task A_file_still_being_written_is_not_ready()
    {
        SkipIfUnavailable();
        await DropAsync("CPCDS_Claims.csv", "a\n1\n");

        (await Store.ListReadyAsync(Tenant, TimeSpan.FromMinutes(2), DateTime.UtcNow, default)).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_tenant_with_no_drop_has_nothing_ready()
    {
        SkipIfUnavailable();
        (await Store.ListReadyAsync("nobody-" + Guid.NewGuid().ToString("N")[..6], TimeSpan.Zero, Later, default))
            .Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Each_file_goes_to_exactly_one_run_and_reads_back_whole()
    {
        SkipIfUnavailable();
        await DropAsync("CPCDS_Claims.csv", "Claim unique identifier\nC-1\n");
        var listed = (await Store.ListReadyAsync(Tenant, TimeSpan.Zero, Later, default)).Single();

        var first = await Store.ClaimAsync(Tenant, "run-1", listed, default);
        var second = await Store.ClaimAsync(Tenant, "run-2", listed, default);

        first.Should().NotBeNull();
        second.Should().BeNull();
        (await Store.ReadAllTextAsync(first!, default)).Should().Be("Claim unique identifier\nC-1\n");
        (await NamesAsync("inbound")).Should().BeEmpty();
        (await NamesAsync("processing", "run-1")).Should().Equal("CPCDS_Claims.csv");
        (await NamesAsync("processing", "run-2")).Should().BeEmpty("the run that lost leaves no copy behind");
    }

    [SkippableFact]
    public async Task A_file_rewritten_after_it_was_listed_waits_for_the_next_poll()
    {
        SkipIfUnavailable();
        Skip.IfNot(DetectsRewrites, "This store cannot tell a rewritten file from the one it listed.");

        await DropAsync("CPCDS_Claims.csv", "old\n");
        var listed = (await Store.ListReadyAsync(Tenant, TimeSpan.Zero, Later, default)).Single();
        await RewriteAsync("CPCDS_Claims.csv", "new\n");

        (await Store.ClaimAsync(Tenant, "run-1", listed, default)).Should().BeNull();
        (await NamesAsync("inbound")).Should().Equal("CPCDS_Claims.csv");
        (await NamesAsync("processing", "run-1")).Should().BeEmpty();
    }

    [SkippableTheory]
    [InlineData(true, "processed")]
    [InlineData(false, "failed")]
    public async Task Completing_a_run_moves_its_files_out_of_processing(bool succeeded, string folder)
    {
        SkipIfUnavailable();
        await DropAsync("CPCDS_Members.csv", "a\n");
        await DropAsync("CPCDS_Claims.csv", "a\n");
        foreach (var file in await Store.ListReadyAsync(Tenant, TimeSpan.Zero, Later, default))
            await Store.ClaimAsync(Tenant, "run-1", file, default);

        var archivedTo = await Store.CompleteAsync(Tenant, "run-1", succeeded, default);

        archivedTo.Should().Contain(folder).And.Contain("run-1");
        (await NamesAsync("processing", "run-1")).Should().BeEmpty();
        (await NamesAsync(folder, "run-1")).Should().BeEquivalentTo("CPCDS_Members.csv", "CPCDS_Claims.csv");
    }

    [SkippableFact]
    public async Task A_tenant_id_that_cannot_name_a_folder_is_refused()
    {
        SkipIfUnavailable();
        var act = () => Store.ListReadyAsync("../other", TimeSpan.Zero, Later, default);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}

public sealed class FolderCpcdsDropStoreContractTests : CpcdsDropStoreContractTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cpcds-drop-contract-" + Guid.NewGuid().ToString("N"));

    protected override Task<ICpcdsDropStore> CreateStoreAsync()
        => Task.FromResult<ICpcdsDropStore>(new FolderCpcdsDropStore(_root));

    protected override Task DropAsync(string name, string content)
    {
        var inbound = Directory.CreateDirectory(Path.Combine(_root, Tenant, "inbound")).FullName;
        return File.WriteAllTextAsync(Path.Combine(inbound, name), content);
    }

    protected override Task<IReadOnlyList<string>> NamesAsync(params string[] folder)
    {
        var path = Path.Combine([_root, Tenant, .. folder]);
        IReadOnlyList<string> names = Directory.Exists(path)
            ? Directory.EnumerateFiles(path).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToList()
            : [];
        return Task.FromResult(names);
    }

    public override Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
}

/// <summary>fake-gcs-server. <c>CHO_TEST_GCS_ENDPOINT</c> is its base URL, e.g. <c>http://127.0.0.1:4443</c>; CI starts it.</summary>
[Trait("Category", "Integration")]
public sealed class GcsCpcdsDropStoreContractTests : CpcdsDropStoreContractTests
{
    public const string EnvVar = "CHO_TEST_GCS_ENDPOINT";
    private const string Bucket = "cho-cpcds-drop-tests";

    private StorageClient? _client;

    protected override bool DetectsRewrites => true;

    protected override async Task<ICpcdsDropStore> CreateStoreAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(endpoint))
            return null!; // each test skips below

        _client = await new StorageClientBuilder
        {
            BaseUri = endpoint.TrimEnd('/') + "/storage/v1/",
            UnauthenticatedAccess = true,
        }.BuildAsync();

        try
        {
            await _client.CreateBucketAsync("cho-test-project", Bucket);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Conflict)
        {
            // Created by an earlier test.
        }

        return new GcsCpcdsDropStore(_client, Bucket);
    }

    protected override void SkipIfUnavailable()
        => Skip.If(_client is null, $"Set {EnvVar} to run the Cloud Storage drop store tests.");

    private StorageClient Client => _client!;

    protected override async Task DropAsync(string name, string content)
        => await Client.UploadObjectAsync(Bucket, $"{Tenant}/inbound/{name}", "text/csv",
            new MemoryStream(Encoding.UTF8.GetBytes(content)));

    protected override async Task<IReadOnlyList<string>> NamesAsync(params string[] folder)
    {
        var prefix = string.Join('/', [Tenant, .. folder]) + "/";
        var names = new List<string>();
        await foreach (var obj in Client.ListObjectsAsync(Bucket, prefix, new ListObjectsOptions { Delimiter = "/" }))
            names.Add(obj.Name[prefix.Length..]);
        return names.Order(StringComparer.Ordinal).ToList();
    }
}
