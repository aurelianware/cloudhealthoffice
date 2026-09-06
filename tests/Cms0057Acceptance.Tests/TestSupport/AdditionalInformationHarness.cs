using System.Text.Json;
using AuthorizationService.Consumers;
using FhirService.Services.Cdex;
using Microsoft.Extensions.Configuration;
using RfaiService.Models;
using RfaiService.Repositories;
using RfaiService.Services;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// Test-only in-memory <see cref="IRfaiRepository"/>.
///
/// A UNIT-TEST FIXTURE, not a second implementation of the rules: the production
/// <see cref="RfaiCaseService"/> and the pure
/// <see cref="RfaiCaseLifecycle"/> run against it unchanged, so what PAS-07
/// proves is the real aggregate.
///
/// Two production behaviours are mirrored deliberately, because the acceptance
/// claims depend on them:
///   * documents are JSON-snapshotted on read and write, so a caller's mutation
///     cannot leak into the store — a record genuinely survives persistence;
///   * <see cref="CreateIfAbsentAsync"/> is a CONDITIONAL insert on the primary
///     key, exactly as Cosmos (409) and Mongo (duplicate key) behave, so the
///     concurrency claim is tested against the same rule production relies on.
/// </summary>
internal sealed class InMemoryRfaiRepository : IRfaiRepository
{
    private readonly Dictionary<string, RfaiCase> _store = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Inserts that actually created a document.</summary>
    public int CreateCount { get; private set; }

    /// <summary>Inserts refused because the id was already taken.</summary>
    public int ConflictCount { get; private set; }

    /// <summary>Runs immediately before each conditional insert — a concurrency hook.</summary>
    public Action<RfaiCase>? OnBeforeCreate { get; set; }

    private static string Key(string tenantId, string id) => $"{tenantId}|{id}";

    private static RfaiCase Clone(RfaiCase c) =>
        JsonSerializer.Deserialize<RfaiCase>(JsonSerializer.Serialize(c))!;

    public Task<RfaiCase?> GetByIdAsync(string tenantId, string id)
    {
        lock (_gate)
        {
            return Task.FromResult(
                _store.TryGetValue(Key(tenantId, id), out var found) ? Clone(found) : null);
        }
    }

    public Task<List<RfaiCase>> GetByAuthNumberAsync(string tenantId, string authNumber)
    {
        lock (_gate)
        {
            return Task.FromResult(_store.Values
                .Where(c => string.Equals(c.TenantId, tenantId, StringComparison.Ordinal)
                            && string.Equals(c.AuthNumber, authNumber, StringComparison.Ordinal))
                .OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.Sequence)
                .Select(Clone)
                .ToList());
        }
    }

    public Task<RfaiCase?> GetByTrackingIdAsync(string tenantId, string trackingId)
    {
        lock (_gate)
        {
            var match = _store.Values.FirstOrDefault(c =>
                string.Equals(c.TenantId, tenantId, StringComparison.Ordinal)
                && string.Equals(c.TrackingId, trackingId, StringComparison.Ordinal));

            return Task.FromResult(match is null ? null : Clone(match));
        }
    }

    public Task<RfaiCase> CreateAsync(RfaiCase rfaiCase)
    {
        lock (_gate)
        {
            _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
            CreateCount++;
            return Task.FromResult(Clone(rfaiCase));
        }
    }

    public Task<(RfaiCase Case, bool Created)> CreateIfAbsentAsync(RfaiCase rfaiCase)
    {
        OnBeforeCreate?.Invoke(rfaiCase);

        lock (_gate)
        {
            var key = Key(rfaiCase.TenantId, rfaiCase.Id);
            if (_store.TryGetValue(key, out var existing))
            {
                ConflictCount++;
                return Task.FromResult((Clone(existing), false));
            }

            _store[key] = Clone(rfaiCase);
            CreateCount++;
            return Task.FromResult((Clone(rfaiCase), true));
        }
    }

    public Task<RfaiCase> UpdateAsync(RfaiCase rfaiCase)
    {
        lock (_gate)
        {
            rfaiCase.UpdatedAt = DateTime.UtcNow;
            _store[Key(rfaiCase.TenantId, rfaiCase.Id)] = Clone(rfaiCase);
            return Task.FromResult(Clone(rfaiCase));
        }
    }

    /// <summary>Every stored case, for assertions about history and cycles.</summary>
    public IReadOnlyList<RfaiCase> All
    {
        get { lock (_gate) { return _store.Values.Select(Clone).ToList(); } }
    }
}

