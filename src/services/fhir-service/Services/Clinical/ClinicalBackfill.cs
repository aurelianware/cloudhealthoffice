using System.Text.Json;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Task = System.Threading.Tasks.Task;

namespace FhirService.Services.Clinical;

/// <summary>
/// Enumeration a clinical backfill needs from the import store. Kept out of
/// <see cref="IPayerToPayerImportRepository"/> because it is not part of the
/// ingestion contract: only the two real stores implement it, and nothing in the
/// exchange path depends on it.
/// </summary>
public interface IClinicalBackfillStore
{
    /// <summary>
    /// Every COMMITTED import ledger entry, oldest first, in bounded batches.
    /// Entries whose exchange never committed are not returned: a backfill must
    /// not publish a package the original ingestion refused.
    /// </summary>
    IAsyncEnumerable<PayerToPayerImportLedgerEntry> CommittedLedgerEntriesAsync(
        int batchSize, CancellationToken ct = default);

    /// <summary>
    /// Records that this exchange's clinical rows were produced by the backfill
    /// rather than by its original ingestion, and updates its counts to what the
    /// exchange now actually holds.
    /// </summary>
    Task UpdateBackfilledLedgerAsync(PayerToPayerImportLedgerEntry entry, CancellationToken ct = default);
}

/// <summary>Backfill configuration. Off by default — an operator turns it on deliberately.</summary>
public sealed class ClinicalBackfillOptions
{
    public const string SectionName = "Clinical:Backfill";

    /// <summary>
    /// Whether the backfill runs at startup. Default false: a deployment with no
    /// pre-existing Payer-to-Payer history has nothing to backfill and should not
    /// pay for a sweep, and one that does should schedule it knowingly.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Report what would be written without writing it.</summary>
    public bool DryRun { get; set; }

    /// <summary>Ledger entries read per batch.</summary>
    public int BatchSize { get; set; } = 50;
}

