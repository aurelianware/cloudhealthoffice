using AppealsService.Middleware;
using AppealsService.Models;
using AppealsService.Services;
using AppealsService.Services.RulePacks;
using Microsoft.AspNetCore.Mvc;

namespace AppealsService.Controllers;

/// <summary>
/// Read-only view of the State appeal and grievance rule packs
/// (docs/architecture/appeals-case-model.md §5). Nothing here changes an
/// appeal: the deadlines endpoint answers "what would this State require
/// for a case received at this time", computed with the tenant's holiday
/// calendar and clamped to the federal ceiling.
///
/// The literal <c>rule-packs</c> segment outranks <c>api/appeals/{id}</c>
/// in routing, so these routes never reach the appeal lookup. GET only, so
/// the default convention requires <c>appeals:read</c>.
/// </summary>
[ApiController]
[Route("api/appeals/rule-packs")]
[Produces("application/json")]
public class RulePacksController : ControllerBase
{
    private readonly IRulePackCatalog _catalog;
    private readonly IAppealHolidayCalendarProvider _holidays;

    public RulePacksController(IRulePackCatalog catalog, IAppealHolidayCalendarProvider holidays)
    {
        _catalog = catalog;
        _holidays = holidays;
    }

    /// <summary>Summary of every pack this service carries.</summary>
    [HttpGet]
    public ActionResult<IEnumerable<RulePackSummary>> List() =>
        Ok(_catalog.All.Select(RulePackSummary.From));

    /// <summary>The full pack in effect for a State on a date (default today, UTC).</summary>
    [HttpGet("{state}")]
    public ActionResult<RulePack> Get(
        string state,
        [FromQuery] LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid,
        [FromQuery] DateOnly? asOf = null)
    {
        var pack = _catalog.Find(state, lineOfBusiness, asOf ?? Today);
        return pack is null ? NotFoundPack(state, lineOfBusiness) : Ok(pack);
    }

    /// <summary>Deadlines the State's pack gives for a case, with the rule and citation behind each.</summary>
    [HttpGet("{state}/deadlines")]
    public ActionResult<RulePackEvaluation> Deadlines(
        string state,
        [FromQuery] DateTime receivedAt,
        [FromQuery] LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid,
        [FromQuery] RulePackCaseType caseType = RulePackCaseType.MemberAppeal,
        [FromQuery] bool expedited = false,
        [FromQuery] DateTime? informationReceivedAt = null,
        [FromQuery] DateTime? decidedAt = null,
        [FromQuery] DateTime? noticeSentAt = null,
        [FromQuery] DateOnly? asOf = null)
    {
        if (receivedAt == default)
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "receivedAt is required.");

        var received = Utc(receivedAt);
        var pack = _catalog.Find(state, lineOfBusiness, asOf ?? DateOnly.FromDateTime(received));
        if (pack is null)
            return NotFoundPack(state, lineOfBusiness);

        var input = new RulePackEvaluationInput
        {
            CaseType = caseType,
            Expedited = expedited,
            ReceivedAt = received,
            InformationReceivedAt = informationReceivedAt is { } i ? Utc(i) : null,
            DecidedAt = decidedAt is { } d ? Utc(d) : null,
            NoticeSentAt = noticeSentAt is { } n ? Utc(n) : null
        };

        return Ok(RulePackEvaluator.Evaluate(pack, input, _holidays.ForTenant(HttpContext.GetTenantId())));
    }

    /// <summary>The recoupment rule in effect for a State on a date.</summary>
    [HttpGet("{state}/recoupment")]
    public ActionResult<RecoupmentRule> Recoupment(
        string state,
        [FromQuery] LineOfBusiness lineOfBusiness = LineOfBusiness.Medicaid,
        [FromQuery] DateOnly? asOf = null)
    {
        var date = asOf ?? Today;
        var pack = _catalog.Find(state, lineOfBusiness, date);
        if (pack is null)
            return NotFoundPack(state, lineOfBusiness);

        var rule = RulePackEvaluator.RecoupmentAsOf(pack, date);
        return rule is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: $"No recoupment rule in effect for {pack.State} on {date:yyyy-MM-dd}.")
            : Ok(rule);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // Model binding gives Utc for "Z"/offset values and Unspecified for bare
    // ones; both are read as UTC, never local time.
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private ObjectResult NotFoundPack(string state, LineOfBusiness lob) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: $"No {lob} rule pack for State '{state}'.");
}

public sealed record RulePackSummary(
    string Id,
    string State,
    LineOfBusiness LineOfBusiness,
    string Program,
    string Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    DateOnly VerifiedOn,
    int ConflictCount,
    int ToConfirmCount)
{
    public static RulePackSummary From(RulePack p) => new(
        p.Id, p.State, p.LineOfBusiness, p.Program, p.Version, p.EffectiveFrom, p.EffectiveTo, p.VerifiedOn,
        p.Conflicts.Count, p.ToConfirm.Count);
}
