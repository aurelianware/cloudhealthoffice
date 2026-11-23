using FluentAssertions;
using FhirService.Services;
using FhirService.Services.Pas;
using Hl7.Fhir.Model;

namespace CloudHealthOffice.FhirService.Tests.Services;

/// <summary>
/// How CHO states a request it cannot process in PAS terms: X12 AAA codes on
/// <c>ClaimResponse.error</c>, with the element and the path into the request,
/// and the back-reference to a Claim it cannot name.
/// </summary>
public class PasRequestErrorsTests
{
    private const string Base = "http://example.org/fhir";
    private const string Dar = "http://hl7.org/fhir/StructureDefinition/data-absent-reason";

    [Theory]
    [InlineData("Patient.birthDate", "58", "2010C-DMG02", "Bundle.entry.resource.ofType(Patient).birthDate", null)]
    [InlineData("Claim.provider", "43", "2010B-NM109", "Bundle.entry.resource.ofType(Claim).provider", null)]
    [InlineData("Claim.item[2].quantity", "33", "2000F-HSD02", "Bundle.entry.resource.ofType(Claim).item.where(sequence = 2).quantity", 2)]
    [InlineData("Claim.diagnosis[1]", "AF", "2000E-HI01", "Bundle.entry.resource.ofType(Claim).diagnosis.where(sequence = 1)", null)]
    [InlineData("Something.else", "33", null, "Bundle.entry.resource.ofType(Something).else", null)]
    public void MappingErrors_BecomeX12Errors_WithAPathFromTheBundle(
        string expression, string code, string? element, string path, int? sequence)
    {
        var error = PasRequestErrors.FromMapping(new PasMappingError(expression, "message"));

        error.Code.Should().Be(code);
        error.X12Element.Should().Be(element);
        error.ErrorPath.Should().Be(path);
        error.ItemSequence.Should().Be(sequence);
        error.FollowUpAction.Should().Be("C");
    }

    private static (Claim, Bundle) Request(string? claimId = "claim-1")
    {
        var claim = new Claim
        {
            Id = claimId,
            Identifier = [new Identifier("http://example.org/trace", "111099")],
            Patient = new ResourceReference("Patient/p"),
            Provider = new ResourceReference("Organization/clinic"),
        };
        var bundle = new Bundle
        {
            Type = Bundle.BundleType.Collection,
            Entry =
            [
                new Bundle.EntryComponent { FullUrl = claimId is null ? "urn:uuid:4f1d" : $"{Base}/Claim/{claimId}", Resource = claim },
                new Bundle.EntryComponent { FullUrl = $"{Base}/Patient/p", Resource = new Patient { Id = "p" } },
                new Bundle.EntryComponent { FullUrl = $"{Base}/Organization/clinic", Resource = new Organization { Id = "clinic" } },
            ],
        };
        return (claim, bundle);
    }

    [Fact]
    public void ErrorResponse_HasErrorsAndNotes_ButNoNumberOrAdjudication()
    {
        var (claim, request) = Request();
        var errors = new[]
        {
            PasRequestErrors.FromMapping(new PasMappingError("Patient.birthDate", "Patient.birthDate must be a full date.")),
            PasRequestErrors.FromMapping(new PasMappingError("Claim.item[1].quantity", "Requested quantity must be positive.")),
        };

        var bundle = new PasResponseBuilder().BuildErrorResponse(claim, errors, request, Base);
        var response = (ClaimResponse)bundle.Entry[0].Resource;

        response.Outcome.Should().Be(ClaimProcessingCodes.Error);
        response.PreAuthRef.Should().BeNull();
        response.Item.Should().BeEmpty();
        response.Error.Should().HaveCount(2);
        response.ProcessNote.Select(n => n.Text).Should().Equal(errors.Select(e => e.Message));
        response.Error[1].ItemSequence.Should().Be(1);

        var element = response.Error[0].Extension.Single(e => e.Url.EndsWith("/extension-errorElement"));
        ((FhirString)element.Extension.Single(e => e.Url == "error").Value).Value.Should().Be("2010C-DMG02");
        ((PositiveInt)element.Extension.Single(e => e.Url == "processNote").Value).Value.Should().Be(1);
        ((CodeableConcept)response.Error[0].Extension.Single(e => e.Url.EndsWith("/extension-errorFollowupAction")).Value)
            .Coding.Single().Code.Should().Be("C");
        response.Request.Reference.Should().Be("Claim/claim-1");
    }

    [Fact]
    public void AClaimWithNoId_IsNamedByItsIdentifier_AndTheReferenceSaysWhyItIsAbsent()
    {
        var (claim, request) = Request(claimId: null);

        var response = (ClaimResponse)new PasResponseBuilder()
            .BuildPendedResponse(claim, "PAS-1", [], request, Base).Entry[0].Resource;

        response.Request.Reference.Should().BeNull("the response does not echo the Claim, so a urn:uuid would not resolve");
        response.Request.Identifier.Value.Should().Be("111099");
        ((Code)response.Request.Extension.Single(e => e.Url == Dar).Value).Value.Should().Be("unknown");
    }

    [Fact]
    public void UnmatchedInquiry_IsAnAaError_WhoseOriginalRequestIsUnknown()
    {
        var (_, request) = Request();

        var bundle = new PasResponseBuilder().BuildInquiryErrorResponse(request, PasRequestErrors.InquiryNotMatched(), Base);
        var response = (ClaimResponse)bundle.Entry[0].Resource;

        response.Outcome.Should().Be(ClaimProcessingCodes.Error);
        response.PreAuthRef.Should().BeNull();
        response.Error.Single().Code.Coding.Single().Code.Should().Be("AA");
        ((Code)response.Request.Extension.Single(e => e.Url == Dar).Value).Value.Should().Be("unknown");
        response.Patient.Reference.Should().Be("Patient/p");
        bundle.Entry.Select(e => e.Resource.TypeName).Should().Contain("Patient");
    }
}
