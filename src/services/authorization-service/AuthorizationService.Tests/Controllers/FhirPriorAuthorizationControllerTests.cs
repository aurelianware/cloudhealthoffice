using System.Text.Json.Nodes;
using AuthorizationService.Controllers;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.Fhir;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace AuthorizationService.Tests.Controllers;

/// <summary>
/// The CMS-0057-F PA-data endpoint. What these pin is the part that matters for
/// disclosure: a search never returns another member's record, a drug
/// authorization is invisible rather than refused, and a read of something that
/// is not in scope is indistinguishable from a read of something absent.
/// </summary>
public class FhirPriorAuthorizationControllerTests
{
    private readonly Mock<IAuthorizationRepository> _repository = new();
    private readonly FhirPriorAuthorizationController _controller;

    public FhirPriorAuthorizationControllerTests()
    {
        _controller = new FhirPriorAuthorizationController(
            _repository.Object,
            new PriorAuthorizationEobProjector(),
            Mock.Of<ILogger<FhirPriorAuthorizationController>>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static Authorization Auth(
        string memberId,
        string number,
        string? serviceTypeCode = "2",
        string procedureCode = "97110",
        string? procedureSystem = "cpt",
        DateTime? submitted = null)
        => new()
        {
            TenantId = "tenant-1",
            MemberId = memberId,
            AuthorizationNumber = number,
            ServiceTypeCode = serviceTypeCode,
            SubmittedDate = submitted ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = AuthorizationStatus.Approved,
            RequestedServices =
            [
                new RequestedService
                {
                    ProcedureCode = procedureCode,
                    ProductOrServiceSystem = procedureSystem,
                    RequestedUnits = 1,
                },
            ],
        };

    private static JsonObject Body(IActionResult result)
    {
        var content = result.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        return JsonNode.Parse(content.Content!)!.AsObject();
    }

    private static int Status(IActionResult result)
        => result.Should().BeOfType<ContentResult>().Subject.StatusCode!.Value;

    // ── search ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASearchWithoutAPatient_IsRefused()
    {
        // There is no tenant-wide prior-auth listing on this route: every
        // consumer of it is acting for one member.
        var result = await _controller.SearchPriorAuthorizations(patient: null, status: null);

        Status(result).Should().Be(400);
        Body(result)["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
    }

    [Fact]
    public async Task ATypedPatientReference_IsAcceptedLikeABareId()
    {
        // FHIR search semantics: `patient=Patient/M1` means `patient=M1`.
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, null, null, 1, 50))
            .ReturnsAsync([Auth("M1", "PA-1")]);

        var result = await _controller.SearchPriorAuthorizations(patient: "Patient/M1", status: null);

        Status(result).Should().Be(200);
        Body(result)["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ADrugAuthorization_IsNotReturnedBySearch()
    {
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, null, null, 1, 50))
            .ReturnsAsync(
            [
                Auth("M1", "PA-MEDICAL"),
                // A J-code line: a physician-administered drug, out of the
                // CMS-0057-F PA-data scope.
                Auth("M1", "PA-DRUG", procedureCode: "J1745", procedureSystem: "hcpcs"),
                // A pharmacy-type request, likewise out of scope.
                Auth("M1", "PA-PHARMACY", serviceTypeCode: "88"),
            ]);

        var bundle = Body(await _controller.SearchPriorAuthorizations(patient: "M1", status: null));

        var ids = bundle["entry"]!.AsArray()
            .Select(e => e!["resource"]!["id"]!.GetValue<string>())
            .ToList();
        ids.Should().Equal("pa-PA-MEDICAL");
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ARecordForAnotherMember_IsDroppedEvenIfTheStoreReturnsIt()
    {
        // Defence in depth: the repository filters by member, and a result for
        // anyone else means it did not. This controller's own guarantee holds
        // whatever the store does.
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, null, null, 1, 50))
            .ReturnsAsync([Auth("M1", "PA-MINE"), Auth("M2", "PA-SOMEONE-ELSES")]);

        var bundle = Body(await _controller.SearchPriorAuthorizations(patient: "M1", status: null));

        bundle["entry"]!.AsArray()
            .Select(e => e!["resource"]!["patient"]!["reference"]!.GetValue<string>())
            .Should().AllBe("Patient/M1");
        bundle["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ResultsAreNewestFirst()
    {
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, null, null, 1, 50))
            .ReturnsAsync(
            [
                Auth("M1", "PA-OLD", submitted: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                Auth("M1", "PA-NEW", submitted: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)),
            ]);

        var bundle = Body(await _controller.SearchPriorAuthorizations(patient: "M1", status: null));

        bundle["entry"]!.AsArray().Select(e => e!["resource"]!["id"]!.GetValue<string>())
            .Should().Equal("pa-PA-NEW", "pa-PA-OLD");
    }

