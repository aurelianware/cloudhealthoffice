using System.Text;
using System.Text.Json;
using ClaimsService.Models;
using CloudHealthOffice.Events;
using Confluent.Kafka;

namespace ClaimsService.Services;

/// <summary>
/// Publishes claim lifecycle events to Kafka. Currently emits ClaimPendedEvent;
/// designed so additional event types (claim-approved, claim-finalized) can be
/// added without further consumer-side coupling — each event is its own type
/// with its own topic, no shared envelope schema to break.
/// </summary>
public interface IClaimEventPublisher
{
    /// <summary>
    /// Emit a ClaimPendedEvent. Safe to call when Kafka is unavailable —
    /// failures are logged but never propagated; the claim status update
    /// must succeed even if the event bus is degraded (claim DB is the
    /// source of truth, not the event stream).
    /// </summary>
    Task PublishClaimPendedAsync(Claim claim, string tenantId, CancellationToken ct = default);
}

public class ClaimEventPublisher : IClaimEventPublisher, IHostedService, IAsyncDisposable
{
    public const string ClaimPendedTopic = "claims.pended.v1";

    private readonly ILogger<ClaimEventPublisher> _logger;
    private readonly IConfiguration _configuration;
    private IProducer<string, string>? _producer;
    private bool _available;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public ClaimEventPublisher(ILogger<ClaimEventPublisher> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var bootstrapServers = _configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrEmpty(bootstrapServers))
        {
            _logger.LogWarning("Kafka:BootstrapServers not configured — claim event publisher disabled");
            return Task.CompletedTask;
        }

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            ClientId = _configuration["Kafka:ClientId"] ?? "claims-service",
            MessageTimeoutMs = 10_000,
            RequestTimeoutMs = 10_000,
            SocketTimeoutMs = 10_000,
            EnableIdempotence = true
        };

        var saslUsername = _configuration["Kafka:SaslUsername"];
        if (!string.IsNullOrEmpty(saslUsername))
        {
            producerConfig.SaslUsername = saslUsername;
            producerConfig.SaslPassword = _configuration["Kafka:SaslPassword"];
            producerConfig.SaslMechanism = SaslMechanism.ScramSha512;
            producerConfig.SecurityProtocol = SecurityProtocol.SaslSsl;
        }

        try
        {
            _producer = new ProducerBuilder<string, string>(producerConfig).Build();
            _available = true;
            _logger.LogInformation("Claim event publisher connected to Kafka at {Servers}", bootstrapServers);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kafka producer init failed — claim event publisher running in degraded mode");
            _producer?.Dispose();
            _producer = null;
            _available = false;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _producer?.Flush(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error flushing Kafka producer on shutdown");
        }
        _producer?.Dispose();
        _producer = null;
        _available = false;
        return Task.CompletedTask;
    }

    public async Task PublishClaimPendedAsync(Claim claim, string tenantId, CancellationToken ct = default)
    {
        if (!_available || _producer == null)
        {
            _logger.LogDebug("Kafka producer unavailable; skipping ClaimPendedEvent for claim {ClaimId}", claim.Id);
            return;
        }

        var evt = new ClaimPendedEvent
        {
            TenantId = tenantId,
            ClaimId = claim.Id,
            ClaimNumber = claim.ClaimNumber,
            MemberId = claim.MemberId,
            BillingProviderNPI = claim.BillingProviderNPI,
            LineOfBusiness = claim.LineOfBusiness.ToString(),
            TotalChargeAmount = claim.TotalChargeAmount,
            ServiceDateFrom = claim.ServiceDateFrom,
            PendDetails = MapPendDetails(claim.PendDetails)
        };

        var message = new Message<string, string>
        {
            Key = claim.Id,
            Value = JsonSerializer.Serialize(evt, JsonOptions),
            Headers = new Headers
            {
                { "tenant-id", Encoding.UTF8.GetBytes(tenantId) },
                { "event-type", Encoding.UTF8.GetBytes(evt.EventType) },
                { "event-version", Encoding.UTF8.GetBytes(evt.EventVersion) }
            }
        };

        try
        {
            await _producer.ProduceAsync(ClaimPendedTopic, message, ct);
            _logger.LogInformation("Published ClaimPendedEvent for claim {ClaimId} to {Topic}",
                claim.Id, ClaimPendedTopic);
        }
        catch (ProduceException<string, string> ex)
        {
            _logger.LogError(ex, "Failed to publish ClaimPendedEvent for claim {ClaimId}: {Reason}",
                claim.Id, ex.Error.Reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error publishing ClaimPendedEvent for claim {ClaimId}", claim.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        GC.SuppressFinalize(this);
    }

    private static CloudHealthOffice.Events.PendDetails? MapPendDetails(ClaimsService.Models.PendDetails? source)
    {
        if (source is null) return null;
        return new CloudHealthOffice.Events.PendDetails
        {
            PendCode = source.PendCode,
            PendReason = source.PendReason,
            PendedAt = source.PendedAt,
            EditFailures = source.EditFailures
                .Select(e => new CloudHealthOffice.Events.NcciEditFailureSnapshot
                {
                    EditType = e.EditType,
                    RuleId = e.RuleId,
                    Message = e.Message,
                    Column1Code = e.Column1Code,
                    Column2Code = e.Column2Code,
                    AffectedLineNumbers = e.AffectedLineNumbers.ToList(),
                    ModifierOverridePresent = e.ModifierOverridePresent,
                    UnitsBilled = e.UnitsBilled,
                    MueMaxUnits = e.MueMaxUnits,
                    SuggestedCarc = e.SuggestedCarc,
                    SuggestedRarc = e.SuggestedRarc
                })
                .ToList()
        };
    }
}