/// <summary>
/// Captures the resume-review announcement rfai-service publishes instead of
/// putting it on a broker, so a scenario can hand it to the REAL
/// <see cref="RfaiDocsReceivedConsumer"/> and prove what the authorization does
/// next.
/// </summary>
internal sealed class RecordingKafkaProducer : IKafkaProducerService
{
    public List<(string Topic, string Key, object Value)> Published { get; } = new();

    public Task SendAsync(
        string topic, string key, object value, Dictionary<string, string>? headers = null)
    {
        Published.Add((topic, key, value));
        return Task.CompletedTask;
    }

    /// <summary>The announcements, in the shape the authorization-service consumer reads.</summary>
    public IReadOnlyList<RfaiDocsReceivedMessage> DocsReceivedMessages =>
        Published
            .Select(p => JsonSerializer.Deserialize<RfaiDocsReceivedMessage>(
                JsonSerializer.Serialize(p.Value),
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true,
                })!)
            .ToList();
}

/// <summary>
/// The in-process stand-in for the HTTP hop between fhir-service and
/// rfai-service.
///
/// It implements fhir-service's read/write seam by calling the REAL rfai-service
/// aggregate. Only the transport is elided: every rule the CDex surface depends
/// on — one open cycle per authorization, idempotency by submission id, the
/// artifact cap, the single resume-review announcement — is the production rule,
/// executing here.
///
/// Tenant is passed explicitly on every call, exactly as the HTTP store passes
/// it as a header, so the cross-tenant scenarios exercise the same isolation.
/// </summary>
internal sealed class LocalCdexAdditionalInformationStore : ICdexAdditionalInformationStore
{
    private readonly IRfaiRepository _repository;
    private readonly IRfaiCaseService _cases;

    public LocalCdexAdditionalInformationStore(IRfaiRepository repository, IRfaiCaseService cases)
    {
        _repository = repository;
        _cases = cases;
    }

    public async Task<CdexAdditionalInformationRequest?> GetByIdAsync(
        string tenantId, string id, CancellationToken ct = default)
        => Project(await _repository.GetByIdAsync(tenantId, id));

    public async Task<CdexAdditionalInformationRequest?> GetByTrackingIdAsync(
        string tenantId, string trackingId, CancellationToken ct = default)
        => Project(await _repository.GetByTrackingIdAsync(tenantId, trackingId));

    public async Task<IReadOnlyList<CdexAdditionalInformationRequest>> GetByAuthorizationNumberAsync(
        string tenantId, string authorizationNumber, CancellationToken ct = default)
        => (await _repository.GetByAuthNumberAsync(tenantId, authorizationNumber))
            .Select(Project)
            .OfType<CdexAdditionalInformationRequest>()
            .ToList();

    public async Task MarkDeliveredAsync(string tenantId, string id, CancellationToken ct = default)
        => await _cases.MarkDeliveredAsync(tenantId, id, ct);

    public async Task<CdexResponseRecordResult?> RecordResponseAsync(
        string tenantId, string id, IReadOnlyList<CdexResponseArtifact> artifacts,
        CancellationToken ct = default)
    {
        var offered = artifacts.Select(a => new RfaiResponseArtifact
        {
            SubmissionId = a.SubmissionId,
            AttachmentControlNumber = a.AttachmentControlNumber,
            StorageProvider = a.StorageProvider,
            StorageKey = a.StorageKey,
            FileHash = a.FileHash,
            ContentType = a.ContentType,
            SizeBytes = a.SizeBytes,
            Title = a.Title,
            DocumentTypeCode = a.DocumentTypeCode,
            DocumentTypeSystem = a.DocumentTypeSystem,
            SubmittedBy = a.SubmittedBy,
            Channel = a.Channel,
        }).ToList();

        var result = await _cases.RecordResponseAsync(tenantId, id, offered, ct);
        if (result is null) return null;

        return new CdexResponseRecordResult
        {
            Outcome = result.Outcome.ToString(),
            Recorded = result.Recorded.Count,
            ResumedReview = result.TransitionedToDocsReceived,
        };
    }