    [Fact]
    public async Task AStatusFilterIsPassedToTheStore()
    {
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, AuthorizationStatus.Pended, null, 1, 50))
            .ReturnsAsync([])
            .Verifiable();

        var result = await _controller.SearchPriorAuthorizations(patient: "M1", status: "pended");

        Status(result).Should().Be(200);
        _repository.Verify();
    }

    [Fact]
    public async Task AnUnrecognisedStatus_IsRefusedRatherThanIgnored()
    {
        // Silently ignoring it would answer a question the caller did not ask.
        var result = await _controller.SearchPriorAuthorizations(patient: "M1", status: "not-a-status");

        Status(result).Should().Be(400);
    }

    [Theory]
    // Below the floor and above the ceiling both clamp; a page below 1 is page 1.
    [InlineData(0, 0, 1, 1)]
    [InlineData(5000, 3, 200, 3)]
    public async Task PagingIsClamped(int count, int page, int expectedSize, int expectedPage)
    {
        _repository
            .Setup(r => r.SearchAsync("M1", null, null, null, null, null, expectedPage, expectedSize))
            .ReturnsAsync([])
            .Verifiable();

        await _controller.SearchPriorAuthorizations(patient: "M1", status: null, count: count, page: page);

        _repository.Verify();
    }

    [Fact]
    public async Task AStoreFailureIsA500OperationOutcome_NotAnEmptyBundle()
    {
        _repository
            .Setup(r => r.SearchAsync(It.IsAny<string>(), null, null, null, null, null, It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("store down"));

        var result = await _controller.SearchPriorAuthorizations(patient: "M1", status: null);

        Status(result).Should().Be(500);
        Body(result)["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
    }

    // ── read ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AReadByResourceId_ReturnsThePriorAuthorization()
    {
        _repository
            .Setup(r => r.GetByAuthorizationNumberAsync("PA-1"))
            .ReturnsAsync(Auth("M1", "PA-1"));

        var body = Body(await _controller.ReadPriorAuthorization("pa-PA-1"));

        body["resourceType"]!.GetValue<string>().Should().Be("ExplanationOfBenefit");
        body["use"]!.GetValue<string>().Should().Be("preauthorization");
        body["id"]!.GetValue<string>().Should().Be("pa-PA-1");
    }

    [Fact]
    public async Task AReadByBareAuthorizationNumber_AlsoWorks()
    {
        // A provider holding a preAuthRef should not have to know the prefix.
        _repository
            .Setup(r => r.GetByAuthorizationNumberAsync("PA-1"))
            .ReturnsAsync(Auth("M1", "PA-1"));

        Status(await _controller.ReadPriorAuthorization("PA-1")).Should().Be(200);
    }

    [Fact]
    public async Task AReadOfAnAbsentAuthorization_Is404()
    {
        _repository
            .Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((Authorization?)null);

        Status(await _controller.ReadPriorAuthorization("pa-MISSING")).Should().Be(404);
    }

    [Fact]
    public async Task AReadOfADrugAuthorization_Is404_NotARefusal()
    {
        // A 403 would tell the caller a drug authorization exists. Out of scope
        // is indistinguishable from absent, on purpose.
        _repository
            .Setup(r => r.GetByAuthorizationNumberAsync("PA-DRUG"))
            .ReturnsAsync(Auth("M1", "PA-DRUG", serviceTypeCode: "88"));

        var result = await _controller.ReadPriorAuthorization("pa-PA-DRUG");

        Status(result).Should().Be(404);
        Body(result)["resourceType"]!.GetValue<string>().Should().Be("OperationOutcome");
    }

    [Fact]
    public async Task AReadWithABlankId_IsRefused()
    {
        Status(await _controller.ReadPriorAuthorization("   ")).Should().Be(400);
    }
}
