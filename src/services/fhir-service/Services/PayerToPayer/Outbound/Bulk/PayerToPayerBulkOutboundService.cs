using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudHealthOffice.Infrastructure.Logging;
using FhirService.Models;
using FhirService.Models.PayerToPayer;
using FhirService.Services.PayerToPayer.Ingestion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirService.Services.PayerToPayer.Outbound.Bulk;

/// <summary>A request to fetch many members' data from one prior payer.</summary>
public sealed record BulkOutboundStartRequest(
    string TenantId, string TargetPayerId, IReadOnlyList<string> MemberIds, string? AsOfDate, string? InitiatedBy);

/// <summary>Why a bulk request was refused before anything was recorded.</summary>
public enum BulkOutboundRefusal
{
    None,
    TenantMismatch,
    NoMembers,
    TooManyMembers,
    TargetPayerNotConfigured,
}

public sealed record BulkOutboundStartResult(PayerToPayerBulkOutboundJob? Job, BulkOutboundRefusal Refusal);

/// <summary>
/// Outbound bulk Payer-to-Payer (G-35, CHO as the new payer): the PDex 2.1
/// bulk flow against a prior payer for many transitioning members at once.
/// </summary>
public interface IPayerToPayerBulkOutboundService
{
    /// <summary>Records the job. Nothing is sent until the worker advances it.</summary>
    Task<BulkOutboundStartResult> StartAsync(BulkOutboundStartRequest request, CancellationToken ct = default);

    Task<PayerToPayerBulkOutboundJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default);

    /// <summary>Takes the job as far as it can go now, and saves it.</summary>
    Task AdvanceAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct = default);
}

/// <summary>
/// Drives one bulk job through the PDex flow:
/// <list type="number">
///   <item>each member is checked as the single exchange checks one (a member
///   of the tenant, one coverage with the payer, an active Payer-to-Payer
///   opt-in); a member who fails a check is sent nowhere;</item>
///   <item>the rest go in one <c>Group/$bulk-member-match</c>, each with its
///   PDex consent and an opaque correlation identifier the payer echoes
///   back;</item>
///   <item>the matched Group is exported with
///   <c>Group/{id}/$davinci-data-export</c>, after the members' opt-ins are
///   checked again;</item>
///   <item>the export's files are sorted by member: a resource naming one
///   matched member is theirs, a resource naming several is kept by none, and
///   resources naming no member (an Organization, a Practitioner) go with the
///   members whose resources refer to them;</item>
///   <item>each member's resources are validated and ingested exactly as a
///   single exchange's package is, under their own import ledger entry.</item>
/// </list>
/// The job is advanced in steps by <see cref="PayerToPayerBulkOutboundWorker"/>,
/// polling at the payer's <c>Retry-After</c>. A peer that cannot be reached is
/// tried again until <see cref="PayerToPayerBulkOutboundOptions.JobTimeout"/>.
/// </summary>
public sealed class PayerToPayerBulkOutboundService : IPayerToPayerBulkOutboundService
{
    public const string CorrelationSystem = "https://cloudhealthoffice.com/fhir/NamingSystem/p2p-bulk-correlation";

    private const string MemberIdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    private const string MatchedMembers = "MatchedMembers";
    private const string NonMatchedMembers = "NonMatchedMembers";
    private const string ConsentConstrainedMembers = "ConsentConstrainedMembers";
    private const string MatchParametersExtension =
        "http://hl7.org/fhir/us/davinci-pdex/StructureDefinition/base-ext-match-parameters";

    private readonly IPayerToPayerMemberSource _memberSource;
    private readonly IPayerToPayerMemberMatchSource _coverageSource;
    private readonly IPayerToPayerConsentGate _consentGate;
    private readonly IPayerToPayerEndpointResolver _endpoints;
    private readonly IPayerToPayerBulkRemoteClient _remote;
    private readonly IPayerToPayerBulkOutboundJobStore _store;
    private readonly IPayerToPayerPackageIngestionService _ingestion;
    private readonly PayerToPayerDirectoryOptions _directory;
    private readonly PayerToPayerBulkOutboundOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<PayerToPayerBulkOutboundService> _logger;

