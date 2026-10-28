using System.Text;
using System.Text.Json;
using Hl7.Fhir.Serialization;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Models;
using FhirService.Services.PayerToPayer;
using FhirService.Services.PayerToPayer.BulkMemberMatch;
using FhirService.Services.ProviderAccess;
using Microsoft.Extensions.Options;

namespace FhirService.Services.BulkExport;

/// <summary>
/// Runs accepted bulk exports and deletes the files of finished ones.
///
/// <para>
/// One job at a time per replica. A job is claimed with a lease that progress
/// keeps extending, so a replica that stops mid-export leaves the job to be
/// picked up again by another once the lease runs out.
/// </para>
///
/// <para>
/// A Provider Access export's members are the provider's attributed panel at
/// the time the export runs, and every one of them is asked through the same
/// Provider Access decision a single read is: attribution again, then the
/// member's opt-out. A member who opted out is left out of the file, not
/// reported.
/// </para>
///
/// <para>
/// A Payer-to-Payer export's members are those whose consent naming the
/// requesting payer is in force when it runs, and each is exported through the
/// single-member Payer-to-Payer exchange, which checks that consent again.
/// </para>
/// </summary>
public sealed class BulkExportWorker : BackgroundService
{
    internal const string FailureMessage = "The export could not be completed.";

    /// <summary>
    /// What the error file says for a <c>patient</c> the export did not
    /// include. One wording whether the patient does not exist or is outside
    /// the caller's scope, so the file never tells a caller which.
    /// </summary>
    internal const string PatientNotExported = "Not exported: no such patient is available to this export.";

    /// <summary>The manifest <c>error</c> file's type and name stem.</summary>
    internal const string ErrorFileType = "OperationOutcome";

    private static readonly JsonSerializerOptions FhirJson =
        new JsonSerializerOptions().ForFhir(typeof(Hl7.Fhir.Model.Patient).Assembly);
    private const int PurgeBatch = 50;
    private static readonly TimeSpan ProgressHeartbeat = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IBulkExportJobStore _jobs;
    private readonly IBulkExportFileStore _files;
    private readonly BulkExportOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BulkExportWorker> _logger;

    public BulkExportWorker(
        IServiceScopeFactory scopes,
        IBulkExportJobStore jobs,
        IBulkExportFileStore files,
        IOptions<BulkExportOptions> options,
        TimeProvider time,
        ILogger<BulkExportWorker> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _files = files;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                await PurgeAsync(stoppingToken);
                worked = await RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk export worker pass failed.");
            }