    /// <summary>
    /// The same narrowing the HTTP store performs when it deserializes
    /// rfai-service's response: identifiers, status and receipt metadata only.
    /// </summary>
    private static CdexAdditionalInformationRequest? Project(RfaiCase? c)
    {
        if (c is null) return null;

        return new CdexAdditionalInformationRequest
        {
            TenantId = c.TenantId,
            Id = c.Id,
            AuthNumber = c.AuthNumber,
            AuthorizationId = c.AuthorizationId,
            TrackingId = c.TrackingId,
            Sequence = c.Sequence,
            Status = Enum.Parse<CdexAdditionalInformationStatus>(c.Status.ToString()),
            DueDate = c.DueDate,
            Notes = c.Notes,
            MemberId = c.MemberId,
            RequestingProviderNpi = c.RequestingProviderNpi,
            ReviewDecision = c.ReviewDecision,
            ReasonCode = c.ReasonCode,
            ReasonDescription = c.ReasonDescription,
            RequestedBy = c.RequestedBy,
            RequestSource = c.RequestSource,
            FirstDeliveredAt = c.FirstDeliveredAt,
            LastDeliveredAt = c.LastDeliveredAt,
            RespondedAt = c.RespondedAt,
            ClosedAt = c.ClosedAt,
            CreatedAt = c.CreatedAt,
            UpdatedAt = c.UpdatedAt,
            RequestedItems = c.RequestedItems.Select(i => new CdexRequestedItem
            {
                Code = i.Code,
                LoincCode = i.LoincCode,
                Description = i.Description,
                Required = i.Required,
                ServiceLineProcedureCode = i.ServiceLineProcedureCode,
                DiagnosisCode = i.DiagnosisCode,
            }).ToList(),
            ReceivedAttachments = c.ReceivedAttachments.Select(a => new CdexReceivedArtifact
            {
                SubmissionId = a.SubmissionId,
                ReceivedAt = a.ReceivedAt,
                AttachmentControlNumber = a.AttachmentControlNumber,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                Title = a.Title,
                DocumentTypeCode = a.DocumentTypeCode,
                DocumentTypeSystem = a.DocumentTypeSystem,
                FileHash = a.FileHash,
                Channel = a.Channel,
            }).ToList(),
        };
    }
}

/// <summary>
/// The in-process stand-in for authorization-service's HTTP hop to rfai-service.
/// Calls the REAL <see cref="RfaiCaseService"/>, so idempotency and the
/// one-open-cycle rule are production behaviour, not fixture behaviour.
/// </summary>
internal sealed class LocalRfaiRequestGateway : AuthorizationService.Services.Rfai.IRfaiRequestGateway
{
    private readonly IRfaiCaseService _cases;

    public LocalRfaiRequestGateway(IRfaiCaseService cases) => _cases = cases;

    /// <summary>Set to simulate rfai-service being unreachable.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Calls made, including ones that failed.</summary>
    public int Calls { get; private set; }

