using CloudHealthOffice.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartAuthService.Models;
using SmartAuthService.Services;

namespace SmartAuthService.Controllers.Admin;

/// <summary>
/// Identity bindings grant a person access to a member's records, so they are
/// made by people: a CHO service token, which the shared permission layer lets
/// satisfy any tenant permission, is refused here.
/// </summary>
public sealed class HumanActorOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var actor = context.HttpContext.RequestServices.GetRequiredService<ICurrentActor>();
        if (actor.IsService)
        {
            context.Result = new ObjectResult(new { error = "user_token_required" }) { StatusCode = 403 };
        }
    }
}

public sealed record MemberEnrolmentRequest(string? MemberId);
public sealed record ProviderEnrolmentRequest(string? ProviderId, string? Npi);

/// <summary>An enrolment as the admin API shows it. The code is returned once, at creation.</summary>
public sealed record EnrolmentView(
    string Id, string Kind, string TenantId, string? MemberId, string? ProviderId, string? Npi,
    string Status, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string? RedeemedBy, DateTimeOffset? RedeemedAt, string? Code = null)
{
    public static EnrolmentView From(Enrolment e, string? code = null) => new(
        e.Id, e.Kind, e.TenantId, e.MemberId, e.ProviderId, e.Npi, e.Status, e.CreatedBy, e.CreatedAt,
        e.ExpiresAt, e.RedeemedBy, e.RedeemedAt, code);
}

/// <summary>
/// Member identity bindings for SMART Patient Access. Tenant from the CHO
/// token; actor from the CHO token. A member's identity is bound only when the
/// member, signed in, redeems the single-use code issued here
/// (<c>/account/link</c>); this API never binds an identity it was merely told about.
/// </summary>
[ApiController]
[Route("api/admin/smart")]
[RequirePermission("members:write")]
[HumanActorOnly]
public sealed class SmartMemberBindingsController : ControllerBase
{
    private readonly ISmartIdentityStore _store;
    private readonly ICurrentActor _actor;
    private readonly SmartAuthAudit _audit;
    private readonly IConfiguration _config;

    public SmartMemberBindingsController(
        ISmartIdentityStore store, ICurrentActor actor, SmartAuthAudit audit, IConfiguration config)
    {
        _store = store;
        _actor = actor;
        _audit = audit;
        _config = config;
    }

    [HttpPost("member-enrolments")]
    public async Task<IActionResult> CreateEnrolment([FromBody] MemberEnrolmentRequest request, CancellationToken ct)
    {
        if (!SmartIdentifiers.IsFhirId(request.MemberId))
            return BadRequest(new { error = "memberId must be a FHIR id ([A-Za-z0-9-.]{1,64})." });

        var (enrolment, code) = SmartEnrolments.New(
            EnrolmentKind.Member, _actor.TenantId, _actor.UserId, _config,
            memberId: request.MemberId);
        await _store.CreateEnrolmentAsync(enrolment, ct);
        _audit.Admin("member-enrolment-issued", _actor.TenantId, _actor.UserId, $"enrolment={enrolment.Id} member={enrolment.MemberId}");
        return StatusCode(201, EnrolmentView.From(enrolment, code));
    }

    [HttpGet("member-enrolments")]
    public async Task<IActionResult> ListEnrolments(CancellationToken ct)
        => Ok((await _store.ListEnrolmentsAsync(_actor.TenantId, EnrolmentKind.Member, ct)).Select(e => EnrolmentView.From(e)));

    [HttpDelete("member-enrolments/{id}")]
    public async Task<IActionResult> CancelEnrolment(string id, CancellationToken ct)
    {
        if (!await _store.CancelEnrolmentAsync(_actor.TenantId, EnrolmentKind.Member, id, _actor.UserId, ct))
            return NotFound();
        _audit.Admin("member-enrolment-cancelled", _actor.TenantId, _actor.UserId, $"enrolment={id}");
        return NoContent();
    }

    [HttpGet("member-links")]
    public async Task<IActionResult> ListLinks(CancellationToken ct)
        => Ok(await _store.ListMemberLinksAsync(_actor.TenantId, ct));

