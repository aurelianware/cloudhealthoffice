using System.Text.Json;
using System.Text.Json.Nodes;
using AppealsService.Models;
using AppealsService.Repositories;
using AppealsService.Services;
using Confluent.Kafka;

namespace AppealsService.HostedServices;

/// <summary>
/// Background consumer for the X12 275 Kafka topic
/// (default <c>"attachments-in"</c>). Filters for messages whose
/// envelope <c>context</c> field equals <c>"appeal"</c>, locates the
/// open appeal in the tenant via
/// <see cref="IAppealRepository.GetMostRecentAppealByClaimIdAsync"/>,
/// and routes the 275 into the existing
/// <see cref="IAppealRepository.AppendAttachmentAsync"/> path. Mirrors
/// the structure of <c>ClaimsExaminerService.Services.Kafka.ClaimPendedConsumer</c>:
/// degraded-mode no-op when <c>Kafka:BootstrapServers</c> is empty,
/// 1-second poll loop, commit offset regardless of handler outcome so
/// poison messages do not block the queue.
///
/// Adopts the dual-constructor pattern from
/// <c>AuthorizationService.Consumers.RfaiDocsReceivedConsumer</c> so the
/// per-message handler is exercisable from the test project without
/// standing up a fake <see cref="IServiceProvider"/>.
/// </summary>
public sealed class Attachment275ConsumerHostedService : BackgroundService
{
    public const string AppealContext = "appeal";
    public const string IngressActor = "appeals-service:275-consumer";
    public const string UnknownCorrelationId = "x12-275-unknown";
    public const string DefaultTopic = "attachments-in";
    public const string DefaultGroupId = "appeals-service-275-consumer";
    public const string IngressSourcePayloadKey = "ingressSource";
    public const string IngressSourcePayloadValue = "Availity275";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IServiceProvider? _services;
    private readonly IConfiguration? _configuration;
    private readonly ILogger<Attachment275ConsumerHostedService> _logger;

    // Test-mode collaborators. When non-null, HandleMessageAsync uses
    // these directly instead of resolving from a per-message scope. Keeps
    // the hosted service's message-handling logic exercisable without a
    // fake IServiceProvider.
    private readonly IAppealRepository? _testRepository;
    private readonly IAppealEventPublisher? _testPublisher;
    private readonly IAppealFieldEncryptor? _testEncryptor;
    private readonly IAttachment275DeadLetterSink? _testDeadLetterSink;
    private readonly Attachment275EnvelopeMapper? _testMapper;