/// <summary>What one backfill run did. Counts only — no member, no content.</summary>
public sealed record ClinicalBackfillReport(
    int ExchangesExamined, int ExchangesBackfilled, int ResourcesStaged, int ResourcesRejected)
{
    public static readonly ClinicalBackfillReport Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Makes clinical data that Cloud Health Office already holds, but could not
/// serve, visible through the PAT-02 read path.
///
/// THE PROBLEM IT SOLVES. Before this change, a Payer-to-Payer package's
/// Condition, Observation and the rest were classified Unsupported: named and
/// counted on the exchange, preserved verbatim in the ledger's archived package,
/// but never staged as rows and therefore never readable. That data is already
/// durable. Requiring an operator to re-run a prior payer exchange to surface it
/// would mean asking another payer for data CHO already has, and would fail
/// outright wherever that payer relationship has ended.
///
/// WHAT IT DOES instead is re-read each COMMITTED exchange's archived package —
/// the payer's own bytes, untouched — and stage its clinical resources under
/// exactly the identities the original ingestion would have used had this
/// feature existed then. Because the import key is a pure function of
/// (tenant, member, source payer, type, source id), the ids a backfilled
/// resource gets are the same ids a re-import would produce, so a later exchange
/// carrying an updated version supersedes the backfilled one normally rather than
/// creating a second copy.
///
/// THE PROPERTIES THAT MAKE IT SAFE TO RUN:
///   * DETERMINISTIC — same archive in, same rows out, every time.
///   * REPLAY-SAFE — rows are keyed (tenant, exchange, import key) and written as
///     upserts, so running it twice, or after a partial run, converges. It is not
///     "run once and never again".
///   * NON-DESTRUCTIVE — it only adds clinical rows. It never touches member
///     history, administrative context, the archived package, or any row a real
///     exchange committed.
///   * TENANT-SAFE — tenant, member and source payer come from the LEDGER ENTRY
///     CHO wrote, never from the archived Bundle, so a package that names another
///     tenant or member is still filed under the exchange's own binding.
///   * COMMITTED ONLY — an exchange that failed or never committed is skipped, so
///     the backfill cannot publish what the original ingestion refused.
///   * GATED — the same payload validator the live path uses runs here, so a
///     resource too large or too deeply nested is refused by the backfill exactly
///     as it would be on arrival.
///
/// LIMITATION, stated plainly: a run interrupted part-way leaves the exchanges it
/// had reached backfilled and the rest not. Nothing is corrupted and nothing is
/// lost — the next run finishes the job — but the sweep is not one transaction.
/// </summary>
public sealed class ClinicalBackfillService
{
    private static readonly FhirJsonParser Parser = new(new ParserSettings { PermissiveParsing = true });
    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    private readonly IClinicalBackfillStore _backfillStore;
    private readonly IPayerToPayerImportRepository _repository;
    private readonly ClinicalPayloadValidator _validator;
    private readonly ClinicalBackfillOptions _options;
    private readonly ILogger<ClinicalBackfillService> _logger;

    public ClinicalBackfillService(
        IClinicalBackfillStore backfillStore,
        IPayerToPayerImportRepository repository,
        ClinicalPayloadValidator validator,
        IOptions<ClinicalBackfillOptions> options,
        ILogger<ClinicalBackfillService> logger)
    {
        _backfillStore = backfillStore;
        _repository = repository;
        _validator = validator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClinicalBackfillReport> RunAsync(CancellationToken ct = default)
    {
        var examined = 0;
        var backfilled = 0;
        var staged = 0;
        var rejected = 0;

        var batchSize = Math.Clamp(_options.BatchSize, 1, 500);

        await foreach (var ledger in _backfillStore.CommittedLedgerEntriesAsync(batchSize, ct))
        {
            ct.ThrowIfCancellationRequested();
            examined++;

            var outcome = Project(ledger);
            if (outcome.Resources.Count == 0 && outcome.Rejected == 0) continue;

            rejected += outcome.Rejected;

            if (_options.DryRun)
            {
                staged += outcome.Resources.Count;
                if (outcome.Resources.Count > 0) backfilled++;
                continue;
            }

            if (outcome.Resources.Count > 0)
            {
                await _repository.StageAsync(outcome.Resources, ct);
                staged += outcome.Resources.Count;
                backfilled++;
            }

            ApplyCounts(ledger, outcome);
            await _backfillStore.UpdateBackfilledLedgerAsync(ledger, ct);
        }

        var report = new ClinicalBackfillReport(examined, backfilled, staged, rejected);

        // Aggregate counts and a dry-run flag. No tenant list, no member, no type
        // breakdown that could narrow a member's record.
        _logger.LogInformation(
            "Clinical backfill complete: examined={Examined} backfilled={Backfilled} "
            + "staged={Staged} rejected={Rejected} dryRun={DryRun}",
            report.ExchangesExamined, report.ExchangesBackfilled,
            report.ResourcesStaged, report.ResourcesRejected, _options.DryRun);

        return report;
    }

    private readonly record struct ProjectionOutcome(
        IReadOnlyList<ImportedFhirResource> Resources,
        int Rejected,
        IReadOnlyList<string> RejectedReasons,
        IReadOnlyList<string> StillUnsupported);

    /// <summary>
    /// Re-derives one exchange's clinical rows from its archived package. Every
    /// identity component comes from the ledger entry, never from the Bundle.
    /// </summary>
    private ProjectionOutcome Project(PayerToPayerImportLedgerEntry ledger)
    {
        if (string.IsNullOrWhiteSpace(ledger.ArchivedPackageJson))
            return new ProjectionOutcome([], 0, [], []);

        Bundle bundle;
        try
        {
            bundle = Parser.Parse<Bundle>(ledger.ArchivedPackageJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            // Category only — the message can carry package content.
            _logger.LogWarning(
                "Clinical backfill skipped an exchange whose archived package could not be read: exchange={Exchange}",
                Clean(ledger.ExchangeId));
            return new ProjectionOutcome([], 0, [], []);
        }

        var resources = bundle.Entry?
            .Select(e => e.Resource).OfType<Resource>()
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToList() ?? [];

        // The archive is the package AS RECEIVED, so its references still point at
        // the peer. Normalize them the same way the live path does, to exactly the
        // same local identities — which is also why a backfilled row and a
        // re-imported one have the same content hash.
        var normalization = PayerToPayerReferenceNormalizer.Normalize(
            bundle,
            (type, id) => PayerToPayerImportPolicy.ImportKey(
                ledger.TenantId, ledger.MemberId, ledger.SourcePayerId, type, id));

        var staged = new List<ImportedFhirResource>();
        var rejectedReasons = new SortedSet<string>(StringComparer.Ordinal);
        var stillUnsupported = new SortedSet<string>(StringComparer.Ordinal);
        var rejected = 0;

        foreach (var resource in resources)
        {
            var classification = PayerToPayerImportPolicy.Classify(resource.TypeName);

            if (classification == ImportedResourceClass.Unsupported)
            {
                stillUnsupported.Add(resource.TypeName);
                continue;
            }

            // Only clinical rows are backfilled. Member history and administrative
            // context were staged by the original ingestion and are left exactly
            // as that exchange committed them.
            if (classification != ImportedResourceClass.ClinicalRecord) continue;

            string json;
            try
            {
                json = Serializer.SerializeToString(resource);
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
            {
                rejected++;
                rejectedReasons.Add($"{resource.TypeName}:{ClinicalPayloadRejection.Unreadable}");
                continue;
            }

            var rejection = _validator.Validate(resource, json);
            if (rejection != ClinicalPayloadRejection.None)
            {
                rejected++;
                rejectedReasons.Add($"{resource.TypeName}:{rejection}");
                continue;
            }

            staged.Add(new ImportedFhirResource
            {
                ImportKey = PayerToPayerImportPolicy.ImportKey(
                    ledger.TenantId, ledger.MemberId, ledger.SourcePayerId, resource.TypeName, resource.Id!),
                TenantId = ledger.TenantId,
                MemberId = ledger.MemberId,
                SourcePayerId = ledger.SourcePayerId,
                ExchangeId = ledger.ExchangeId,
                ResourceType = resource.TypeName,
                SourceResourceId = resource.Id!,
                // The original exchange's remote member id is not on the ledger;
                // the row's own tenant/member binding is what authorizes it, and
                // the source-side identity stays answerable from the archive.
                RemoteMemberId = string.Empty,
                Classification = ImportedResourceClass.ClinicalRecord,
                ResourceJson = json,
                ContentHash = PayerToPayerImportPolicy.ContentHash(json),
                // Per resource, not per package: a row nobody referenced must not
                // claim its references were rewritten.
                ReferencesNormalized = normalization.RewrittenResources.Contains(
                    $"{resource.TypeName}/{resource.Id}"),
                ReceivedAtUtc = ledger.CompletedAtUtc ?? ledger.StartedAtUtc,
                IngestedAtUtc = ledger.CompletedAtUtc ?? ledger.StartedAtUtc,
            });
        }

        return new ProjectionOutcome(staged, rejected, rejectedReasons.ToList(), stillUnsupported.ToList());
    }

    /// <summary>
    /// Brings the exchange's counts in line with what it now holds. Leaving
    /// "unsupported: Condition, Observation" on an exchange whose Conditions and
    /// Observations CHO is now serving would make the record untrue; the
    /// backfill marker keeps "these arrived later, by backfill" answerable.
    /// </summary>
    private static void ApplyCounts(PayerToPayerImportLedgerEntry ledger, ProjectionOutcome outcome)
    {
        ledger.Counts.Clinical = outcome.Resources.Count;
        ledger.Counts.Unsupported = outcome.StillUnsupported.Count;
        ledger.Counts.UnsupportedTypes = outcome.StillUnsupported;
        ledger.Counts.Rejected = outcome.Rejected;
        ledger.Counts.RejectedReasons = outcome.RejectedReasons;
        ledger.ClinicalBackfilledAtUtc = DateTime.UtcNow;
    }

    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// Runs the clinical backfill ONCE at startup when it is enabled. It is a
/// one-shot convergence, not a recurring sweep: a second run over an
/// already-backfilled store writes the same rows to the same keys and changes
/// nothing, so there is nothing for a schedule to keep doing.
///
/// A failure is logged and swallowed. The backfill makes existing data visible;
/// it is not on the path of any request, and taking the FHIR service down
/// because a historical archive could not be re-read would be the worse outcome.
/// </summary>
public sealed class ClinicalBackfillWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ClinicalBackfillOptions _options;
    private readonly ILogger<ClinicalBackfillWorker> _logger;

    public ClinicalBackfillWorker(
        IServiceProvider services,
        IOptions<ClinicalBackfillOptions> options,
        ILogger<ClinicalBackfillWorker> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Clinical backfill is disabled; no historical import will be re-projected.");
            return;
        }

        try
        {
            using var scope = _services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<ClinicalBackfillService>();
            await service.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown. The next start converges the rest.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clinical backfill did not complete; it is safe to re-run.");
        }
    }
}