    [HttpDelete("member-links/{id}")]
    public async Task<IActionResult> RevokeLink(string id, CancellationToken ct)
    {
        if (!await _store.RevokeMemberLinkAsync(_actor.TenantId, id, _actor.UserId, ct))
            return NotFound();
        _audit.Admin("member-link-revoked", _actor.TenantId, _actor.UserId, $"link={id}");
        return NoContent();
    }
}

/// <summary>Provider-user identity bindings for SMART Provider Access. Same rules as members.</summary>
[ApiController]
[Route("api/admin/smart")]
[RequirePermission("providers:write")]
[HumanActorOnly]
public sealed class SmartProviderBindingsController : ControllerBase
{
    private readonly ISmartIdentityStore _store;
    private readonly ICurrentActor _actor;
    private readonly SmartAuthAudit _audit;
    private readonly IConfiguration _config;

    public SmartProviderBindingsController(
        ISmartIdentityStore store, ICurrentActor actor, SmartAuthAudit audit, IConfiguration config)
    {
        _store = store;
        _actor = actor;
        _audit = audit;
        _config = config;
    }

    [HttpPost("provider-enrolments")]
    public async Task<IActionResult> CreateEnrolment([FromBody] ProviderEnrolmentRequest request, CancellationToken ct)
    {
        if (!SmartIdentifiers.IsFhirId(request.ProviderId))
            return BadRequest(new { error = "providerId must be a FHIR id ([A-Za-z0-9-.]{1,64})." });
        if (!SmartIdentifiers.IsNpi(request.Npi))
            return BadRequest(new { error = "npi must be a valid 10-digit NPI." });

        var (enrolment, code) = SmartEnrolments.New(
            EnrolmentKind.Provider, _actor.TenantId, _actor.UserId, _config,
            providerId: request.ProviderId, npi: request.Npi);
        await _store.CreateEnrolmentAsync(enrolment, ct);
        _audit.Admin("provider-enrolment-issued", _actor.TenantId, _actor.UserId,
            $"enrolment={enrolment.Id} provider={enrolment.ProviderId} npi={enrolment.Npi}");
        return StatusCode(201, EnrolmentView.From(enrolment, code));
    }

    [HttpGet("provider-enrolments")]
    public async Task<IActionResult> ListEnrolments(CancellationToken ct)
        => Ok((await _store.ListEnrolmentsAsync(_actor.TenantId, EnrolmentKind.Provider, ct)).Select(e => EnrolmentView.From(e)));

    [HttpDelete("provider-enrolments/{id}")]
    public async Task<IActionResult> CancelEnrolment(string id, CancellationToken ct)
    {
        if (!await _store.CancelEnrolmentAsync(_actor.TenantId, EnrolmentKind.Provider, id, _actor.UserId, ct))
            return NotFound();
        _audit.Admin("provider-enrolment-cancelled", _actor.TenantId, _actor.UserId, $"enrolment={id}");
        return NoContent();
    }

    [HttpGet("provider-users")]
    public async Task<IActionResult> ListLinks(CancellationToken ct)
        => Ok(await _store.ListProviderLinksAsync(_actor.TenantId, ct));

    [HttpDelete("provider-users/{id}")]
    public async Task<IActionResult> RevokeLink(string id, CancellationToken ct)
    {
        if (!await _store.RevokeProviderLinkAsync(_actor.TenantId, id, _actor.UserId, ct))
            return NotFound();
        _audit.Admin("provider-user-revoked", _actor.TenantId, _actor.UserId, $"link={id}");
        return NoContent();
    }
}

internal static class SmartEnrolments
{
    public static (Enrolment Enrolment, string Code) New(
        string kind, string tenantId, string actor, IConfiguration config,
        string? memberId = null, string? providerId = null, string? npi = null)
    {
        var ttl = TimeSpan.FromHours(Math.Clamp(config.GetValue("SmartAuth:EnrolmentCodeTtlHours", 72), 1, 24 * 30));
        var code = SmartIdentifiers.NewEnrolmentCode();
        var now = DateTimeOffset.UtcNow;
        return (new Enrolment
        {
            Id = MongoSmartIdentityStore.NewId(),
            Kind = kind,
            TenantId = tenantId,
            MemberId = memberId,
            ProviderId = providerId,
            Npi = npi,
            CodeHash = SmartIdentifiers.HashCode(code),
            CreatedBy = actor,
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
        }, code);
    }
}