    public PayerToPayerBulkOutboundService(
        IPayerToPayerMemberSource memberSource,
        IPayerToPayerMemberMatchSource coverageSource,
        IPayerToPayerConsentGate consentGate,
        IPayerToPayerEndpointResolver endpoints,
        IPayerToPayerBulkRemoteClient remote,
        IPayerToPayerBulkOutboundJobStore store,
        IPayerToPayerPackageIngestionService ingestion,
        IOptions<PayerToPayerDirectoryOptions> directory,
        IOptions<PayerToPayerBulkOutboundOptions> options,
        TimeProvider time,
        ILogger<PayerToPayerBulkOutboundService> logger)
    {
        _memberSource = memberSource;
        _coverageSource = coverageSource;
        _consentGate = consentGate;
        _endpoints = endpoints;
        _remote = remote;
        _store = store;
        _ingestion = ingestion;
        _directory = directory.Value;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<BulkOutboundStartResult> StartAsync(BulkOutboundStartRequest request, CancellationToken ct = default)
    {
        var tenantId = request.TenantId?.Trim() ?? string.Empty;
        if (!string.Equals(tenantId, _memberSource.ServedTenantId, StringComparison.Ordinal))
            return new(null, BulkOutboundRefusal.TenantMismatch);

        var memberIds = (request.MemberIds ?? [])
            .Select(m => m?.Trim())
            .Where(m => !string.IsNullOrEmpty(m))
            .Select(m => m!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (memberIds.Count == 0) return new(null, BulkOutboundRefusal.NoMembers);
        if (memberIds.Count > _options.MaxMembersPerRequest) return new(null, BulkOutboundRefusal.TooManyMembers);

        var targetPayerId = request.TargetPayerId?.Trim() ?? string.Empty;
        var endpoint = _endpoints.Resolve(tenantId, targetPayerId);
        if (endpoint?.FhirBaseUri is null) return new(null, BulkOutboundRefusal.TargetPayerNotConfigured);

        var now = _time.GetUtcNow();
        var job = new PayerToPayerBulkOutboundJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            TargetPayerId = targetPayerId,
            TargetEndpointKey = endpoint.EndpointKey,
            InitiatedBy = request.InitiatedBy,
            AsOfDate = MemberIdentityNormalizer.BirthDate(request.AsOfDate),
            Members = memberIds.Select(m => new PayerToPayerBulkOutboundMember { MemberId = m }).ToList(),
            CreatedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now,
            ExpiresAt = now + _options.JobTimeout + _options.JobRetention,
        };
        await _store.SaveAsync(job, ct);

        _logger.LogInformation(
            "P2P outbound bulk {JobId} recorded: tenant={Tenant} targetPayer={Payer} members={Count}",
            job.JobId, LogSanitizer.Sanitize(tenantId), LogSanitizer.Sanitize(targetPayerId), memberIds.Count);
        return new(job, BulkOutboundRefusal.None);
    }

    public Task<PayerToPayerBulkOutboundJob?> GetAsync(string tenantId, string jobId, CancellationToken ct = default)
        => _store.GetAsync(tenantId, jobId, _time.GetUtcNow(), ct);

    public async Task AdvanceAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct = default)
    {
        // A step that leaves the job due at once (a match answered in the same
        // response, say) is followed by the next; a step that waits on the peer
        // ends the turn.
        for (var step = 0; step < 4 && !job.IsTerminal && job.NextAttemptAt <= _time.GetUtcNow(); step++)
        {
            var endpoint = _endpoints.Resolve(job.TenantId, job.TargetPayerId);
            if (endpoint?.FhirBaseUri is null)
            {
                Fail(job, PayerToPayerBulkOutboundFailure.TargetPayerNotConfigured);
                break;
            }
            if (_time.GetUtcNow() - job.CreatedAt > _options.JobTimeout)
            {
                Fail(job, PayerToPayerBulkOutboundFailure.TimedOut);
                break;
            }

            switch (job.Status)
            {
                case PayerToPayerBulkOutboundStatus.Pending:
                    await SubmitMatchAsync(job, endpoint, ct);
                    break;
                case PayerToPayerBulkOutboundStatus.Matching:
                    await PollMatchAsync(job, endpoint, ct);
                    break;
                case PayerToPayerBulkOutboundStatus.Matched:
                    await KickOffExportAsync(job, endpoint, ct);
                    break;
                case PayerToPayerBulkOutboundStatus.Exporting:
                    await PollExportAsync(job, endpoint, ct);
                    break;
            }
        }

        job.LeasedUntil = null;
        job.UpdatedAt = _time.GetUtcNow();
        await _store.SaveAsync(job, ct);
    }

