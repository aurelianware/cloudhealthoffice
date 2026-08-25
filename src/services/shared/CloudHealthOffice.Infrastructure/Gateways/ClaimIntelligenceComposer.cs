using System.Diagnostics;
using CloudHealthOffice.Infrastructure.Gateways.Models;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.ReferenceData.Payers;
using CloudHealthOffice.Infrastructure.Responders;
using CloudHealthOffice.Infrastructure.Responders.Models;
using Microsoft.Extensions.Logging;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// On-demand claim intelligence projection. Always rebuilds from transaction
/// stores so duplicate 277CA / 835 / attachment deliveries cannot duplicate
/// timeline events.
/// </summary>
public sealed class ClaimIntelligenceComposer : IClaimIntelligenceComposer
{
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAcknowledgmentStore _acknowledgments;
    private readonly IClaimStatusInquiryStore _statusInquiries;
    private readonly IClaimAttachmentTransmissionStore _outboundAttachments;
    private readonly IInboundClaimAttachmentReceiptStore _inboundAttachments;
    private readonly IRemittanceStore _remittances;
    private readonly ILogger<ClaimIntelligenceComposer> _logger;
    private readonly IPayerReferenceService? _payers;
    private readonly TimeProvider _timeProvider;

    public ClaimIntelligenceComposer(
        IClaimTransmissionStore transmissions,
        IClaimAcknowledgmentStore acknowledgments,
        IClaimStatusInquiryStore statusInquiries,
        IClaimAttachmentTransmissionStore outboundAttachments,
        IInboundClaimAttachmentReceiptStore inboundAttachments,
        IRemittanceStore remittances,
        ILogger<ClaimIntelligenceComposer> logger,
        IPayerReferenceService? payers = null,
        TimeProvider? timeProvider = null)
    {
        _transmissions = transmissions;
        _acknowledgments = acknowledgments;
        _statusInquiries = statusInquiries;
        _outboundAttachments = outboundAttachments;
        _inboundAttachments = inboundAttachments;
        _remittances = remittances;
        _logger = logger;
        _payers = payers;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ClaimIntelligenceView?> ComposeAsync(
        ClaimIntelligenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        if (string.IsNullOrWhiteSpace(request.TenantId) || string.IsNullOrWhiteSpace(request.ClaimId))
        {
            RecordFailed("validation");
            return null;
        }

        try
        {
            var view = await BuildAsync(request.TenantId.Trim(), request.ClaimId.Trim(), cancellationToken)
                .ConfigureAwait(false);
            var latency = Stopwatch.GetElapsedTime(started);
            if (view is null)
            {
                RecordFailed("not_found");
                return null;
            }

            RecordSuccess(view, latency);
            Log(view);
            return view;
        }
        catch (Exception)
        {
            RecordFailed("exception");
            throw;
        }
    }

    private async Task<ClaimIntelligenceView?> BuildAsync(
        string tenantId, string claimId, CancellationToken ct)
    {
        var transmissions = (await _transmissions
            .FindByTenantAndClaimIdAsync(tenantId, claimId, ct)
            .ConfigureAwait(false))
            .OrderBy(t => t.SubmittedAtUtc)
            .ToList();

        var inbound = (await _inboundAttachments
            .ListByClaimIdAsync(tenantId, claimId, ct)
            .ConfigureAwait(false))
            .Where(r => r.Status != InboundClaimAttachmentStatus.Quarantined)
            .OrderBy(r => r.ReceivedAtUtc)
            .ToList();

        var inquiries = (await _statusInquiries
            .ListByTenantAndClaimIdAsync(tenantId, claimId, ct)
            .ConfigureAwait(false))
            .OrderBy(i => i.RequestedAtUtc)
            .ToList();

        if (transmissions.Count == 0 && inbound.Count == 0 && inquiries.Count == 0)
        {
            return null;
        }

        var primary = transmissions.LastOrDefault();
        var acknowledgments = new List<ClaimAcknowledgmentRecord>();
        var outbound = new List<ClaimAttachmentTransmissionRecord>();
        var remittances = new List<RemittanceReceipt>();

        foreach (var transmission in transmissions)
        {
            acknowledgments.AddRange(await _acknowledgments
                .ListByTransmissionIdAsync(transmission.TransmissionId, ct)
                .ConfigureAwait(false));
            outbound.AddRange(await _outboundAttachments
                .ListByClaimTransmissionIdAsync(transmission.TransmissionId, ct)
                .ConfigureAwait(false));
            remittances.AddRange(await _remittances
                .ListByTransmissionIdAsync(transmission.TransmissionId, ct)
                .ConfigureAwait(false));
        }

        acknowledgments = DistinctBy(acknowledgments, a => a.AcknowledgmentId);
        outbound = DistinctBy(outbound, a => a.AttachmentTransmissionId);
        remittances = DistinctBy(remittances, r => r.ReceiptId);
        inquiries = DistinctBy(inquiries, i => i.InquiryId);
        inbound = DistinctBy(inbound, r => r.ReceiptId);

        var latestAck = acknowledgments
            .OrderByDescending(a => a.ReceivedAtUtc)
            .FirstOrDefault();
        var latestStatus = inquiries
            .OrderByDescending(i => i.CompletedAtUtc ?? i.RequestedAtUtc)
            .FirstOrDefault();
        var matchedRemittance = SelectRemittance(remittances, claimId, primary?.TransmissionId);
        var remittedClaim = SelectRemittedClaim(matchedRemittance, claimId, primary?.TransmissionId);
        var attachments = MapAttachments(outbound, inbound);
        var lifecycle = ClaimIntelligenceMapper.MapLifecycle(
            primary, latestAck, latestStatus, matchedRemittance, remittedClaim);
        var missing = ClaimIntelligenceMapper.MissingLinks(
            primary, latestAck, latestStatus, matchedRemittance);
        var next = ClaimIntelligenceMapper.MapNextAction(
            lifecycle, latestStatus, attachments, matchedRemittance?.Status);
        var payer = await ResolvePayerAsync(primary?.PayerId, ct).ConfigureAwait(false);
        var source = primary?.InquirySource;
        var financial = MapFinancial(primary, remittedClaim, matchedRemittance);
        var timeline = BuildTimeline(
            transmissions, acknowledgments, inquiries, outbound, inbound, remittances, claimId);

        foreach (var link in missing)
        {
            ChoMetrics.ClaimIntelligenceMissingLinks.Add(1,
                new KeyValuePair<string, object?>("cho.missing_link", link));
        }

        return new ClaimIntelligenceView
        {
            ClaimId = claimId,
            TenantId = tenantId,
            Identifiers = new ClaimIntelligenceIdentifiers
            {
                TransmissionId = primary?.TransmissionId,
                PatientControlNumber = primary?.PatientControlNumber,
                PayerClaimControlNumber = primary?.PayerClaimControlNumber ?? latestAck?.ClaimControlNumber,
                SubmissionId = primary?.SubmissionId,
                GatewayName = primary?.GatewayName
            },
            Patient = MapPerson(source?.Patient ?? source?.Subscriber),
            Provider = MapProvider(source?.BillingProvider),
            Payer = payer,
            LifecycleStatus = lifecycle,
            Transactions = new ClaimIntelligenceTransactionSet
            {
                Submission = primary is null ? null : Snapshot(
                    primary.Status.ToString(), primary.TransmissionId, primary.SubmittedAtUtc, "837"),
                Acknowledgment = latestAck is null ? null : Snapshot(
                    latestAck.Status.ToString(), latestAck.AcknowledgmentId, latestAck.ReceivedAtUtc, "277CA"),
                Status = latestStatus is null ? null : Snapshot(
                    latestStatus.NormalizedStatus.ToString(), latestStatus.InquiryId,
                    latestStatus.CompletedAtUtc ?? latestStatus.RequestedAtUtc, "276277"),
                Attachments = attachments.Count == 0 ? null : Snapshot(
                    attachments.Received ? "Received" : attachments.Requested ? "Requested" : "Submitted",
                    null, null, "275"),
                Remittance = matchedRemittance is null ? null : Snapshot(
                    matchedRemittance.Status.ToString(), matchedRemittance.RemittanceId,
                    matchedRemittance.ReceivedAtUtc, "835")
            },
            Financial = financial,
            Attachments = attachments,
            Timeline = timeline,
            Workflow = new ClaimIntelligenceWorkflow
            {
                ProcedureSummary = FirstProcedure(source),
                PayerDisplay = payer?.Name ?? payer?.PayerId,
                SubmittedOn = primary is null ? null : DateOnly.FromDateTime(primary.SubmittedAtUtc.UtcDateTime),
                Expected = ClaimIntelligenceMapper.MapExpected(lifecycle),
                PatientResponsibilityDisplay = financial.HasRemittance
                    ? financial.PatientResponsibility?.ToString("0.##")
                    : "Unknown",
                NextAction = next
            },
            Signals = new ClaimIntelligenceSignals
            {
                ActionRequired = next is ClaimIntelligenceNextAction.ProvideInformation
                    or ClaimIntelligenceNextAction.CorrectAndResubmit,
                NeedsFollowUp = next is ClaimIntelligenceNextAction.ProvideInformation
                    or ClaimIntelligenceNextAction.WaitForPayer
                    or ClaimIntelligenceNextAction.CorrectAndResubmit,
                MissingDocumentation = attachments.Requested && !attachments.Received,
                UnusualPayerResponse = latestAck?.Status == ClaimAcknowledgmentStatus.Rejected &&
                    primary?.Status == GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway,
                MissingTransactionLinks = missing
            },
            GeneratedAtUtc = _timeProvider.GetUtcNow()
        };
    }

    private async Task<ClaimIntelligencePayer?> ResolvePayerAsync(string? payerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payerId))
        {
            return null;
        }

