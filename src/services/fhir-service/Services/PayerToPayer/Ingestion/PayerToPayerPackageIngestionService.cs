using System.Text.Json;
using FhirService.Models.PayerToPayer;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging;

namespace FhirService.Services.PayerToPayer.Ingestion;

/// <summary>
/// Durably ingests a validated Payer-to-Payer package into Cloud Health Office
/// (CMS-0057-F P2P-02, ingestion half).
///
/// It is handed an ALREADY VALIDATED package by
/// <c>PayerToPayerOutboundService</c> and never talks to a remote payer itself —
/// the orchestration and the transport stay where PR #1150 put them.
///
/// Order of operations:
///   1. check the ingestion context (tenant, member, exchange, source payer) —
///      all of it from the exchange CHO drove, never from the peer's Bundle;
///   2. archive the validated package verbatim, so nothing CHO cannot project is
///      lost;
///   3. classify each resource (member history / administrative reference /
///      unsupported);
///   4. normalize intra-package references to CHO's imported identities;
///   5. stage every resource under a deterministic import key;
///   6. commit the exchange's ledger entry — the single write that publishes the
///      import.
/// A failure at any step leaves the member's imported history untouched: staged
/// rows without a committed ledger entry are invisible, and the retry re-stages
/// the same keys.
/// </summary>
public interface IPayerToPayerPackageIngestionService
{
    Task<PayerToPayerIngestionResult> IngestAsync(
        PayerToPayerIngestionContext context, PayerToPayerReceivedPackage package, CancellationToken ct = default);
}

public sealed class PayerToPayerPackageIngestionService : IPayerToPayerPackageIngestionService
{
    private static readonly FhirJsonSerializer Serializer = new(new SerializerSettings { Pretty = false });

    private readonly IPayerToPayerImportRepository _repository;
    private readonly ILogger<PayerToPayerPackageIngestionService> _logger;