            if (!worked)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, _time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Deletes the files of every job that is due. Returns how many jobs were purged.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var due = await _jobs.ListPurgeableAsync(_time.GetUtcNow(), PurgeBatch, ct);
        foreach (var job in due)
        {
            await DeleteFilesAsync(job.TenantId, job.JobId, job.Files.Select(f => f.Key), ct);
            await _jobs.MarkPurgedAsync(job.TenantId, job.JobId, ct);
        }
        return due.Count;
    }

    /// <summary>Claims and runs the next waiting export. False when there was none.</summary>
    public async Task<bool> RunNextAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var job = await _jobs.ClaimNextAsync(now, now + _options.LeaseDuration, ct);
        if (job is null)
            return false;

        var work = Path.Combine(Path.GetTempPath(), "cho-bulk-export", job.JobId);
        var uploaded = new List<string>();
        try
        {
            Directory.CreateDirectory(work);
            await RunAsync(job, work, uploaded, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another replica starts the
            // export again, writing the same file names over these.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk export {JobId} for tenant {TenantId} failed.",
                LogSanitizer.Sanitize(job.JobId), LogSanitizer.Sanitize(job.TenantId));
            await DeleteFilesAsync(job.TenantId, job.JobId, uploaded, CancellationToken.None);
            var at = _time.GetUtcNow();
            await _jobs.FailAsync(job.TenantId, job.JobId, FailureMessage, at, at + _options.PurgeAfter, CancellationToken.None);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
        return true;
    }

    private async Task RunAsync(BulkExportJobRecord job, string work, List<string> uploaded, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();

        // The upstream reads (the FHIR data adapter, the Payer-to-Payer member
        // directory) take their tenant from the request. An export has none,
        // so it runs in a context naming only its tenant, with no user:
        // nothing here acts for a person, and calls to CHO services carry
        // fhir-service's own service token for that tenant.
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = BackgroundTenantContext.Create(scope.ServiceProvider, job.TenantId);
        try
        {
            await RunInTenantAsync(job, scope.ServiceProvider, work, uploaded, ct);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private async Task RunInTenantAsync(
        BulkExportJobRecord job, IServiceProvider services, string work, List<string> uploaded, CancellationToken ct)
    {
        // The data is read from here on; the manifest says it is current as of now.
        var transactionTime = _time.GetUtcNow();
        var payerToPayer = job.Level == BulkExportLevel.PayerToPayer;
        var members = payerToPayer
            ? await PayerToPayerMembersAsync(job, services, transactionTime, ct)
            : await AuthorizedMembersAsync(
                job,
                services.GetRequiredService<IProviderAttributionSource>(),
                services.GetRequiredService<IProviderAccessAuthorizationService>(),
                ct);

        // The patient parameter narrows the members, never widens them.
        var notExported = new List<string>();
        if (job.Patients is { } requested)
        {
            notExported = requested.Where(p => !members.Contains(p, StringComparer.Ordinal)).ToList();
            members = members.Where(m => requested.Contains(m, StringComparer.Ordinal)).ToList();
        }

        var filter = BulkExportTypeFilter.Parse(job.TypeFilters, job.Types);

        var progress = new ProgressReporter(this, job);
        var writers = job.Types.ToDictionary(
            t => t, t => new RotatingNdjsonWriter(work, t, _options.MaxResourcesPerFile), StringComparer.Ordinal);
        var written = new List<(string Type, string Name, int Count, string Path)>();
        try
        {
            var steps = Math.Max(1, members.Count);
            var step = 0;
            foreach (var member in members)
            {
                if (payerToPayer)
                {
                    // Each member is exported as the single-member export
                    // would answer the payer now, consent checked again.
                    var source = services.GetRequiredService<IPayerToPayerBulkSource>();
                    await foreach (var (type, line) in source.ReadAsync(
                                       job.TenantId, job.ReceivingPayerId!, job.RequestedBy, member, job.Types, job.Since, ct))
                    {
                        if (Passes(filter, type, line))
                            await writers[type].WriteAsync(line, ct);
                    }
                }
                else
                {
                    var source = services.GetRequiredService<IBulkExportResourceSource>();
                    foreach (var type in job.Types)
                    {
                        await foreach (var line in source.ReadAsync(job.TenantId, member, type, job.Since, ct))
                        {
                            if (Passes(filter, type, line))
                                await writers[type].WriteAsync(line, ct);
                        }
                    }
                }

                step++;
                if (!await progress.ReportAsync(step * 99 / steps, ct))
                {
                    _logger.LogInformation("Bulk export {JobId} was cancelled while running.", LogSanitizer.Sanitize(job.JobId));
                    return;
                }
            }

            // Files listed in the supported type order.
            foreach (var type in job.Types)
            {
                await writers[type].CloseAsync(ct);
                written.AddRange(writers[type].Files.Select(f => (type, f.Name, f.Count, f.Path)));
            }

            // Bulk Data: problems with the request go in the manifest's error
            // array, as OperationOutcome NDJSON.
            if (notExported.Count > 0)
            {
                await using var errors = new RotatingNdjsonWriter(work, ErrorFileType, _options.MaxResourcesPerFile);
                foreach (var patient in notExported)
                    await errors.WriteAsync(NotExportedOutcome(patient), ct);
                await errors.CloseAsync(ct);
                written.AddRange(errors.Files.Select(f => (ErrorFileType, f.Name, f.Count, f.Path)));
            }
        }
        finally
        {
            foreach (var writer in writers.Values)
                await writer.DisposeAsync();
        }

        var stored = new List<BulkExportStoredFile>();
        foreach (var file in written)
        {
            await using var content = File.OpenRead(file.Path);
            var key = await _files.UploadAsync(job.TenantId, job.JobId, file.Name, content, ct);
            uploaded.Add(key);
            stored.Add(new BulkExportStoredFile(file.Type, file.Name, file.Count, key));
        }

        var completedAt = _time.GetUtcNow();
        var completed = await _jobs.CompleteAsync(
            job.TenantId, job.JobId, stored, transactionTime, completedAt,
            completedAt + _options.FileLifetime, completedAt + _options.PurgeAfter, ct);

        if (!completed)
        {
            // Cancelled while the files were uploading: they must not outlive it.
            await DeleteFilesAsync(job.TenantId, job.JobId, uploaded, CancellationToken.None);
            return;
        }

        _logger.LogInformation(
            "Bulk export {JobId} for tenant {TenantId} completed: {Members} members, {Files} files.",
            LogSanitizer.Sanitize(job.JobId), LogSanitizer.Sanitize(job.TenantId), members.Count, stored.Count);
    }

    /// <summary>
    /// Whether <paramref name="line"/> passes the export's <c>_typeFilter</c>.
    /// Parsed only for a type a query names.
    /// </summary>
    internal static bool Passes(BulkExportTypeFilter filter, string type, string line)
    {
        if (!filter.Filters(type))
            return true;
        var resource = JsonSerializer.Deserialize<Hl7.Fhir.Model.Resource>(line, FhirJson);
        return resource is not null && filter.Matches(resource);
    }

    private static string NotExportedOutcome(string patientId)
        => JsonSerializer.Serialize(new Hl7.Fhir.Model.OperationOutcome
        {
            Issue =
            [
                new Hl7.Fhir.Model.OperationOutcome.IssueComponent
                {
                    Severity = Hl7.Fhir.Model.OperationOutcome.IssueSeverity.Error,
                    Code = Hl7.Fhir.Model.OperationOutcome.IssueType.NotFound,
                    Diagnostics = PatientNotExported,
                    Expression = [$"Patient/{patientId}"],
                },
            ],
        }, FhirJson);

    /// <summary>
    /// The members a Payer-to-Payer export covers: those of its match Group
    /// (PDex <c>$davinci-data-export</c>), or else every member whose consent
    /// for the payer is in force. Listing is not the decision: each member's
    /// consent is judged again when it is exported.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PayerToPayerMembersAsync(
        BulkExportJobRecord job, IServiceProvider services, DateTimeOffset now, CancellationToken ct)
    {
        if (job.GroupId is { } groupId)
        {
            var group = services.GetService<IPayerToPayerMatchGroupStore>() is { } groups
                ? await groups.GetAsync(job.TenantId, groupId, now, ct)
                : null;
            return group?.MemberIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [];
        }

        return await services.GetRequiredService<IPayerToPayerConsentAttestationStore>()
            .ListMembersInForceAsync(job.TenantId, job.ReceivingPayerId!, now.UtcDateTime, ct);
    }

    /// <summary>
    /// The provider's panel, each member authorized as a single Provider Access
    /// read would be. The provider is the caller who started the export, which
    /// the kick-off already required to be the Group.
    /// </summary>
    private static async Task<IReadOnlyList<string>> AuthorizedMembersAsync(
        BulkExportJobRecord job,
        IProviderAttributionSource attribution,
        IProviderAccessAuthorizationService authorization,
        CancellationToken ct)
    {
        var panel = await attribution.ListAttributedAsync(job.TenantId, job.RequestedBy, ct);
        var members = new List<string>();
        foreach (var member in panel.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var decision = await authorization.AuthorizeAsync(
                new ProviderAccessRequest { TenantId = job.TenantId, MemberId = member, ProviderId = job.RequestedBy }, ct);
            if (decision.Allowed)
                members.Add(member);
        }
        return members;
    }

    private async Task DeleteFilesAsync(string tenantId, string jobId, IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            try
            {
                await _files.DeleteAsync(key, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The bucket's own lifecycle rule deletes anything left behind.
                _logger.LogWarning(ex, "Could not delete a file of bulk export {JobId} for tenant {TenantId}.",
                    LogSanitizer.Sanitize(jobId), LogSanitizer.Sanitize(tenantId));
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Progress, written when it changes or at least once a minute so the
    /// lease outlives a long member. False once the job has been cancelled.
    /// </summary>
    private sealed class ProgressReporter(BulkExportWorker worker, BulkExportJobRecord job)
    {
        private int _percent = -1;
        private DateTimeOffset _lastReport = DateTimeOffset.MinValue;

        public async Task<bool> ReportAsync(int percent, CancellationToken ct)
        {
            var now = worker._time.GetUtcNow();
            if (percent == _percent && now - _lastReport < ProgressHeartbeat)
                return true;

            _percent = percent;
            _lastReport = now;
            return await worker._jobs.ReportProgressAsync(
                job.TenantId, job.JobId, percent, now + worker._options.LeaseDuration, ct);
        }
    }
}

/// <summary>
/// NDJSON lines of one resource type, as <c>{Type}-1.ndjson</c>,
/// <c>{Type}-2.ndjson</c>… with at most <c>maxPerFile</c> lines each. A type
/// with no resources writes no file, as the Bulk Data IG asks.
/// </summary>
internal sealed class RotatingNdjsonWriter : IAsyncDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly string _directory;
    private readonly string _type;
    private readonly int _maxPerFile;
    private readonly List<(string Name, int Count, string Path)> _files = new();
    private FileStream? _current;
    private int _count;

    public RotatingNdjsonWriter(string directory, string type, int maxPerFile)
    {
        _directory = directory;
        _type = type;
        _maxPerFile = maxPerFile;
    }

    public IReadOnlyList<(string Name, int Count, string Path)> Files => _files;

    public async Task WriteAsync(string line, CancellationToken ct)
    {
        if (_current is null || _count >= _maxPerFile)
        {
            await CloseAsync(ct);
            var name = $"{_type}-{_files.Count + 1}.ndjson";
            var path = Path.Combine(_directory, name);
            _current = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            _files.Add((name, 0, path));
            _count = 0;
        }

        await _current.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
        await _current.WriteAsync(NewLine, ct);
        _count++;
        _files[^1] = _files[^1] with { Count = _count };
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        if (_current is null)
            return;
        await _current.FlushAsync(ct);
        await _current.DisposeAsync();
        _current = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is not null)
            await _current.DisposeAsync();
        _current = null;
    }
}