    public async Task<AuthorizationService.Services.Rfai.RfaiRequestHandle?> EnsureRequestAsync(
        AuthorizationService.Services.Rfai.RfaiRequestCommand command, CancellationToken ct = default)
    {
        Calls++;

        if (Unavailable)
            throw new HttpRequestException("rfai-service is unreachable.");

        var result = await _cases.EnsureRequestAsync(new RfaiCreationRequest
        {
            TenantId = command.TenantId,
            AuthNumber = command.AuthNumber,
            AuthorizationId = command.AuthorizationId,
            CorrelationKey = command.CorrelationKey,
            MemberId = command.MemberId,
            RequestingProviderNpi = command.RequestingProviderNpi,
            ReviewDecision = command.ReviewDecision,
            ReasonCode = command.ReasonCode,
            ReasonDescription = command.ReasonDescription,
            RequestedBy = command.RequestedBy,
            RequestSource = command.RequestSource,
            DueDate = command.DueDate,
            Notes = command.Notes,
            RequestedItems = command.RequestedItems.Select(i => new RequestedItem
            {
                Code = i.Code,
                LoincCode = i.LoincCode,
                Description = i.Description,
                Required = i.Required,
                ServiceLineProcedureCode = i.ServiceLineProcedureCode,
                DiagnosisCode = i.DiagnosisCode,
            }).ToList(),
        }, ct);

        return new AuthorizationService.Services.Rfai.RfaiRequestHandle
        {
            Id = result.Case.Id,
            TrackingId = result.Case.TrackingId,
            Created = result.Created,
        };
    }
}

/// <summary>
/// A content scanner that refuses everything, for the "rejected before anything
/// is stored" scenario.
/// </summary>
internal sealed class RejectingAttachmentContentScanner : IAttachmentContentScanner
{
    public Task<AttachmentScanResult> ScanAsync(
        ReadOnlyMemory<byte> content, string contentType, CancellationToken ct = default)
        => Task.FromResult(AttachmentScanResult.Rejected("test rejection"));
}

/// <summary>
/// Wires the whole additional-information round trip in process: the real RFAI
/// aggregate, the real CDex projection and submission service, the real
/// authorization-service coordinator and the real resume-review consumer.
/// </summary>
internal sealed class AdditionalInformationHarness
{
    public InMemoryRfaiRepository Repository { get; } = new();
    public RecordingKafkaProducer Kafka { get; } = new();
    public CloudHealthOffice.Infrastructure.Gateways.InMemoryClaimAttachmentContentStore Content { get; }
        = new(new CloudHealthOffice.Infrastructure.Gateways.ClaimAttachmentOptions
        {
            ContentContainer = CdexAttachmentPolicy.StorageContainer,
        });

    public IRfaiCaseService Cases { get; }
    public LocalCdexAdditionalInformationStore Store { get; }
    public LocalRfaiRequestGateway Gateway { get; }
    public CdexTaskMapper TaskMapper { get; } = new();

    public AdditionalInformationHarness()
    {
        Cases = new RfaiCaseService(
            Repository,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            AcceptanceContext.Logger<RfaiCaseService>(),
            Kafka);

        Store = new LocalCdexAdditionalInformationStore(Repository, Cases);
        Gateway = new LocalRfaiRequestGateway(Cases);
    }

    /// <summary>The real submission service, over the real aggregate and the shared content store.</summary>
    public CdexAttachmentSubmissionService Submissions(IAttachmentContentScanner? scanner = null)
        => new(Store, Content,
               scanner ?? new UnscannedAttachmentContentScanner(
                   AcceptanceContext.Logger<UnscannedAttachmentContentScanner>()),
               AcceptanceContext.Logger<CdexAttachmentSubmissionService>());

    /// <summary>The real coordinator that turns an A4 decision into a request.</summary>
    public AuthorizationService.Services.Rfai.PendedAuthorizationRfaiCoordinator Coordinator()
        => new(Gateway,
               AcceptanceContext.Logger<AuthorizationService.Services.Rfai.PendedAuthorizationRfaiCoordinator>());

    /// <summary>The real consumer that returns an authorization to review.</summary>
    public RfaiDocsReceivedConsumer ResumeConsumer(
        AuthorizationService.Repositories.IAuthorizationRepository repository)
        => (RfaiDocsReceivedConsumer)Activator.CreateInstance(
            typeof(RfaiDocsReceivedConsumer),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [repository, AcceptanceContext.Logger<RfaiDocsReceivedConsumer>()],
            culture: null)!;
}
