using CloudHealthOffice.RiskAdjustmentEngine.Domain;

namespace CloudHealthOffice.RiskAdjustmentEngine.Services;

/// <summary>
/// Pipeline: map → resolve hierarchy → calculate score.
/// </summary>
public class RiskAdjustmentEngine : IRiskAdjustmentEngine
{
    private readonly IIcdToHccMapper _mapper;
    private readonly IHccHierarchyResolver _resolver;
    private readonly IRiskScoreCalculator _calculator;

    public RiskAdjustmentEngine(
        IIcdToHccMapper mapper,
        IHccHierarchyResolver resolver,
        IRiskScoreCalculator calculator)
    {
        _mapper     = mapper;
        _resolver   = resolver;
        _calculator = calculator;
    }

    public RiskScoreResult ComputeRiskScore(RiskScoreInput input)
    {
        // Step 1 — map ICD-10 codes to HCC categories
        var diagMap = _mapper.MapAll(input.DiagnosisCodes, input.Model);

        // Step 2 — collect unique mapped HCC codes
        var mappedHccs = diagMap.Values
            .Where(h => h.HasValue)
            .Select(h => h!.Value)
            .ToHashSet();

        // Step 3 — apply hierarchy rules
        var hierarchyResult = _resolver.Resolve(mappedHccs, input.Model);

        // Step 4 — compute risk score
        return _calculator.Calculate(input, diagMap, hierarchyResult);
    }

    public IReadOnlyList<RiskScoreResult> ComputeRiskScores(IReadOnlyList<RiskScoreInput> inputs) =>
        inputs.Select(ComputeRiskScore).ToList();
}
