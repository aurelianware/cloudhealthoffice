using AuthorizationService.Models;

namespace AuthorizationService.Services.BenefitExclusion;

/// <summary>
/// The authorization-workflow entry point for drug/service exclusion: resolves
/// the applicable benefit plan's exclusions for a request and evaluates them.
/// The CHO-native backend calls this before persisting a submission so an
/// excluded request never follows the ordinary approvable path.
/// </summary>
public interface IAuthorizationExclusionService
{
    BenefitExclusionDetermination DetermineExclusion(Authorization authorization);
}

public sealed class AuthorizationExclusionService : IAuthorizationExclusionService
{
    private readonly IBenefitExclusionCatalog _catalog;
    private readonly IDrugExclusionEvaluator _evaluator;

    public AuthorizationExclusionService(
        IBenefitExclusionCatalog catalog, IDrugExclusionEvaluator evaluator)
    {
        _catalog = catalog;
        _evaluator = evaluator;
    }

    public BenefitExclusionDetermination DetermineExclusion(Authorization authorization)
    {
        var exclusions = _catalog.ResolveExclusions(authorization);
        return _evaluator.Evaluate(authorization, exclusions);
    }
}
