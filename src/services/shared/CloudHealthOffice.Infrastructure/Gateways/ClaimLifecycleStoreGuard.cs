using CloudHealthOffice.Infrastructure.Gateways.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudHealthOffice.Infrastructure.Gateways;

/// <summary>
/// Fails startup when a non-Development host would silently use process-local
/// 277CA/transmission storage.
/// </summary>
internal sealed class ClaimLifecycleStoreGuard : IHostedService
{
    private readonly IClaimAcknowledgmentStore _acknowledgments;
    private readonly IClaimTransmissionStore _transmissions;
    private readonly IClaimAcknowledgmentCursorStore _cursors;
    private readonly IClaimAttachmentTransmissionStore _attachments;
    private readonly IClaimAttachmentContentStore _content;
    private readonly IOptions<HealthcareTransactionOptions> _options;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger<ClaimLifecycleStoreGuard> _logger;

    public ClaimLifecycleStoreGuard(
        IClaimAcknowledgmentStore acknowledgments,
        IClaimTransmissionStore transmissions,
        IClaimAcknowledgmentCursorStore cursors,
        IClaimAttachmentTransmissionStore attachments,
        IClaimAttachmentContentStore content,
        IOptions<HealthcareTransactionOptions> options,
        ILogger<ClaimLifecycleStoreGuard> logger,
        IHostEnvironment? environment = null)
    {
        _acknowledgments = acknowledgments;
        _transmissions = transmissions;
        _cursors = cursors;
        _attachments = attachments;
        _content = content;
        _options = options;
        _logger = logger;
        _environment = environment;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var ephemeral = _acknowledgments is InMemoryClaimAcknowledgmentStore ||
                        _transmissions is InMemoryClaimTransmissionStore ||
                        _cursors is InMemoryClaimAcknowledgmentCursorStore ||
                        _attachments is InMemoryClaimAttachmentTransmissionStore ||
                        _content is InMemoryClaimAttachmentContentStore;
        var allowed = ClaimLifecycleStoreResolver.AllowsEphemeral(_options.Value.ClaimLifecycle, _environment);

        if (ephemeral && !allowed)
        {
            throw new InvalidOperationException(
                "HealthcareTransactions:ClaimLifecycle:Store must be Mongo (with IMongoClient) " +
                "and IClaimAttachmentContentStore must be a durable implementation " +
                "in non-Development environments. In-memory 277CA/attachment storage is not " +
                "durable and is not used silently in production.");
        }

        if (!ephemeral)
        {
            _logger.LogInformation("Claim lifecycle persistence is durable ({StoreType}).",
                _acknowledgments.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class ClaimLifecycleStoreResolver
{
    public static bool AllowsEphemeral(ClaimLifecycleOptions options, IHostEnvironment? environment)
    {
        if (options.AllowInMemoryInNonDevelopment)
        {
            return true;
        }

        return environment is null || environment.IsDevelopment();
    }
}

internal sealed class ClaimLifecycleIndexHostedService : IHostedService
{
    private readonly MongoClaimLifecycleStore? _mongo;

    public ClaimLifecycleIndexHostedService(IClaimAcknowledgmentStore store)
    {
        _mongo = store as MongoClaimLifecycleStore;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _mongo is null ? Task.CompletedTask : _mongo.EnsureIndexesAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ClaimAcknowledgmentOutboxPublisher : BackgroundService
{
    private readonly IClaimAcknowledgmentProcessor _processor;
    private readonly IOptions<HealthcareTransactionOptions> _options;
    private readonly ILogger<ClaimAcknowledgmentOutboxPublisher> _logger;

    public ClaimAcknowledgmentOutboxPublisher(
        IClaimAcknowledgmentProcessor processor,
        IOptions<HealthcareTransactionOptions> options,
        ILogger<ClaimAcknowledgmentOutboxPublisher> logger)
    {
        _processor = processor;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = _options.Value.ClaimLifecycle.OutboxIntervalSeconds;
        if (seconds <= 0)
        {
            _logger.LogInformation("Claim acknowledgment outbox publisher is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _processor.DispatchPendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Claim acknowledgment outbox dispatch failed");
            }
        }
    }
}
