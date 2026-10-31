using AuthorizationService.Import;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthorizationService.Tests.Import;

/// <summary>
/// A core's or clearinghouse's 278 request and response files load as the
/// same read-only authorizations as the CSV, keyed by the certification number.
/// </summary>
public sealed class X12_278ImportTests
{
    private const string Tenant = "tenant-a";
    private readonly Dictionary<string, Authorization> _store = new(StringComparer.Ordinal);
    private readonly AuthorizationImportService _service;

    public X12_278ImportTests()
    {
        var repository = new Mock<IAuthorizationRepository>();
        repository.Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((string n) => _store.Values.FirstOrDefault(a => a.AuthorizationNumber == n));
        repository.Setup(r => r.CreateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) => { AuthorizationDeadlinePolicy.Initialize(a); _store[a.Id] = a; return a; });
        repository.Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) => { _store[a.Id] = a; return a; });
        _service = new AuthorizationImportService(repository.Object, NullLogger<AuthorizationImportService>.Instance);
    }

    private static string Sample()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "integration", "AUTHORIZATION-IMPORT.md")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "integration", "samples", "authorizations", "authorizations-278.x12"));
    }

    private Task<AuthorizationImportResult> Import(string content)
        => _service.ImportX12_278Async(Tenant, content,
            new AuthorizationImportOptions { SourceSystem = "clearinghouse", LineOfBusiness = LineOfBusiness.Medicaid }, "svc");

    [Fact]
    public async Task Responses_paired_with_their_requests_load_as_authorizations()
    {
        var result = await Import(Sample());

        result.FileErrors.Should().BeEmpty();
        result.TransactionSets.Should().Be(4);
        result.Created.Should().Be(2);
        result.Authorizations.Should().OnlyContain(a => a.Errors.Count == 0 && a.Warnings.Count == 0);

        var knee = _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-X12-0001");
        knee.ExternalSource!.Format.Should().Be("x12-278");
        knee.MemberId.Should().Be("SAMPLE-M-001");
        knee.PatientLastName.Should().Be("SAMPLE");
        knee.PatientDateOfBirth.Should().Be(new DateTime(1970, 1, 1));
        knee.Status.Should().Be(AuthorizationStatus.Approved);
        knee.ReviewDecision.Should().Be("A1");
        knee.LineOfBusiness.Should().Be(LineOfBusiness.Medicaid);
        knee.RequestingProviderNPI.Should().Be("1234567893");
        knee.RequestingProviderName.Should().Be("SAMPLE ORTHOPEDICS");
        knee.ServicingProviderNPI.Should().Be("1987654321");
        knee.ServiceTypeCode.Should().Be("2");
        knee.LevelOfService.Should().Be("E");
        knee.SubmittedDate.Should().Be(new DateTime(2026, 10, 1, 8, 15, 0, DateTimeKind.Utc), "the request's BHT is when it was received");
        knee.ReviewedDate.Should().Be(new DateTime(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc), "the response's BHT is when it was decided");
        knee.ApprovedServiceDateFrom.Should().Be(new DateTime(2026, 11, 15));
        knee.ApprovedServiceDateTo.Should().Be(new DateTime(2027, 1, 15));
        knee.DiagnosisCodes.Select(d => d.Code).Should().Equal("M1711");
        knee.RequestedServices.Should().ContainSingle().Which.Modifiers.Should().Equal("RT");

        var therapy = _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-X12-0002");
        therapy.Status.Should().Be(AuthorizationStatus.Modified, "one line was certified in part");
        therapy.LevelOfService.Should().Be("U");
        therapy.RequestedServiceDateFrom.Should().Be(new DateTime(2026, 10, 5));
        therapy.RequestedServiceDateTo.Should().Be(new DateTime(2026, 12, 31));
        therapy.RequestedServices.Select(s => (s.ProcedureCode, s.RequestedUnits, s.ApprovedUnits, s.ServiceStatus))
            .Should().Equal(("97110", 24m, 12m, "A2"), ("97140", 24m, 24m, "A1"));
    }

    [Fact]
    public async Task The_same_responses_resent_in_a_new_envelope_are_unchanged()
    {
        await Import(Sample());
        var resent = Sample().Replace("RSP-1", "RSP-9", StringComparison.Ordinal).Replace("000000002", "000000077", StringComparison.Ordinal);

        var again = await Import(resent);

        again.Unchanged.Should().Be(2);
    }

    [Fact]
    public async Task A_response_without_its_request_loads_with_a_warning_about_timeframes()
    {
        var sample = Sample();
        var responsesOnly = sample[sample.IndexOf("ISA", 10, StringComparison.Ordinal)..];

        var result = await Import(responsesOnly);

        result.Created.Should().Be(2);
        result.Authorizations.Should().OnlyContain(a => a.Warnings.Any(w => w.Contains("timeframe")));
        _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-X12-0002").RequestedServices[0].RequestedUnits
            .Should().Be(24m, "the response's own quantity stands in for the request's");
    }

    [Fact]
    public async Task Requests_with_no_response_are_reported_not_loaded()
    {
        var sample = Sample();
        var requestsOnly = sample[..sample.IndexOf("ISA", 10, StringComparison.Ordinal)];

        var result = await Import(requestsOnly);

        result.AuthorizationsInFile.Should().Be(0);
        result.FileErrors.Should().ContainMatch("*2 request(s) have no response*");
    }

    [Fact]
    public async Task A_file_that_is_not_X12_is_refused()
    {
        var result = await Import("authorization_number,member_id\n");
        result.FileErrors.Should().ContainSingle().Which.Should().Contain("ISA");
    }

    [Fact]
    public async Task A_denial_carries_its_reason_and_no_approved_period()
    {
        var denied = Sample().Replace("HCR*A1*SAMPLE-X12-0001~", "HCR*A3*SAMPLE-X12-0001*35~", StringComparison.Ordinal);

        await Import(denied);

        var knee = _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-X12-0001");
        knee.Status.Should().Be(AuthorizationStatus.Denied);
        knee.DenialReasonCode.Should().Be("35");
        knee.ApprovedServiceDateFrom.Should().BeNull();
        knee.RequestedServices.Single().ApprovedUnits.Should().Be(0m);
    }
}