        if (_payers is null)
        {
            return new ClaimIntelligencePayer { PayerId = payerId };
        }

        var record = await _payers.GetByIdAsync(payerId, ct).ConfigureAwait(false);
        return new ClaimIntelligencePayer
        {
            PayerId = payerId,
            Name = record?.Name
        };
    }

    private static ClaimIntelligenceFinancialSummary MapFinancial(
        ClaimTransmissionRecord? transmission,
        RemittedClaim? remitted,
        RemittanceReceipt? remittance)
    {
        if (remitted is null)
        {
            return new ClaimIntelligenceFinancialSummary
            {
                SubmittedAmount = transmission?.ClaimAmount ?? transmission?.InquirySource?.ClaimAmount,
                HasRemittance = false
            };
        }

        return new ClaimIntelligenceFinancialSummary
        {
            SubmittedAmount = remitted.ChargedAmount > 0
                ? remitted.ChargedAmount
                : transmission?.ClaimAmount,
            AllowedAmount = remitted.AllowedAmount,
            PaidAmount = remitted.PaidAmount,
            PatientResponsibility = remitted.PatientResponsibilityAmount,
            HasRemittance = remittance is not null
        };
    }

    private static ClaimIntelligenceAttachmentSummary MapAttachments(
        IReadOnlyList<ClaimAttachmentTransmissionRecord> outbound,
        IReadOnlyList<InboundClaimAttachmentReceipt> inbound)
    {
        var types = outbound.Select(a => a.AttachmentType.ToString())
            .Concat(inbound.Select(a => a.AttachmentType.ToString()))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        var requested = outbound.Any(a => a.Mode == ClaimAttachmentMode.Solicited) ||
                        inbound.Any(a => a.Mode == ClaimAttachmentMode.Solicited);
        var received = inbound.Any(a =>
            a.Status is InboundClaimAttachmentStatus.Matched
                or InboundClaimAttachmentStatus.AvailableToClaim
                or InboundClaimAttachmentStatus.Received
                or InboundClaimAttachmentStatus.Stored
                or InboundClaimAttachmentStatus.Validated);
        return new ClaimIntelligenceAttachmentSummary
        {
            Requested = requested,
            Received = received,
            AttachmentAvailable = received || outbound.Count > 0,
            Count = outbound.Count + inbound.Count,
            OutboundCount = outbound.Count,
            InboundCount = inbound.Count,
            Types = types
        };
    }

    private static List<ClaimIntelligenceTimelineEvent> BuildTimeline(
        IReadOnlyList<ClaimTransmissionRecord> transmissions,
        IReadOnlyList<ClaimAcknowledgmentRecord> acknowledgments,
        IReadOnlyList<ClaimStatusInquiryRecord> inquiries,
        IReadOnlyList<ClaimAttachmentTransmissionRecord> outbound,
        IReadOnlyList<InboundClaimAttachmentReceipt> inbound,
        IReadOnlyList<RemittanceReceipt> remittances,
        string claimId)
    {
        var events = new List<ClaimIntelligenceTimelineEvent>();
        foreach (var tx in transmissions)
        {
            events.Add(Event(
                $"837:{tx.TransmissionId}",
                tx.SubmittedAtUtc,
                tx.Status is GatewayClaimTransmissionStatus.SubmissionAcceptedByGateway
                    or GatewayClaimTransmissionStatus.Transmitted
                    ? "GatewayAccepted"
                    : "837Submitted",
                "837",
                tx.Status.ToString()));
        }

        foreach (var ack in acknowledgments)
        {
            events.Add(Event(
                $"277ca:{ack.AcknowledgmentId}",
                ack.ReceivedAtUtc,
                ack.Status == ClaimAcknowledgmentStatus.Rejected ? "277CARejected" : "277CAAccepted",
                "277CA",
                ack.Status.ToString()));
        }

        foreach (var inquiry in inquiries)
        {
            events.Add(Event(
                $"276:{inquiry.InquiryId}",
                inquiry.CompletedAtUtc ?? inquiry.RequestedAtUtc,
                "276277" + inquiry.NormalizedStatus,
                "276277",
                inquiry.NormalizedStatus.ToString()));
        }

        foreach (var attachment in outbound)
        {
            events.Add(Event(
                $"275-out:{attachment.AttachmentTransmissionId}",
                attachment.SubmittedAtUtc,
                "275AttachmentSubmitted",
                "275",
                attachment.Status.ToString()));
        }

        foreach (var attachment in inbound)
        {
            events.Add(Event(
                $"275-in:{attachment.ReceiptId}",
                attachment.ReceivedAtUtc,
                "275AttachmentReceived",
                "275",
                attachment.Status.ToString()));
        }

        foreach (var remittance in remittances)
        {
            var claim = SelectRemittedClaim(remittance, claimId, null);
            events.Add(Event(
                $"835:{remittance.ReceiptId}",
                remittance.ReceivedAtUtc,
                remittance.Status == RemittanceLifecycleStatus.Posted
                    ? "Posted"
                    : remittance.Status is RemittanceLifecycleStatus.AvailableForPosting
                        or RemittanceLifecycleStatus.Matched
                        ? "ReadyForPosting"
                        : "835Received",
                "835",
                remittance.Status.ToString(),
                claim is null ? null : "matched"));
        }

        return events
            .GroupBy(e => e.EventId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Timestamp)
            .ThenBy(e => e.EventId, StringComparer.Ordinal)
            .ToList();
    }

    private static RemittanceReceipt? SelectRemittance(
        IReadOnlyList<RemittanceReceipt> remittances,
        string claimId,
        string? transmissionId) =>
        remittances
            .Where(r => SelectRemittedClaim(r, claimId, transmissionId) is not null)
            .OrderByDescending(r => r.PaymentDate)
            .ThenByDescending(r => r.ReceivedAtUtc)
            .FirstOrDefault();

    private static RemittedClaim? SelectRemittedClaim(
        RemittanceReceipt? remittance, string claimId, string? transmissionId)
    {
        if (remittance is null)
        {
            return null;
        }

        return remittance.Claims.FirstOrDefault(c =>
            string.Equals(c.ClaimId, claimId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(transmissionId) &&
             string.Equals(c.TransmissionId, transmissionId, StringComparison.Ordinal)) ||
            string.Equals(c.PatientControlNumber, claimId, StringComparison.Ordinal));
    }

    private static ClaimIntelligenceParty? MapPerson(GatewayEligibilityPerson? person)
    {
        if (person is null)
        {
            return null;
        }

        return new ClaimIntelligenceParty
        {
            FirstName = person.FirstName,
            LastName = person.LastName,
            MemberId = person.MemberId
        };
    }

    private static ClaimIntelligenceParty? MapProvider(GatewayClaimProvider? provider)
    {
        if (provider is null)
        {
            return null;
        }

        return new ClaimIntelligenceParty
        {
            FirstName = provider.FirstName,
            LastName = provider.LastName,
            OrganizationName = provider.OrganizationName,
            Npi = provider.Npi
        };
    }

    private static string? FirstProcedure(ClaimStatusInquirySource? source)
    {
        var code = source?.ServiceLines.FirstOrDefault()?.ProcedureCode;
        return string.IsNullOrWhiteSpace(code) ? null : code;
    }

    private static ClaimIntelligenceTransactionSnapshot Snapshot(
        string status, string? recordId, DateTimeOffset? at, string source) =>
        new()
        {
            Status = status,
            RecordId = recordId,
            AtUtc = at,
            SourceTransaction = source
        };

    private static ClaimIntelligenceTimelineEvent Event(
        string id, DateTimeOffset at, string type, string source, string status, string? metadata = null) =>
        new()
        {
            EventId = id,
            Timestamp = at,
            EventType = type,
            SourceTransaction = source,
            Status = status,
            Metadata = metadata
        };

    private static List<T> DistinctBy<T>(IEnumerable<T> source, Func<T, string> key) =>
        source
            .GroupBy(key, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

    private void Log(ClaimIntelligenceView view) =>
        _logger.LogInformation(
            "Claim intelligence composed tenant={TenantId} claim={ClaimId} status={Status} " +
            "next={Next} missing={MissingCount} timeline={TimelineCount}",
            Sanitize(view.TenantId),
            Sanitize(view.ClaimId),
            view.LifecycleStatus,
            view.Workflow.NextAction,
            view.Signals.MissingTransactionLinks.Count,
            view.Timeline.Count);

    private static void RecordSuccess(ClaimIntelligenceView view, TimeSpan latency)
    {
        ChoMetrics.ClaimIntelligenceViews.Add(1,
            new KeyValuePair<string, object?>("cho.status", view.LifecycleStatus.ToString()),
            new KeyValuePair<string, object?>("cho.next_action", view.Workflow.NextAction.ToString()));
        ChoMetrics.ClaimIntelligenceDuration.Record(
            latency.TotalSeconds,
            new KeyValuePair<string, object?>("cho.status", view.LifecycleStatus.ToString()));
        ChoMetrics.ClaimIntelligenceRebuilds.Add(1,
            new KeyValuePair<string, object?>("cho.status", view.LifecycleStatus.ToString()));
    }

    private static void RecordFailed(string reason) =>
        ChoMetrics.ClaimIntelligenceFailures.Add(1,
            new KeyValuePair<string, object?>("cho.error_category", reason));

    private static string? Sanitize(string? value) => ClaimAttachmentRules.SanitizeForLog(value);
}
