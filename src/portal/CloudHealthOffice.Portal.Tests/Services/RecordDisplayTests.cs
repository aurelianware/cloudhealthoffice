using System.Text.Json;
using CloudHealthOffice.Portal.Services;

namespace CloudHealthOffice.Portal.Tests.Services;

/// <summary>Display mappings the record pages use for data loaded from a core.</summary>
public class RecordDisplayTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("1", "Medical Care (1)")]
    [InlineData("pt", "Physical Therapy (pt)")]
    [InlineData("Surgery", "Surgery")]
    [InlineData("ZZ", "ZZ")]
    [InlineData(null, "")]
    public void ServiceTypeCodes_ShowTheirX12Name(string? code, string expected) =>
        X12ServiceTypes.Describe(code).Should().Be(expected);

    [Fact]
    public void AnAuthorization_ShowsItsRequestingProviderNpi()
    {
        var details = JsonSerializer.Deserialize<AuthorizationDetails>(
            """{ "authorizationNumber": "TXPA-1", "requestingProviderNPI": "1000020010", "serviceTypeCode": "1" }""", Web)!;

        details.ProviderId.Should().Be("1000020010");
        details.ServiceType.Should().Be("Medical Care (1)");
    }

    [Fact]
    public void APharmacyClaimType_ReadsAsPharmacy()
    {
        var claim = JsonSerializer.Deserialize<ClaimSummary>("""{ "claimType": 4 }""", Web)!;
        claim.ClaimType.Should().Be("Pharmacy");
    }

    [Fact]
    public void AClaimHeldForProviderScreening_ExplainsItsDifference_AndNotesTheHold()
    {
        var held = new ShadowResult
        {
            Kind = ShadowKinds.AllowedVariance,
            ChoPendCode = ShadowKinds.ProviderScreeningCode,
            ChoPendReason = "Billing provider: Provider verification could not reach a confident determination",
            ChoStageFindings = ["ProviderIntegrity: Billing provider: Provider verification could not reach a confident determination"],
        };
        ShadowKinds.Why(held).Should()
            .Be("The allowed amounts differ: CHO priced the lines from the plan's fee schedule. Held for provider screening.");

        held.ChoStageFindings.Add("NcciEdits: Bundled with 97530");
        ShadowKinds.Why(held).Should().Be("NcciEdits: Bundled with 97530 Held for provider screening.");

        var pended = new ShadowResult { Kind = ShadowKinds.ChoUndecided, ChoPendCode = "MEDREVIEW", ChoPendReason = "Needs review" };
        ShadowKinds.Why(pended).Should().Be("Needs review");
    }
}