    public PayerToPayerPackageIngestionService(
        IPayerToPayerImportRepository repository, ILogger<PayerToPayerPackageIngestionService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<PayerToPayerIngestionResult> IngestAsync(
        PayerToPayerIngestionContext context, PayerToPayerReceivedPackage package, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;

        // 1. Context. Fail closed: without a full tenant/member/exchange/payer
        //    binding there is no safe place to put this data.
        if (!context.IsComplete)
            return PayerToPayerIngestionResult.Failed(PayerToPayerIngestionFailure.InvalidContext, startedAt);

        var resources = package.Bundle.Entry?
            .Select(e => e.Resource).OfType<Resource>()
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .ToList() ?? [];
        if (resources.Count == 0)
            return PayerToPayerIngestionResult.Failed(PayerToPayerIngestionFailure.EmptyPackage, startedAt);

        // An exchange already committed is a replay: report what is held rather
        // than re-staging it.
        var existing = await _repository.GetLedgerAsync(context.TenantId, context.ExchangeId, ct);
        if (existing is { Status: PayerToPayerIngestionStatus.Completed })
        {
            return new PayerToPayerIngestionResult
            {
                Status = PayerToPayerIngestionStatus.Completed,
                Failure = PayerToPayerIngestionFailure.None,
                Counts = existing.Counts,
                StartedAtUtc = existing.StartedAtUtc,
                CompletedAtUtc = existing.CompletedAtUtc,
                IsReplay = true,
            };
        }

        var ledger = await _repository.OpenLedgerAsync(
            existing ?? new PayerToPayerImportLedgerEntry
            {
                ExchangeId = context.ExchangeId,
                TenantId = context.TenantId,
                MemberId = context.MemberId,
                SourcePayerId = context.SourcePayerId,
                StartedAtUtc = startedAt,
            }, ct);

        // 2 + 4. Normalize references BEFORE serializing, so what is archived and
        //        what is stored agree, then archive the package verbatim.
        var normalization = PayerToPayerReferenceNormalizer.Normalize(
            package.Bundle,
            (type, id) => PayerToPayerImportPolicy.ImportKey(
                context.TenantId, context.MemberId, context.SourcePayerId, type, id));

        var counts = new PayerToPayerIngestionCounts
        {
            Received = resources.Count,
            ReferencesNormalized = normalization.Rewritten,
        };

        try
        {
            ledger.ArchivedPackageJson = Serializer.SerializeToString(package.Bundle);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            // The exception can carry resource content, so only the category is
            // recorded — never the message.
            await _repository.FailAsync(ledger, PayerToPayerIngestionFailure.UnreadableResource, ct);
            return PayerToPayerIngestionResult.Failed(
                PayerToPayerIngestionFailure.UnreadableResource, startedAt, counts);
        }

        // 3 + 5. Classify and stage. The persisted/administrative counters stay
        //        LOCAL until the commit lands: a count called "persisted" must
        //        mean stored and visible, never merely classified, or a failed
        //        ingestion would report data it did not keep.
        var staged = new List<ImportedFhirResource>(resources.Count);
        var unsupportedTypes = new SortedSet<string>(StringComparer.Ordinal);
        var memberHistoryStaged = 0;
        var administrativeStaged = 0;

        foreach (var resource in resources)
        {
            var classification = PayerToPayerImportPolicy.Classify(resource.TypeName);
            if (classification == ImportedResourceClass.Unsupported)
            {
                // Named and counted, not silently dropped — and still present in
                // the archived package.
                counts.Unsupported++;
                unsupportedTypes.Add(resource.TypeName);
                continue;
            }

            string json;
            try
            {
                json = Serializer.SerializeToString(resource);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
            {
                await _repository.FailAsync(ledger, PayerToPayerIngestionFailure.UnreadableResource, ct);
                return PayerToPayerIngestionResult.Failed(
                    PayerToPayerIngestionFailure.UnreadableResource, startedAt, counts);
            }

            staged.Add(new ImportedFhirResource
            {
                // Identity, tenancy, and member binding all come from the
                // exchange context. Nothing here is read out of the peer's
                // Bundle, so a remote payer cannot steer an import at another
                // tenant or another member.
                ImportKey = PayerToPayerImportPolicy.ImportKey(
                    context.TenantId, context.MemberId, context.SourcePayerId, resource.TypeName, resource.Id!),
                TenantId = context.TenantId,
                MemberId = context.MemberId,
                SourcePayerId = context.SourcePayerId,
                SourceEndpointKey = context.SourceEndpointKey,
                ExchangeId = context.ExchangeId,
                ResourceType = resource.TypeName,
                SourceResourceId = resource.Id!,
                RemoteMemberId = context.RemoteMemberId,
                Classification = classification,
                ResourceJson = json,
                ContentHash = PayerToPayerImportPolicy.ContentHash(json),
                ReferencesNormalized = normalization.Rewritten > 0,
                ReceivedAtUtc = context.ReceivedAtUtc,
            });

            if (classification == ImportedResourceClass.MemberHistory) memberHistoryStaged++;
            else administrativeStaged++;
        }

        counts.UnsupportedTypes = unsupportedTypes.ToList();

        StageOutcome outcome;
        try
        {
            outcome = await _repository.StageAsync(staged, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Staged rows stay invisible without a committed ledger entry, so the
            // member is never left with half a package.
            await _repository.FailAsync(ledger, PayerToPayerIngestionFailure.StagingFailed, ct);
            return PayerToPayerIngestionResult.Failed(
                PayerToPayerIngestionFailure.StagingFailed, startedAt, counts);
        }

        counts.Duplicate = outcome.UnchangedDuplicates;
        ledger.Counts = counts;


        // 6. Commit — one write, and only now is the import the member's history,
        //    so only now are the staged resources counted as persisted.
        counts.Persisted = memberHistoryStaged;
        counts.AdministrativeReference = administrativeStaged;
        ledger.Counts = counts;

        try
        {
            await _repository.CommitAsync(ledger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The staged rows stay invisible, so nothing is persisted — the
            // reported counts must say so.
            counts.Persisted = 0;
            counts.AdministrativeReference = 0;
            await _repository.FailAsync(ledger, PayerToPayerIngestionFailure.CommitFailed, ct);
            return PayerToPayerIngestionResult.Failed(
                PayerToPayerIngestionFailure.CommitFailed, startedAt, counts);
        }

        // Structured, PHI-free: ids and counts only — no payload, no demographics,
        // no source URL.
        _logger.LogInformation(
            "P2P import committed: exchange={Exchange} sourcePayer={Payer} received={Received} "
            + "persisted={Persisted} administrative={Administrative} duplicate={Duplicate} unsupported={Unsupported}",
            Clean(context.ExchangeId), Clean(context.SourcePayerId),
            counts.Received, counts.Persisted, counts.AdministrativeReference, counts.Duplicate, counts.Unsupported);

        return new PayerToPayerIngestionResult
        {
            Status = PayerToPayerIngestionStatus.Completed,
            Failure = PayerToPayerIngestionFailure.None,
            Counts = counts,
            StartedAtUtc = startedAt,
            CompletedAtUtc = ledger.CompletedAtUtc,
        };
    }

    /// <summary>Strips CR/LF so a config- or peer-derived id cannot forge a log entry (CWE-117).</summary>
    private static string Clean(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}
