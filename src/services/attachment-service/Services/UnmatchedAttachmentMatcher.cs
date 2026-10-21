using AttachmentService.Models;
using AttachmentService.Repositories;
using CloudHealthOffice.Infrastructure.Logging;
using Microsoft.Extensions.Options;

namespace AttachmentService.Services;

public sealed class AttachmentMatchingOptions
{
    public const string SectionName = "AttachmentMatching";

    /// <summary>Whether the background matcher runs. Off only for tests and one-off tools.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often held 275s are matched again.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a 275 waits for its claim before it is rejected (NO_MATCHING_CLAIM, an 824 TR).</summary>
    public int HoldDays { get; set; } = 30;

    /// <summary>Held attachments looked at per run.</summary>
    public int BatchSize { get; set; } = 200;
}

/// <summary>
/// 275s often arrive before their 837. One that named no claim and whose
/// attachment control number (TRN02) no claim had announced is held as
/// <c>Unmatched</c>, its file in the catalog under its provider. This job
/// looks again on an interval: once a claim with that PWK06 exists, the
/// attachment is linked to it (the catalog document gains the claim link)
/// and marked Linked. After <see cref="AttachmentMatchingOptions.HoldDays"/>
/// it is rejected with <see cref="AttachmentRejectionCode.NoMatchingClaim"/>.
/// Every call uses attachment-service's own service token for the
/// attachment's tenant; there is no user on the call.
/// <para>
/// It may run on every replica: linking is idempotent (the catalog answers
/// an existing link as done) and each attachment save is revision-checked,
/// so a second replica's save of the same attachment fails and the
/// attachment is simply looked at again next run.
/// </para>
/// </summary>
public sealed class UnmatchedAttachmentMatcher : BackgroundService
{
    public const string TenantScopingSetting = "MongoDb:UseTenantScoping";

    private readonly IServiceScopeFactory _scopes;
    private readonly AttachmentMatchingOptions _options;
    private readonly string? _unsupportedReason;
    private readonly TimeProvider _clock;
    private readonly ILogger<UnmatchedAttachmentMatcher> _logger;

    public UnmatchedAttachmentMatcher(
        IServiceScopeFactory scopes,
        IOptions<AttachmentMatchingOptions> options,
        IConfiguration configuration,
        TimeProvider clock,
        ILogger<UnmatchedAttachmentMatcher> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _clock = clock;
        _logger = logger;

        // With a database per tenant there is no request to pick the tenant's
        // database, so the matcher would see only the base database and find
        // nothing, silently. It refuses instead, like the claims outboxes and
        // the SLA watchdog.
        if (configuration.GetValue(TenantScopingSetting, false))
        {
            _unsupportedReason =
                "MongoDb:UseTenantScoping=true is not supported by the attachment matcher: with no request it can reach " +
                "only the base database, not each tenant's. Held 275s will not be linked or expired.";
        }
    }

    /// <summary>Why the matcher will not run in this deployment; null when it can.</summary>
    public string? UnsupportedReason => _unsupportedReason;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;
        if (_unsupportedReason is not null)
        {
            _logger.LogCritical("{Reason}", _unsupportedReason);
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Matching held attachments failed; retrying next interval");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over the held attachments. Returns how many were linked and how many expired.</summary>
    public async Task<(int Linked, int Expired)> RunOnceAsync(CancellationToken ct)
    {
        if (_unsupportedReason is not null)
            throw new InvalidOperationException(_unsupportedReason);

        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAttachmentRepository>();
        var matcher = scope.ServiceProvider.GetRequiredService<IClaimAttachmentMatcher>();
        var catalog = scope.ServiceProvider.GetRequiredService<IDocumentCatalog>();

        var linked = 0;
        var expired = 0;
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var attachment in await repository.ListUnmatchedAsync(Math.Max(1, _options.BatchSize)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (await MatchAsync(attachment, repository, matcher, catalog, now, ct))
                {
                    case Outcome.Linked: linked++; break;
                    case Outcome.Expired: expired++; break;
                }
            }
            catch (Exception ex) when (ex is AttachmentParentLookupException or DocumentCatalogException or AttachmentConcurrencyException)
            {
                // Try again next run; one attachment never stops the others.
                _logger.LogWarning(ex, "Could not match held attachment {AttachmentId} in tenant {TenantId} this run",
                    LogSanitizer.Sanitize(attachment.Id), LogSanitizer.Sanitize(attachment.TenantId));
            }
        }

        if (linked > 0 || expired > 0)
            _logger.LogInformation("Held attachments: {Linked} linked to their claim, {Expired} rejected after {HoldDays} days",
                linked, expired, _options.HoldDays);
        return (linked, expired);
    }

    private enum Outcome { Waiting, Linked, Expired }

    private async Task<Outcome> MatchAsync(Attachment attachment, IAttachmentRepository repository,
        IClaimAttachmentMatcher matcher, IDocumentCatalog catalog, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attachment.AttachmentControlNumber))
            return Outcome.Waiting;

        var matches = await matcher.FindAsync(attachment.TenantId, attachment.AttachmentControlNumber, ct);
        if (IClaimAttachmentMatcher.Choose(matches, attachment.ProviderId) is { } claimId)
        {
            if (attachment.DocumentId is { } documentId)
            {
                var found = await catalog.AddLinkAsync(attachment.TenantId, onBehalfOf: null, documentId,
                    AttachmentParentType.Provider, attachment.ProviderId, AttachmentParentType.Claim, claimId, ct);
                if (!found)
                {
                    _logger.LogError("Held attachment {AttachmentId} points at catalog document {DocumentId}, which is not on provider {ProviderId}",
                        LogSanitizer.Sanitize(attachment.Id), LogSanitizer.Sanitize(documentId), LogSanitizer.Sanitize(attachment.ProviderId));
                    return Outcome.Waiting;
                }
            }

            attachment.ClaimId = claimId;
            attachment.Status = "Linked";
            attachment.Notes = $"Matched to claim {claimId} by attachment control number {attachment.AttachmentControlNumber}";
            attachment.LastUpdatedBy = "attachment-matcher";
            await repository.UpdateAsync(attachment);
            return Outcome.Linked;
        }

        if (attachment.CreatedDate.AddDays(_options.HoldDays) <= now)
        {
            attachment.Status = "Failed";
            attachment.RejectionCode = AttachmentRejectionCode.NoMatchingClaim;
            attachment.Notes = $"No claim announced attachment control number {attachment.AttachmentControlNumber} within {_options.HoldDays} days";
            attachment.LastUpdatedBy = "attachment-matcher";
            await repository.UpdateAsync(attachment);
            return Outcome.Expired;
        }

        return Outcome.Waiting;
    }
}