    // ── 1–2. Local checks, then $bulk-member-match ─────────────────────────────

    private async Task SubmitMatchAsync(PayerToPayerBulkOutboundJob job, PayerToPayerEndpoint endpoint, CancellationToken ct)
    {
        var bundles = new JsonArray();
        for (var i = 0; i < job.Members.Count; i++)
        {
            var member = job.Members[i];
            if (member.Outcome != PayerToPayerBulkMemberOutcome.Pending) continue;

            var candidates = await _memberSource.FindCandidatesAsync(
                job.TenantId, new PayerToPayerMemberCriteria { MemberId = member.MemberId }, ct);
            if (candidates.Count != 1)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.MemberNotFound;
                continue;
            }

            var coverage = await TargetCoverageAsync(job, member.MemberId, endpoint.PayerId, ct);
            if (coverage.Outcome == CoverageSelectionOutcome.Ambiguous)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.LocalCoverageAmbiguous;
                continue;
            }

            // Nothing about a member leaves CHO without their opt-in, decided here
            // and now, at the moment of disclosure.
            var decision = await _consentGate.EvaluateAsync(job.TenantId, member.MemberId, null, ct);
            if (!decision.Allowed)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.NotAuthorized;
                continue;
            }

            member.AuthorizingConsentId = decision.ConsentId;
            member.CorrelationId = Guid.NewGuid().ToString("N");
            member.Outcome = PayerToPayerBulkMemberOutcome.Submitted;
            bundles.Add(MemberBundle(i, candidates[0], coverage.Coverage, endpoint, member.CorrelationId,
                decision.EvaluatedAtUtc, job.AsOfDate));
        }

        if (bundles.Count == 0)
        {
            Complete(job);
            return;
        }

        var parameters = new JsonObject { ["resourceType"] = "Parameters", ["parameter"] = bundles };
        var response = await _remote.SubmitMatchAsync(endpoint, parameters, ct);
        switch (response.State)
        {
            case BulkRemoteState.Accepted:
                job.Status = PayerToPayerBulkOutboundStatus.Matching;
                job.StatusUrl = response.StatusUrl!.AbsoluteUri;
                Later(job, _options.MinRetryAfter);
                break;
            case BulkRemoteState.Completed:
                ApplyMatchAnswer(job, response.Payload);
                break;
            default:
                // Sent again later from the start, so each member's opt-in is
                // asked again at that disclosure too.
                foreach (var member in job.Members.Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Submitted))
                {
                    member.Outcome = PayerToPayerBulkMemberOutcome.Pending;
                    member.CorrelationId = null;
                    member.AuthorizingConsentId = null;
                }
                OnRemoteFailure(job, response);
                break;
        }
    }

    private async Task PollMatchAsync(PayerToPayerBulkOutboundJob job, PayerToPayerEndpoint endpoint, CancellationToken ct)
    {
        var response = await _remote.PollAsync(endpoint, new Uri(job.StatusUrl!), ct);
        switch (response.State)
        {
            case BulkRemoteState.Completed:
                ApplyMatchAnswer(job, response.Payload);
                break;
            case BulkRemoteState.InProgress:
                Later(job, response.RetryAfter);
                break;
            default:
                OnRemoteFailure(job, response);
                break;
        }
    }

    /// <summary>
    /// Reads the payer's Groups. A member is placed by the correlation
    /// identifier on the submitted Patient the payer contained and pointed at,
    /// never by the payer's own ids; a member the answer does not place was not
    /// matched.
    /// </summary>
    private void ApplyMatchAnswer(PayerToPayerBulkOutboundJob job, string? payload)
    {
        var parameters = Parse(payload);
        if (parameters is null || Str(parameters["resourceType"]) != "Parameters")
        {
            Fail(job, PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
            return;
        }

        var submitted = job.Members
            .Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Submitted && m.CorrelationId is not null)
            .ToDictionary(m => m.CorrelationId!, StringComparer.Ordinal);

        string? matchedGroupId = null;
        foreach (var parameter in (parameters["parameter"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(parameter["name"]);
            if (parameter["resource"] is not JsonObject group || Str(group["resourceType"]) != "Group") continue;

            var outcome = name switch
            {
                MatchedMembers => PayerToPayerBulkMemberOutcome.Matched,
                NonMatchedMembers => PayerToPayerBulkMemberOutcome.NoMatch,
                ConsentConstrainedMembers => PayerToPayerBulkMemberOutcome.ConsentConstrained,
                _ => (PayerToPayerBulkMemberOutcome?)null,
            };
            if (outcome is null) continue;
            if (outcome == PayerToPayerBulkMemberOutcome.Matched) matchedGroupId = Str(group["id"]);

            foreach (var (correlation, remoteId) in GroupMembers(group))
            {
                if (!submitted.TryGetValue(correlation, out var member)) continue;
                if (outcome == PayerToPayerBulkMemberOutcome.Matched && remoteId is null) continue;
                member.Outcome = outcome.Value;
                member.RemoteMemberId = outcome == PayerToPayerBulkMemberOutcome.Matched ? remoteId : null;
            }
        }

        // PDex answers MatchedMembers always, even empty.
        if (matchedGroupId is null)
        {
            Fail(job, PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
            return;
        }

        foreach (var member in submitted.Values.Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Submitted))
            member.Outcome = PayerToPayerBulkMemberOutcome.NoMatch;

        // Two of our members answered as one of the payer's patients: whose
        // data would it be? Neither's.
        foreach (var shared in job.Members
                     .Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Matched)
                     .GroupBy(m => m.RemoteMemberId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            foreach (var member in shared)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.Ambiguous;
                member.RemoteMemberId = null;
            }
        }

        job.StatusUrl = null;
        if (!job.Members.Any(m => m.Outcome == PayerToPayerBulkMemberOutcome.Matched))
        {
            Complete(job);
            return;
        }

        job.MatchedGroupId = matchedGroupId;
        job.Status = PayerToPayerBulkOutboundStatus.Matched;
        job.NextAttemptAt = _time.GetUtcNow();
    }

    /// <summary>(correlation id, the payer's Patient id when it gave one) for each entry of a Group.</summary>
    private static IEnumerable<(string Correlation, string? RemoteId)> GroupMembers(JsonObject group)
    {
        var contained = (group["contained"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => Str(r["resourceType"]) == "Patient" && Str(r["id"]) is not null)
            .GroupBy(r => Str(r["id"])!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var entry in (group["member"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (entry["entity"] is not JsonObject entity) continue;

            var submittedRef = (entity["extension"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(e => Str(e["url"]) == MatchParametersExtension)
                .Select(e => Str(e["valueReference"]?["reference"]))
                .FirstOrDefault(r => r is not null && r.StartsWith('#'));
            if (submittedRef is null || !contained.TryGetValue(submittedRef[1..], out var patient)) continue;

            var correlation = (patient["identifier"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(i => Str(i["system"]) == CorrelationSystem)
                .Select(i => Str(i["value"]))
                .FirstOrDefault(v => v is not null);
            if (correlation is null) continue;

            yield return (correlation, PatientId(Str(entity["reference"])));
        }
    }

    // ── 3. $davinci-data-export on the matched Group ───────────────────────────

    private async Task KickOffExportAsync(PayerToPayerBulkOutboundJob job, PayerToPayerEndpoint endpoint, CancellationToken ct)
    {
        // The match took time; a member who opted out since is not exported for.
        // (The payer's Group still names them, so whatever comes back for them
        // is dropped unread.)
        await RecheckConsentAsync(job, ct);
        if (!job.Members.Any(m => m.Outcome == PayerToPayerBulkMemberOutcome.Matched))
        {
            Complete(job);
            return;
        }

        var response = await _remote.KickOffExportAsync(endpoint, job.MatchedGroupId!, ct);
        switch (response.State)
        {
            case BulkRemoteState.Accepted:
                job.Status = PayerToPayerBulkOutboundStatus.Exporting;
                job.StatusUrl = response.StatusUrl!.AbsoluteUri;
                Later(job, _options.MinRetryAfter);
                break;
            case BulkRemoteState.Completed:
                // A bulk export is asynchronous; an immediate answer is not a manifest CHO asked for.
                Fail(job, PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
                break;
            default:
                OnRemoteFailure(job, response);
                break;
        }
    }

    private async Task PollExportAsync(PayerToPayerBulkOutboundJob job, PayerToPayerEndpoint endpoint, CancellationToken ct)
    {
        var statusUrl = new Uri(job.StatusUrl!);
        var response = await _remote.PollAsync(endpoint, statusUrl, ct);
        if (response.State == BulkRemoteState.InProgress)
        {
            Later(job, response.RetryAfter);
            return;
        }
        if (response.State != BulkRemoteState.Completed)
        {
            OnRemoteFailure(job, response);
            return;
        }

        var manifest = Parse(response.Payload);
        if (manifest?["output"] is not JsonArray output)
        {
            Fail(job, PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
            return;
        }
        if (output.Count > _options.MaxFiles)
        {
            Fail(job, PayerToPayerBulkOutboundFailure.ExportTooLarge);
            return;
        }
        var requiresAccessToken = manifest["requiresAccessToken"] is not JsonValue flag
                                  || !flag.TryGetValue<bool>(out var required) || required;

        // ── 4. The files, sorted by member ──
        var files = new List<string>(output.Count);
        long total = 0;
        foreach (var file in output.OfType<JsonObject>())
        {
            if (!Uri.TryCreate(Str(file["url"]), UriKind.Absolute, out var url))
            {
                Fail(job, PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
                return;
            }

            var download = await _remote.DownloadAsync(endpoint, url, requiresAccessToken, ct);
            if (download.State != BulkRemoteState.Completed)
            {
                // The manifest stays at its status URL until CHO deletes it, so
                // a file that could not be fetched now is fetched again later.
                OnRemoteFailure(job, download);
                return;
            }

            total += download.Payload!.Length;
            if (total > _options.MaxTotalBytes)
            {
                Fail(job, PayerToPayerBulkOutboundFailure.ExportTooLarge);
                return;
            }
            files.Add(download.Payload);
        }
        job.ExportFileCount = files.Count;

        await IngestAsync(job, endpoint, Sort(job, files), ct);

        // Done with the export: let the payer drop its files.
        await _remote.CompleteAsync(endpoint, statusUrl, ct);
        job.StatusUrl = null;
        Complete(job);
    }

    private sealed record SortedExport(
        Dictionary<string, JsonObject> Patients,
        Dictionary<string, List<JsonObject>> ByMember,
        Dictionary<string, JsonObject> Supporting);

    private static SortedExport Sort(PayerToPayerBulkOutboundJob job, List<string> files)
    {
        var matched = job.Members
            .Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Matched)
            .Select(m => m.RemoteMemberId!)
            .ToHashSet(StringComparer.Ordinal);

        var sorted = new SortedExport(
            new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        foreach (var file in files)
        {
            foreach (var line in file.Split('\n'))
            {
                var resource = Parse(line);
                var type = Str(resource?["resourceType"]);
                var id = Str(resource?["id"]);
                if (resource is null || type is null || id is null) continue;

                if (type == "Patient")
                {
                    if (matched.Contains(id)) sorted.Patients[id] = resource;
                    continue;
                }

                var patients = PatientReferences(resource).ToHashSet(StringComparer.Ordinal);
                if (patients.Count == 0)
                {
                    sorted.Supporting[$"{type}/{id}"] = resource;
                }
                else if (patients.Count == 1)
                {
                    var owner = patients.First();
                    if (!matched.Contains(owner)) continue;
                    if (!sorted.ByMember.TryGetValue(owner, out var list)) sorted.ByMember[owner] = list = [];
                    list.Add(resource);
                }
                else if (patients.Overlaps(matched))
                {
                    job.CrossMemberResourceCount++;
                }
            }
        }
        return sorted;
    }

    // ── 5. Each member's package, validated and ingested ───────────────────────

    private async Task IngestAsync(
        PayerToPayerBulkOutboundJob job, PayerToPayerEndpoint endpoint, SortedExport export, CancellationToken ct)
    {
        await RecheckConsentAsync(job, ct);

        for (var i = 0; i < job.Members.Count; i++)
        {
            var member = job.Members[i];
            if (member.Outcome != PayerToPayerBulkMemberOutcome.Matched) continue;
            var remoteId = member.RemoteMemberId!;

            var resources = new List<JsonObject>();
            if (export.Patients.TryGetValue(remoteId, out var patient)) resources.Add(patient);
            if (export.ByMember.TryGetValue(remoteId, out var own)) resources.AddRange(own);
            if (resources.Count == 0)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.NoData;
                continue;
            }
            resources.AddRange(SupportingFor(resources, export.Supporting));

            var bundle = new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "collection",
                ["entry"] = new JsonArray(resources.Select(r => (JsonNode)new JsonObject { ["resource"] = r.DeepClone() }).ToArray()),
            };
            var validation = PayerToPayerResponseReader.ValidatePackage(bundle.ToJsonString(), remoteId);
            if (!validation.IsValid)
            {
                member.Outcome = PayerToPayerBulkMemberOutcome.Rejected;
                continue;
            }

            var exchangeId = $"{job.JobId}-{(i + 1).ToString(CultureInfo.InvariantCulture)}";
            var receivedAt = _time.GetUtcNow().UtcDateTime;
            var provenance = new PayerToPayerSourceProvenance
            {
                SourcePayerId = endpoint.PayerId,
                SourceEndpointKey = endpoint.EndpointKey,
                ExchangeId = exchangeId,
                ReceivedAtUtc = receivedAt,
            };
            var ingestion = await _ingestion.IngestAsync(new PayerToPayerIngestionContext
            {
                // Bound to the job CHO drove, never to anything in the payer's files.
                TenantId = job.TenantId,
                MemberId = member.MemberId,
                SourcePayerId = endpoint.PayerId,
                SourceEndpointKey = endpoint.EndpointKey,
                ExchangeId = exchangeId,
                RemoteMemberId = remoteId,
                ReceivedAtUtc = receivedAt,
            }, new PayerToPayerReceivedPackage
            {
                Bundle = PayerToPayerResponseReader.StampProvenance(validation.Bundle!, provenance),
                RemoteMemberId = remoteId,
                ResourceCount = validation.ResourceCount,
                Provenance = provenance,
            }, ct);

            member.ExchangeId = exchangeId;
            member.ReceivedResourceCount = validation.ResourceCount;
            member.PersistedResourceCount = ingestion.Counts.Persisted;
            member.Outcome = ingestion.Succeeded
                ? PayerToPayerBulkMemberOutcome.Completed
                : PayerToPayerBulkMemberOutcome.IngestionFailed;
        }
    }

    /// <summary>The supporting resources a member's resources refer to, and those they refer to in turn.</summary>
    private static List<JsonObject> SupportingFor(List<JsonObject> resources, Dictionary<string, JsonObject> supporting)
    {
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<JsonObject>();
        var frontier = resources;
        while (frontier.Count > 0)
        {
            var next = new List<JsonObject>();
            foreach (var key in frontier.SelectMany(References))
            {
                if (supporting.TryGetValue(key, out var resource) && taken.Add(key)) next.Add(resource);
            }
            added.AddRange(next);
            frontier = next;
        }
        return added;
    }

    private async Task RecheckConsentAsync(PayerToPayerBulkOutboundJob job, CancellationToken ct)
    {
        foreach (var member in job.Members.Where(m => m.Outcome == PayerToPayerBulkMemberOutcome.Matched))
        {
            var decision = await _consentGate.EvaluateAsync(job.TenantId, member.MemberId, null, ct);
            if (!decision.Allowed) member.Outcome = PayerToPayerBulkMemberOutcome.ConsentWithdrawn;
        }
    }

    // ── The PDex request ───────────────────────────────────────────────────────

    private async Task<CoverageSelection> TargetCoverageAsync(
        PayerToPayerBulkOutboundJob job, string memberId, string targetPayerId, CancellationToken ct)
    {
        // The same choice the single exchange makes: the member's coverage with
        // the payer, narrowed by date when there are several.
        var normalizedTarget = MemberIdentityNormalizer.Identifier(targetPayerId);
        var withTargetPayer = (await _coverageSource.GetCoveragesAsync(job.TenantId, memberId, ct))
            .Where(c => string.Equals(MemberIdentityNormalizer.Identifier(c.PayerId), normalizedTarget, StringComparison.Ordinal))
            .ToList();

        if (withTargetPayer.Count == 0) return CoverageSelection.NoCoverage;
        if (withTargetPayer.Count == 1) return CoverageSelection.Select(withTargetPayer[0]);
        return PayerToPayerCoverageSelector.Select(withTargetPayer, new MemberMatchCriteria
        {
            AsOfDate = job.AsOfDate ?? job.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// One PDex <c>MemberBundle</c>: the member as an HRex Patient (their member
    /// number with the payer when CHO holds it, name, birth date, gender), the
    /// coverage to match, and the member's PDex consent naming CHO as recipient.
    /// No SSN, address, phone or email is sent.
    /// </summary>
    private JsonObject MemberBundle(
        int index, ChoMember member, ChoCoverage? coverage, PayerToPayerEndpoint endpoint, string correlationId,
        DateTime consentStartUtc, string? asOfDate)
    {
        var number = (index + 1).ToString(CultureInfo.InvariantCulture);
        var patientId = $"member-{number}";
        var subscriberId = string.IsNullOrWhiteSpace(coverage?.SubscriberId) ? null : coverage.SubscriberId.Trim();

        var identifiers = new JsonArray();
        if (subscriberId is not null)
        {
            identifiers.Add(new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject { ["system"] = MemberIdentifierTypeSystem, ["code"] = "MB" }),
                },
                ["value"] = subscriberId,
            });
        }
        identifiers.Add(new JsonObject { ["system"] = CorrelationSystem, ["value"] = correlationId });

        var name = new JsonObject { ["use"] = "official", ["family"] = member.LastName };
        if (!string.IsNullOrWhiteSpace(member.FirstName)) name["given"] = new JsonArray(member.FirstName);

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = patientId,
            ["identifier"] = identifiers,
            ["name"] = new JsonArray(name),
            ["birthDate"] = member.Dob,
        };
        if (Gender(member.Gender) is { } gender) patient["gender"] = gender;

        var coverageToMatch = new JsonObject
        {
            ["resourceType"] = "Coverage",
            ["id"] = $"coverage-{number}",
            ["status"] = coverage?.Status is "active" or "cancelled" or "draft" or "entered-in-error" ? coverage.Status : "active",
            ["beneficiary"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
            ["payor"] = new JsonArray(new JsonObject { ["identifier"] = new JsonObject { ["value"] = endpoint.PayerId } }),
        };
        if (subscriberId is not null) coverageToMatch["subscriberId"] = subscriberId;
        var period = new JsonObject();
        if (coverage?.PeriodStart is { } start) period["start"] = start;
        if (coverage?.PeriodEnd is { } end) period["end"] = end;
        else if (coverage is null && asOfDate is not null) period["end"] = asOfDate;
        if (period.Count > 0) coverageToMatch["period"] = period;

        var consent = PdexConsentBuilder.Build(
            subscriberId, _directory.LocalPayerId, endpoint.PayerId, consentStartUtc, endUtc: null,
            family: member.LastName, birthDate: member.Dob);

        return new JsonObject
        {
            ["name"] = "MemberBundle",
            ["part"] = new JsonArray(
                new JsonObject { ["name"] = "MemberPatient", ["resource"] = patient },
                new JsonObject { ["name"] = "CoverageToMatch", ["resource"] = coverageToMatch },
                new JsonObject { ["name"] = "Consent", ["resource"] = consent }),
        };
    }

    private static string? Gender(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        "m" or "male" => "male",
        "f" or "female" => "female",
        "o" or "other" => "other",
        "u" or "unknown" => "unknown",
        _ => null,
    };

    // ── Job state ──────────────────────────────────────────────────────────────

    private void OnRemoteFailure(PayerToPayerBulkOutboundJob job, BulkRemoteResponse response)
    {
        if (response.Outcome == RemoteCallOutcome.Unavailable || response.State == BulkRemoteState.InProgress)
        {
            Later(job, response.RetryAfter);
            return;
        }
        Fail(job, response.Outcome == RemoteCallOutcome.Unauthorized
            ? PayerToPayerBulkOutboundFailure.RemoteUnauthorized
            : PayerToPayerBulkOutboundFailure.InvalidRemoteResponse);
    }

    private void Later(PayerToPayerBulkOutboundJob job, TimeSpan? retryAfter)
    {
        var wait = retryAfter ?? _options.DefaultRetryAfter;
        if (wait < _options.MinRetryAfter) wait = _options.MinRetryAfter;
        if (wait > _options.MaxRetryAfter) wait = _options.MaxRetryAfter;
        job.NextAttemptAt = _time.GetUtcNow() + wait;
    }

    private void Complete(PayerToPayerBulkOutboundJob job)
    {
        var now = _time.GetUtcNow();
        job.Status = PayerToPayerBulkOutboundStatus.Completed;
        job.CompletedAt = now;
        job.ExpiresAt = now + _options.JobRetention;

        _logger.LogInformation(
            "P2P outbound bulk {JobId} completed: peer={EndpointKey} members={Members} completed={Completed} "
            + "matched-not-kept={NotKept} files={Files} crossMember={CrossMember}",
            job.JobId, LogSanitizer.Sanitize(job.TargetEndpointKey), job.Members.Count,
            job.Members.Count(m => m.Outcome == PayerToPayerBulkMemberOutcome.Completed),
            job.Members.Count(m => m.Outcome is PayerToPayerBulkMemberOutcome.Rejected
                or PayerToPayerBulkMemberOutcome.IngestionFailed or PayerToPayerBulkMemberOutcome.ConsentWithdrawn),
            job.ExportFileCount, job.CrossMemberResourceCount);
    }

    private void Fail(PayerToPayerBulkOutboundJob job, PayerToPayerBulkOutboundFailure failure)
    {
        var now = _time.GetUtcNow();
        job.Status = PayerToPayerBulkOutboundStatus.Failed;
        job.Failure = failure;
        job.CompletedAt = now;
        job.ExpiresAt = now + _options.JobRetention;

        _logger.LogWarning(
            "P2P outbound bulk {JobId} failed: peer={EndpointKey} failure={Failure}",
            job.JobId, LogSanitizer.Sanitize(job.TargetEndpointKey), failure);
    }

    // ── JSON ───────────────────────────────────────────────────────────────────

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every <c>reference</c> in a resource, as <c>Type/id</c> (relative or absolute, no history).</summary>
    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "reference" && Str(value) is { } reference && TypeAndId(reference) is { } typed)
                        yield return typed;
                    else if (key != "contained")
                        foreach (var nested in References(value)) yield return nested;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var nested in References(item)) yield return nested;
                break;
        }
    }

    private static IEnumerable<string> PatientReferences(JsonObject resource)
        => References(resource).Where(r => r.StartsWith("Patient/", StringComparison.Ordinal)).Select(r => r[8..]);

    private static string? TypeAndId(string reference)
    {
        if (reference.StartsWith('#')) return null;
        var history = reference.IndexOf("/_history/", StringComparison.Ordinal);
        if (history >= 0) reference = reference[..history];
        var segments = reference.TrimEnd('/').Split('/');
        return segments.Length >= 2 && segments[^2].Length > 0 && char.IsUpper(segments[^2][0]) && segments[^1].Length > 0
            ? $"{segments[^2]}/{segments[^1]}"
            : null;
    }

    private static string? PatientId(string? reference)
        => reference is not null && TypeAndId(reference) is { } typed && typed.StartsWith("Patient/", StringComparison.Ordinal)
            ? typed[8..]
            : null;

    private static string? Str(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
