using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationService.Models;
using AuthorizationService.Services.ConcurrentReview;

namespace AuthorizationService.Services.CareManagement;

/// <summary>
/// Opens transitions-of-care cases against care-management-service over its
/// internal API.
///
/// authorization-service does not own the case and keeps no copy of it: it
/// sends the referral and keeps the case id that comes back. Every call
/// carries the stay's OWN tenant explicitly, so the case lands in the same
/// partition as the stay that caused it. Modelled on the RFAI gateway.
/// </summary>
public sealed class HttpCareReferralGateway(
    IHttpClientFactory factory, ILogger<HttpCareReferralGateway> logger) : ICareReferralGateway
{
    public const string HttpClientName = "CareManagementService";

    /// <summary>The header care-management-service's tenant middleware reads.</summary>
    private const string TenantHeader = "X-Tenant-ID";

    private static readonly JsonSerializerOptions WireFormat = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<string?> ReferOnDischargeAsync(
        CareReferralCommand command, CancellationToken ct = default)
    {
        var payload = new
        {
            memberId = command.MemberId,
            memberFirstName = command.MemberFirstName,
            memberLastName = command.MemberLastName,
            program = "TransitionsOfCare",
            acuity = command.Acuity,
            referralSource = "InpatientDischarge",
            referralReference = command.StayId,
            referralReason = Describe(command),
            // Transitions-of-care outreach is conventionally within two days
            // of discharge, which is the window readmissions come from.
            nextContactDueDate = command.DischargeDate.Date.AddDays(2),
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/care-cases")
            {
                Content = JsonContent.Create(payload, options: WireFormat),
            };
            request.Headers.TryAddWithoutValidation(TenantHeader, command.TenantId);

            using var response = await factory.CreateClient(HttpClientName).SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                // Status category only — never the body, which echoes the request.
                logger.LogWarning(
                    "care-management-service returned {Status} for a transitions-of-care referral; " +
                    "the discharge is recorded and no case was opened.",
                    (int)response.StatusCode);
                return null;
            }

            var created = await response.Content.ReadFromJsonAsync<CaseCreated>(WireFormat, ct);
            return created?.Id;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A care management service that is down must not stop a UM nurse
            // closing a stay.
            logger.LogWarning(ex,
                "Could not refer a discharged member to care management; the discharge is recorded.");
            return null;
        }
    }

    private static string Describe(CareReferralCommand command)
    {
        var parts = new List<string>
        {
            $"Discharged {command.DischargeDate:yyyy-MM-dd} after {command.LengthOfStayDays} day(s)",
        };
        if (command.FacilityName is { Length: > 0 } facility) parts.Add($"at {facility}");
        if (command.PrincipalDiagnosisCode is { Length: > 0 } dx) parts.Add($"principal diagnosis {dx}");
        if (command.DischargeDisposition is { Length: > 0 } disposition) parts.Add($"disposition {disposition}");
        return string.Join("; ", parts) + ".";
    }

    private sealed record CaseCreated([property: JsonPropertyName("id")] string? Id);
}

/// <summary>
/// Refers a discharged member to care management when the discharge warrants
/// it, and records the case on the stay.
/// </summary>
public sealed class DischargeCareReferral(ICareReferralGateway gateway, ILogger<DischargeCareReferral> logger)
{
    /// <summary>
    /// Called after a discharge has been persisted. Never throws: the
    /// discharge already happened, and a referral failure cannot be allowed
    /// to look like a failed discharge.
    /// </summary>
    public async Task<string?> ReferIfWarrantedAsync(InpatientStay stay, CancellationToken ct = default)
    {
        if (stay.Status != InpatientStayStatus.Discharged || stay.DischargeDate is not { } discharged)
            return null;

        var lengthOfStay = StayReviewPolicy.ElapsedDays(stay, discharged);

        if (!CareReferralPolicy.ShouldRefer(stay, lengthOfStay)) return null;

        try
        {
            return await gateway.ReferOnDischargeAsync(new CareReferralCommand
            {
                TenantId               = stay.TenantId,
                MemberId               = stay.MemberId,
                MemberFirstName        = stay.MemberFirstName,
                MemberLastName         = stay.MemberLastName,
                StayId                 = stay.Id,
                AdmissionDate          = stay.AdmissionDate,
                DischargeDate          = discharged,
                DischargeDisposition   = stay.DischargeDisposition,
                PrincipalDiagnosisCode = stay.PrincipalDiagnosisCode,
                FacilityName           = stay.FacilityName,
                LengthOfStayDays       = lengthOfStay,
                Acuity                 = CareReferralPolicy.AcuityFor(stay, lengthOfStay),
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transitions-of-care referral failed for stay {StayId}.", stay.Id);
            return null;
        }
    }
}
