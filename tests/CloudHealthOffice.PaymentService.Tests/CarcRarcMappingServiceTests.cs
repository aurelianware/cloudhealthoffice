using Microsoft.Extensions.Logging.Abstractions;
using PaymentService.Services;
using Xunit;

namespace CloudHealthOffice.PaymentService.Tests;

public class CarcRarcMappingServiceTests
{
    private readonly CarcRarcMappingService _mapper = new(NullLogger<CarcRarcMappingService>.Instance);

    [Fact]
    public void MapClaimAdjustments_StandardCostShare_EmitsAllReasons()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            AdjustmentReasons =
            {
                new ClaimAdjustmentReasonView { GroupCode = "PR", ReasonCode = "1", Amount = 500m, Description = "Deductible" },
                new ClaimAdjustmentReasonView { GroupCode = "PR", ReasonCode = "2", Amount = 80m, Description = "Coinsurance" },
                new ClaimAdjustmentReasonView { GroupCode = "CO", ReasonCode = "45", Amount = 250m, Description = "Contractual" },
            }
        };

        var output = _mapper.MapClaimAdjustments(snapshot);

        Assert.Equal(3, output.Count);
        Assert.Contains(output, a => a.GroupCode == "PR" && a.ReasonCode == "1" && a.Amount == 500m);
        Assert.Contains(output, a => a.GroupCode == "PR" && a.ReasonCode == "2" && a.Amount == 80m);
        Assert.Contains(output, a => a.GroupCode == "CO" && a.ReasonCode == "45" && a.Amount == 250m);
    }

    [Fact]
    public void MapClaimAdjustments_DenialReason_AppendsCoEntry()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            DenialReasonCode = "29",
            DenialReason = "Time limit for filing has expired"
        };

        var output = _mapper.MapClaimAdjustments(snapshot);

        var denial = Assert.Single(output);
        Assert.Equal("CO", denial.GroupCode);
        Assert.Equal("29", denial.ReasonCode);
        Assert.Equal("Time limit for filing has expired", denial.ReasonDescription);
    }

    [Fact]
    public void MapClaimAdjustments_DenialAlreadyInAdjustments_NoDoubleEmit()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            DenialReasonCode = "45",
            AdjustmentReasons =
            {
                new ClaimAdjustmentReasonView { GroupCode = "CO", ReasonCode = "45", Amount = 250m }
            }
        };

        var output = _mapper.MapClaimAdjustments(snapshot);

        var only = Assert.Single(output);
        Assert.Equal("CO", only.GroupCode);
        Assert.Equal("45", only.ReasonCode);
        Assert.Equal(250m, only.Amount);
    }

    [Fact]
    public void MapLineAdjustments_EditFailureWithSuggestedCarc_UsesSuggested()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            EditFailures =
            {
                new EditFailureView
                {
                    EditType = "NCCI_PAIR",
                    RuleId = "NE001",
                    SuggestedCarc = "236",
                    SuggestedRarc = "M86",
                    AffectedLineNumbers = { 2, 3 },
                    Message = "Bundled procedure"
                }
            }
        };

        var output = _mapper.MapLineAdjustments(snapshot);

        Assert.Equal(2, output.Count);
        Assert.Equal("236", output[2][0].ReasonCode);
        Assert.Equal("M86", output[2][0].RemarkCode);
        Assert.Equal("236", output[3][0].ReasonCode);
    }

    [Fact]
    public void MapLineAdjustments_NullSuggestedCarc_FallsBackTo237()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            EditFailures =
            {
                new EditFailureView
                {
                    EditType = "MUE",
                    RuleId = "NE002",
                    SuggestedCarc = null,
                    SuggestedRarc = null,
                    AffectedLineNumbers = { 1 }
                }
            }
        };

        var output = _mapper.MapLineAdjustments(snapshot);

        Assert.Equal(CarcRarcMappingService.FallbackCarc, output[1][0].ReasonCode);
        Assert.Null(output[1][0].RemarkCode);
    }

    [Fact]
    public void MapLineAdjustments_MultipleFailuresOnSameLine_AppendsBoth()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            EditFailures =
            {
                new EditFailureView { RuleId = "NE001", SuggestedCarc = "236", AffectedLineNumbers = { 1 } },
                new EditFailureView { RuleId = "NE002", SuggestedCarc = "151", AffectedLineNumbers = { 1 } }
            }
        };

        var output = _mapper.MapLineAdjustments(snapshot);

        Assert.Equal(2, output[1].Count);
        Assert.Contains(output[1], a => a.ReasonCode == "236");
        Assert.Contains(output[1], a => a.ReasonCode == "151");
    }

    [Fact]
    public void MapLineAdjustments_NoFailures_ReturnsEmptyMap()
    {
        var snapshot = new ClaimAdjudicationSnapshot { ClaimId = "c1" };
        var output = _mapper.MapLineAdjustments(snapshot);
        Assert.Empty(output);
    }

    [Fact]
    public void MapLineAdjustments_EmptyAffectedLineNumbers_NoEmissions()
    {
        var snapshot = new ClaimAdjudicationSnapshot
        {
            ClaimId = "c1",
            EditFailures =
            {
                new EditFailureView { RuleId = "NE001", SuggestedCarc = "236", AffectedLineNumbers = new List<int>() }
            }
        };

        var output = _mapper.MapLineAdjustments(snapshot);

        Assert.Empty(output);
    }

    [Fact]
    public void MapClaimAdjustments_NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _mapper.MapClaimAdjustments(null!));
    }

    [Fact]
    public void MapLineAdjustments_NullSnapshot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => _mapper.MapLineAdjustments(null!));
    }
}
