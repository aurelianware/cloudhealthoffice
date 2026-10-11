using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationService.Backends;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.OperatingMode;
using FhirService.Services;
using FhirService.Services.Pas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StoredAuthorization = AuthorizationService.Models.Authorization;

namespace Cms0057Acceptance.Tests.TestSupport;

/// <summary>
/// authorization-service, in process, for the PAS scenarios: fhir-service's
/// recorder and inquiry store are wired to the REAL
/// <see cref="AutomatedDecisionsController"/> and
/// <see cref="ChoAuthorizationBackend"/> over the in-memory repository
/// fixture, with every hop serialised in the wire format the HTTP path uses.
/// What is not exercised here is HTTP itself and token authentication: those
/// are proven by fhir-service's PasSubmitPersistenceSeamTests (both real
/// hosts) and authorization-service's AutomatedDecisionEndpointTests.
/// </summary>
internal sealed class InProcessAuthorizationService : IPasAuthorizationRecorder, IPriorAuthorizationStore
{
    /// <summary>authorization-service's MVC wire format: declared enum names, no integers.</summary>
    private static readonly JsonSerializerOptions AuthorizationServiceWire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) },
    };

    private readonly AuthorizationBackendSelector _selector;

    public InProcessAuthorizationService()
    {
        Backend = new ChoAuthorizationBackend(Repository);
        _selector = new AuthorizationBackendSelector(
            new IAuthorizationBackend[] { Backend },
            Options.Create(new AuthorizationBackendOptions { OperatingMode = EngineOperatingMode.Replace }));
    }

    public InMemoryAuthorizationRepository Repository { get; } = new();

    public ChoAuthorizationBackend Backend { get; }

    /// <summary>Fail every write as unavailable (authorization-service down).</summary>
    public bool Unavailable { get; set; }

    public async Task<PasRecordOutcome> RecordAsync(
        PasAuthorizationRecordRequest request, string tenantId, CancellationToken ct = default)
    {
        if (Unavailable)
            return PasRecordOutcome.Failed(PasRecordFailure.Unavailable);

        var body = JsonSerializer.Deserialize<AutomatedDecisionRecordRequest>(
            JsonSerializer.Serialize(request, HttpPriorAuthorizationStore.WireFormat), AuthorizationServiceWire)!;
        if (!Validator.TryValidateObject(body, new ValidationContext(body), null, validateAllProperties: true))
            return PasRecordOutcome.Failed(PasRecordFailure.Rejected);

        var controller = new AutomatedDecisionsController(
            _selector, new ServiceActor(tenantId), AcceptanceContext.Logger<AutomatedDecisionsController>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.Record(body);
        if (result.Result is not ObjectResult { StatusCode: StatusCodes.Status201Created, Value: StoredAuthorization stored })
            return PasRecordOutcome.Failed(PasRecordFailure.Rejected);

        return new PasRecordOutcome
        {
            Stored = JsonSerializer.Deserialize<PasStoredAuthorization>(
                JsonSerializer.Serialize(stored, AuthorizationServiceWire), HttpPriorAuthorizationStore.WireFormat),
        };
    }

    public async Task<PriorAuthorizationRecord?> GetByAuthorizationNumberAsync(
        string authorizationNumber, CancellationToken ct = default)
    {
        var stored = await Backend.GetByNumberAsync(authorizationNumber, ct);
        return stored is null
            ? null
            : JsonSerializer.Deserialize<PriorAuthorizationRecord>(
                JsonSerializer.Serialize(stored, AuthorizationServiceWire), HttpPriorAuthorizationStore.WireFormat);
    }

    /// <summary>fhir-service's service identity, as its token would present it.</summary>
    private sealed class ServiceActor(string tenantId) : ICurrentActor
    {
        public bool IsAuthenticated => true;
        public string UserId => "fhir-service";
        public string? DisplayName => null;
        public string? Email => null;
        public string TenantId => tenantId;
        public bool IsService => true;
        public IReadOnlyCollection<string> Roles => [ChoServiceRole.Name];
        public bool HasPermission(string permission) => !ChoRolePermissions.IsReserved(permission);
    }
}