    public Attachment275ConsumerHostedService(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<Attachment275ConsumerHostedService> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    internal Attachment275ConsumerHostedService(
        IAppealRepository repository,
        IAppealEventPublisher publisher,
        IAppealFieldEncryptor encryptor,
        IAttachment275DeadLetterSink deadLetterSink,
        Attachment275EnvelopeMapper mapper,
        ILogger<Attachment275ConsumerHostedService> logger)
    {
        _testRepository = repository;
        _testPublisher = publisher;
        _testEncryptor = encryptor;
        _testDeadLetterSink = deadLetterSink;
        _testMapper = mapper;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield before the synchronous Consume loop so BackgroundService.StartAsync
        // can return and let Kestrel + sibling hosted services initialize.
        // Same rationale as ClaimPendedConsumer.cs:55.
        await Task.Yield();

        if (_configuration is null)
        {
            // Test-ctor instances do not run the consume loop; tests drive
            // HandleMessageAsync directly.
            return;
        }

        var bootstrap = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrap))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — 275 attachment consumer disabled");
            return;
        }

        var topic = _configuration["Kafka:AttachmentsInTopic"] ?? DefaultTopic;
        var groupId = _configuration["Kafka:Attachment275ConsumerGroupId"] ?? DefaultGroupId;

        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            GroupId = groupId,
            ClientId = _configuration["Kafka:ClientId"] ?? "appeals-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            SessionTimeoutMs = 45_000
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            config.SaslUsername = saslUsername;
            config.SaslPassword = _configuration["Kafka:SaslPassword"];
            config.SaslMechanism = SaslMechanism.ScramSha512;
            config.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        IConsumer<string, string>? consumer = null;
        try
        {
            consumer = new ConsumerBuilder<string, string>(config).Build();
            consumer.Subscribe(topic);
            _logger.LogInformation(
                "275 attachment consumer subscribed to {Topic} (group={Group}, bootstrap={Bootstrap})",
                LogSanitizer.SafeForLog(topic),
                LogSanitizer.SafeForLog(groupId),
                LogSanitizer.SafeForLog(bootstrap));

            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(TimeSpan.FromSeconds(1));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ConsumeException ex)
                {
                    _logger.LogError(ex, "Kafka consume error: {Reason}",
                        LogSanitizer.SafeForLog(ex.Error.Reason));
                    continue;
                }

                if (result?.Message is null)
                {
                    continue;
                }

                // HandleMessageAsync is allowed to throw only in pathological
                // cases (e.g. DI scope creation failure). Catch at the outer
                // boundary so the offset still advances and the queue drains.
                try
                {
                    await HandleMessageAsync(result.Message.Value, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Unhandled error in 275 message handler at {Topic} {Partition} {Offset}; advancing offset",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                }

                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException ex)
                {
                    _logger.LogError(ex, "Failed to commit offset for {Topic} {Partition} {Offset}",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "275 attachment consumer crashed");
        }
        finally
        {
            try { consumer?.Close(); } catch { /* ignore on shutdown */ }
            consumer?.Dispose();
        }
    }

    /// <summary>
    /// Process a single Kafka message value (envelope JSON). Returns the
    /// outcome so tests can assert each branch of the routing decision tree.
    /// </summary>
    internal async Task<Attachment275HandleOutcome> HandleMessageAsync(
        string messageValue, CancellationToken ct)
    {
        Attachment275EnvelopeDto? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Attachment275EnvelopeDto>(messageValue, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "275 envelope deserialization failed; dead-lettering raw message ({Bytes} bytes)",
                messageValue?.Length ?? 0);
            await ResolveDeadLetterSink().DeadLetterMalformedAsync(
                messageValue ?? string.Empty, "json-deserialization-failed", ct);
            return Attachment275HandleOutcome.DeadLetteredMalformedJson;
        }

        if (envelope is null)
        {
            _logger.LogWarning("275 envelope deserialized to null; dead-lettering");
            await ResolveDeadLetterSink().DeadLetterMalformedAsync(
                messageValue ?? string.Empty, "envelope-null", ct);
            return Attachment275HandleOutcome.DeadLetteredMalformedJson;
        }

        if (!string.Equals(envelope.Context, AppealContext, StringComparison.Ordinal))
        {
            // Not an appeal-context message. Skipped; not a failure.
            // Pre-Argo-PR, this is the expected fall-through for every
            // production message — keep at debug to avoid log spam.
            _logger.LogDebug(
                "275 envelope context={Context} is not '{AppealContext}'; skipping",
                LogSanitizer.SafeForLog(envelope.Context),
                AppealContext);
            return Attachment275HandleOutcome.SkippedNonAppealContext;
        }

        if (string.IsNullOrEmpty(envelope.TenantId))
        {
            await ResolveDeadLetterSink().DeadLetterAsync(envelope, "missing-tenantId", ct);
            return Attachment275HandleOutcome.DeadLetteredMissingRequiredField;
        }
        if (string.IsNullOrEmpty(envelope.ClaimId))
        {
            await ResolveDeadLetterSink().DeadLetterAsync(envelope, "missing-claimId", ct);
            return Attachment275HandleOutcome.DeadLetteredMissingRequiredField;
        }
        if (string.IsNullOrEmpty(envelope.RawX12))
        {
            await ResolveDeadLetterSink().DeadLetterAsync(envelope, "missing-rawX12", ct);
            return Attachment275HandleOutcome.DeadLetteredMissingRequiredField;
        }

        if (_testRepository is not null)
        {
            return await RouteAsync(
                envelope,
                _testRepository,
                _testPublisher!,
                _testEncryptor!,
                _testDeadLetterSink!,
                _testMapper!,
                ct);
        }

        // Per-message DI scope so scoped collaborators (Cosmos repository,
        // encryptor key handles, etc.) get fresh lifetimes per envelope.
        using var scope = _services!.CreateScope();
        var sp = scope.ServiceProvider;
        return await RouteAsync(
            envelope,
            sp.GetRequiredService<IAppealRepository>(),
            sp.GetRequiredService<IAppealEventPublisher>(),
            sp.GetRequiredService<IAppealFieldEncryptor>(),
            sp.GetRequiredService<IAttachment275DeadLetterSink>(),
            sp.GetRequiredService<Attachment275EnvelopeMapper>(),
            ct);
    }

    private async Task<Attachment275HandleOutcome> RouteAsync(
        Attachment275EnvelopeDto envelope,
        IAppealRepository repository,
        IAppealEventPublisher publisher,
        IAppealFieldEncryptor encryptor,
        IAttachment275DeadLetterSink deadLetterSink,
        Attachment275EnvelopeMapper mapper,
        CancellationToken ct)
    {
        try
        {
            var appeal = await repository.GetMostRecentAppealByClaimIdAsync(
                envelope.TenantId, envelope.ClaimId!, ct);
            if (appeal is null)
            {
                _logger.LogWarning(
                    "275 envelope: no open appeal for tenantId={TenantId} claimId={ClaimId} controlNumber={ControlNumber}",
                    LogSanitizer.SafeForLog(envelope.TenantId),
                    LogSanitizer.SafeForLog(envelope.ClaimId),
                    LogSanitizer.SafeForLog(envelope.ControlNumber));
                await deadLetterSink.DeadLetterAsync(envelope, "no-open-appeal-for-claim", ct);
                return Attachment275HandleOutcome.DeadLetteredNoOpenAppeal;
            }

            var encryptedDescription = await encryptor.EncryptAsync(envelope.Notes, ct);
            var attachment = mapper.ToAppealAttachment(envelope, encryptedDescription);

            var correlationId = string.IsNullOrEmpty(envelope.ControlNumber)
                ? UnknownCorrelationId
                : envelope.ControlNumber;

            var auditEvent = new AppealEvent
            {
                TenantId = appeal.TenantId,
                AppealId = appeal.Id,
                EventId = Guid.NewGuid().ToString(),
                EventType = AppealEventType.AppealAttachmentAdded,
                ActorId = IngressActor,
                CorrelationId = correlationId,
                OccurredAt = DateTime.UtcNow,
                Payload = new JsonObject
                {
                    ["attachmentId"] = attachment.AttachmentId,
                    ["attachmentTypeCode"] = attachment.AttachmentTypeCode,
                    ["transmissionCode"] = attachment.TransmissionCode,
                    ["controlNumber"] = attachment.ControlNumber,
                    ["uploadedAt"] = attachment.UploadedAt.ToString("o"),
                    [IngressSourcePayloadKey] = IngressSourcePayloadValue
                }
            };

            var updated = await repository.AppendAttachmentAsync(appeal, attachment, auditEvent, ct);
            await publisher.PublishAttachmentAddedAsync(updated, attachment, IngressActor, correlationId, ct);

            _logger.LogInformation(
                "275 attachment routed: tenantId={TenantId} appealId={AppealId} attachmentId={AttachmentId} controlNumber={ControlNumber}",
                LogSanitizer.SafeForLog(updated.TenantId),
                LogSanitizer.SafeForLog(updated.Id),
                LogSanitizer.SafeForLog(attachment.AttachmentId),
                LogSanitizer.SafeForLog(envelope.ControlNumber));

            return Attachment275HandleOutcome.Routed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "275 envelope routing failed: tenantId={TenantId} claimId={ClaimId} controlNumber={ControlNumber}; dead-lettering and advancing offset",
                LogSanitizer.SafeForLog(envelope.TenantId),
                LogSanitizer.SafeForLog(envelope.ClaimId),
                LogSanitizer.SafeForLog(envelope.ControlNumber));
            await deadLetterSink.DeadLetterAsync(envelope, "handler-exception", ct);
            return Attachment275HandleOutcome.DeadLetteredHandlerException;
        }
    }

    private IAttachment275DeadLetterSink ResolveDeadLetterSink()
    {
        if (_testDeadLetterSink is not null) return _testDeadLetterSink;
        // For the pre-route paths (deserialization failure, missing
        // required fields) we resolve a transient sink without holding
        // a full per-message scope open — the sink is a singleton.
        return _services!.GetRequiredService<IAttachment275DeadLetterSink>();
    }
}

/// <summary>
/// Outcome of one
/// <see cref="Attachment275ConsumerHostedService.HandleMessageAsync"/> call.
/// Internal because it's a test-only assertion target — the production
/// path commits the offset regardless of which value is returned.
/// </summary>
internal enum Attachment275HandleOutcome
{
    Routed,
    SkippedNonAppealContext,
    DeadLetteredMalformedJson,
    DeadLetteredMissingRequiredField,
    DeadLetteredNoOpenAppeal,
    DeadLetteredHandlerException
}
