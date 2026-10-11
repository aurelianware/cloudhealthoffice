using FhirService.Services;
using FhirService.Services.Pas;

namespace CloudHealthOffice.FhirService.Tests.TestSupport;

/// <summary>
/// Stands in for authorization-service in controller unit tests: records what
/// <c>$submit</c> asked to store and answers as a successful store would (the
/// stored status follows the decision), or fails as configured.
/// </summary>
internal sealed class FakePasAuthorizationRecorder : IPasAuthorizationRecorder
{
    public List<(PasAuthorizationRecordRequest Request, string TenantId)> Calls { get; } = [];

    /// <summary>Fail every call this way (None = succeed).</summary>
    public PasRecordFailure Failure { get; set; } = PasRecordFailure.None;

    /// <summary>Store a different status than the decision (a plan exclusion).</summary>
    public PasStoredAuthorization? StoredOverride { get; set; }

    public PasAuthorizationRecordRequest? Last => Calls.Count == 0 ? null : Calls[^1].Request;

    public Task<PasRecordOutcome> RecordAsync(
        PasAuthorizationRecordRequest request, string tenantId, CancellationToken ct = default)
    {
        Calls.Add((request, tenantId));
        if (Failure != PasRecordFailure.None)
            return Task.FromResult(PasRecordOutcome.Failed(Failure));

        var stored = StoredOverride is { } o
            ? o with { TenantId = tenantId, AuthorizationNumber = request.AuthorizationNumber }
            : new PasStoredAuthorization
            {
                TenantId = tenantId,
                AuthorizationNumber = request.AuthorizationNumber,
                Status = request.Decision?.Outcome switch
                {
                    "Approved" => PriorAuthorizationStatus.Approved,
                    "Denied" => PriorAuthorizationStatus.Denied,
                    _ => PriorAuthorizationStatus.Pended,
                },
                DenialReasonCode = request.Decision?.DenialReasonCode,
                DenialReason = request.Decision?.DenialReason,
                ApprovedServiceDateFrom = request.Decision?.ApprovedServiceDateFrom,
                ApprovedServiceDateTo = request.Decision?.ApprovedServiceDateTo,
                RequestHash = request.RequestHash,
            };
        return Task.FromResult(new PasRecordOutcome { Stored = stored });
    }
}
