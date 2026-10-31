using AuthorizationService.Backends;
using AuthorizationService.Import;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services;
using CloudHealthOffice.Infrastructure.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthorizationService.Tests.Import;

/// <summary>
/// Authorizations another system decided load into CHO read-only, keyed by
/// the source's authorization number, and reload without churn.
/// </summary>
public sealed class AuthorizationImportTests
{
    private const string Tenant = "tenant-a";

    private const string Header =
        "authorization_number,member_id,status,received_date,decision_date,requesting_provider_npi,servicing_provider_npi," +
        "request_type,service_type_code,level_of_service,requested_from_date,requested_to_date,approved_from_date," +
        "approved_to_date,diagnosis_codes,procedure_code,modifiers,requested_units,approved_units,line_status,denial_reason";

    private const string TwoAuths = Header + "\n" +
        "PA-100,M-1,approved,2026-10-01,2026-10-03,1234567893,1987654321,prior,2,standard,2026-10-10,2026-10-20,2026-10-10,2026-10-20,\"M17.11;Z96.651\",27447,,1,1,A1,\n" +
        "PA-200,M-2,modified,2026-10-02,2026-10-02,1234567893,,prior,1,urgent,2026-10-05,2026-11-05,2026-10-05,2026-10-20,M54.5,97110,GP,12,6,A2,Six visits approved\n" +
        "PA-200,M-2,modified,2026-10-02,2026-10-02,1234567893,,prior,1,urgent,2026-10-05,2026-11-05,2026-10-05,2026-10-20,M54.5,97140,GP,12,12,A1,\n";

    private readonly Dictionary<string, Authorization> _store = new(StringComparer.Ordinal);
    private readonly Mock<IAuthorizationRepository> _repository = new();
    private readonly AuthorizationImportService _service;

    public AuthorizationImportTests()
    {
        _repository.Setup(r => r.GetByAuthorizationNumberAsync(It.IsAny<string>()))
            .ReturnsAsync((string n) => _store.Values.FirstOrDefault(a => a.AuthorizationNumber == n) is { } a ? Clone(a) : null);
        _repository.Setup(r => r.CreateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) =>
            {
                AuthorizationDeadlinePolicy.Initialize(a);
                _store[a.Id] = Clone(a);
                return a;
            });
        _repository.Setup(r => r.UpdateAsync(It.IsAny<Authorization>()))
            .ReturnsAsync((Authorization a) =>
            {
                if (_store[a.Id].Revision != a.Revision) throw new ConcurrencyConflictException("moved on");
                a.Revision++;
                _store[a.Id] = Clone(a);
                return a;
            });
        _service = new AuthorizationImportService(_repository.Object, NullLogger<AuthorizationImportService>.Instance);
    }

    private static Authorization Clone(Authorization a)
        => System.Text.Json.JsonSerializer.Deserialize<Authorization>(System.Text.Json.JsonSerializer.Serialize(a))!
            is var copy ? Restore(copy, a) : null!;

    // ExternalSource and the ledger are [JsonIgnore]: copy them across by hand.
    private static Authorization Restore(Authorization copy, Authorization original)
    {
        copy.ExternalSource = original.ExternalSource is null ? null : new AuthorizationExternalSource
        {
            Format = original.ExternalSource.Format, SourceSystem = original.ExternalSource.SourceSystem,
            SourceMemberId = original.ExternalSource.SourceMemberId, ContentHash = original.ExternalSource.ContentHash,
            ImportBatchId = original.ExternalSource.ImportBatchId, FileName = original.ExternalSource.FileName,
            FirstImportedAt = original.ExternalSource.FirstImportedAt, ImportedAt = original.ExternalSource.ImportedAt,
        };
        copy.TenantId = original.TenantId;
        copy.Revision = original.Revision;
        return copy;
    }

    private Task<AuthorizationImportResult> Import(string csv, bool dryRun = false)
        => _service.ImportCsvAsync(Tenant, csv,
            new AuthorizationImportOptions { SourceSystem = "jiva", FileName = "auths.csv", DryRun = dryRun }, "svc");

    [Fact]
    public async Task Loads_each_authorization_with_its_lines_and_provenance()
    {
        var result = await Import(TwoAuths);

        result.FileErrors.Should().BeEmpty();
        result.AuthorizationsInFile.Should().Be(2);
        result.Created.Should().Be(2);

        var knee = _store.Values.Single(a => a.AuthorizationNumber == "PA-100");
        knee.Id.Should().Be(AuthorizationImportService.ImportedId(Tenant, "PA-100"));
        knee.MemberId.Should().Be("M-1");
        knee.Status.Should().Be(AuthorizationStatus.Approved);
        knee.ReviewDecision.Should().Be("A1");
        knee.AuthorizationType.Should().Be(AuthorizationType.PreAuthorization);
        knee.LevelOfService.Should().Be("E");
        knee.ServicingProviderNPI.Should().Be("1987654321");
        knee.DiagnosisCodes.Select(d => (d.Code, d.CodeQualifier)).Should().Equal(("M1711", "BK"), ("Z96651", "BF"));
        knee.SubmittedDate.Should().Be(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        knee.ReviewedDate.Should().Be(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc));
        knee.ExternalSource!.SourceSystem.Should().Be("jiva");
        knee.ExternalSource.Format.Should().Be("csv");
        knee.IsImported.Should().BeTrue();
        knee.StatusHistory.Select(h => h.Status).Should().Equal(AuthorizationStatus.Submitted, AuthorizationStatus.Approved);
        knee.StatusHistory.Should().OnlyContain(h => h.Source == "import");

        var therapy = _store.Values.Single(a => a.AuthorizationNumber == "PA-200");
        therapy.Status.Should().Be(AuthorizationStatus.Modified);
        therapy.LevelOfService.Should().Be("U");
        therapy.RequestedServices.Select(s => (s.LineNumber, s.ProcedureCode, s.RequestedUnits, s.ApprovedUnits, s.ServiceStatus))
            .Should().Equal((1, "97110", 12m, 6m, "A2"), (2, "97140", 12m, 12m, "A1"));
        therapy.RequestedServices[0].Modifiers.Should().Equal("GP");
    }

    [Fact]
    public async Task A_reload_of_the_same_file_changes_nothing()
    {
        await Import(TwoAuths);
        var again = await Import(TwoAuths);

        again.Unchanged.Should().Be(2);
        again.Created.Should().Be(0);
        _repository.Verify(r => r.UpdateAsync(It.IsAny<Authorization>()), Times.Never);
    }

    [Fact]
    public async Task A_decision_in_a_later_file_updates_the_authorization_and_its_history()
    {
        await Import(Header + "\nPA-300,M-3,pended,2026-10-01,,1234567893,,,,,2026-10-10,,,,,,,,,,\n");
        var pended = _store.Values.Single();
        pended.Status.Should().Be(AuthorizationStatus.Pended);

        var result = await Import(Header + "\nPA-300,M-3,denied,2026-10-01,2026-10-04,1234567893,,,,,2026-10-10,,,,,,,,,,Not medically necessary\n");

        result.Updated.Should().Be(1);
        var denied = _store.Values.Single();
        denied.Id.Should().Be(pended.Id);
        denied.Status.Should().Be(AuthorizationStatus.Denied);
        denied.ReviewDecision.Should().Be("A3");
        denied.DenialReason.Should().Be("Not medically necessary");
        denied.StatusHistory.Select(h => h.Status)
            .Should().Equal(AuthorizationStatus.Submitted, AuthorizationStatus.Pended, AuthorizationStatus.Denied);
        denied.ExternalSource!.FirstImportedAt.Should().Be(pended.ExternalSource!.FirstImportedAt);
    }

    [Fact]
    public async Task A_number_CHO_owns_is_never_overwritten()
    {
        _store["native"] = new Authorization { Id = "native", AuthorizationNumber = "PA-100", TenantId = Tenant };

        var result = await Import(TwoAuths);

        result.Rejected.Should().Be(1);
        result.Authorizations.Single(a => a.AuthorizationNumber == "PA-100").Errors.Should().ContainMatch("*never replaces native work*");
        _store["native"].ExternalSource.Should().BeNull();
    }

    [Fact]
    public async Task A_dry_run_reports_and_writes_nothing()
    {
        var result = await Import(TwoAuths, dryRun: true);

        result.Created.Should().Be(2);
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_required_columns_are_named()
    {
        var result = await Import("authorization_number,member_id\nPA-1,M-1\n");

        result.AuthorizationsInFile.Should().Be(0);
        result.FileErrors.Should().Contain(e => e.Contains("\"status\"")).And.Contain(e => e.Contains("\"received_date\""))
            .And.Contain(e => e.Contains("\"requesting_provider_npi\""));
    }

    [Theory]
    [InlineData("maybe", "Status 'maybe'")]
    [InlineData("approved,2026-13-40", "Received date")]
    public async Task Bad_values_reject_the_authorization_with_a_reason(string statusAndDate, string expected)
    {
        var parts = statusAndDate.Split(',');
        var received = parts.Length > 1 ? parts[1] : "2026-10-01";
        var result = await Import(Header + $"\nPA-9,M-9,{parts[0]},{received},,1234567893,,,,,,,,,,,,,,,\n");

        result.Rejected.Should().Be(1);
        result.Authorizations.Single().Errors.Should().ContainMatch($"*{expected}*");
        _store.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bad_npi_rejects_and_a_pipe_delimited_file_with_its_own_headers_loads_through_a_column_map()
    {
        var piped = "AUTH_NO|MBR|Status|Request Date|Requesting NPI\nPA-1|M-1|A1|20261001|1234567893\nPA-2|M-2|denied|20261001|12345\n";
        var result = await _service.ImportCsvAsync(Tenant, piped, new AuthorizationImportOptions
        {
            ColumnMap = new Dictionary<string, string> { ["authorization_number"] = "AUTH_NO", ["member_id"] = "MBR" },
        }, null);

        result.Delimiter.Should().Be("|");
        result.Created.Should().Be(1);
        result.Authorizations.Single(a => a.AuthorizationNumber == "PA-2").Errors.Should().ContainMatch("*10-digit NPI*");
    }

    [Fact]
    public async Task The_documented_sample_file_loads_cleanly()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "integration", "AUTHORIZATION-IMPORT.md")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run inside the repository");
        var sample = await File.ReadAllTextAsync(
            Path.Combine(dir!.FullName, "docs", "integration", "samples", "authorizations", "authorizations.csv"));

        var result = await Import(sample);

        result.FileErrors.Should().BeEmpty();
        result.UnrecognizedColumns.Should().BeEmpty();
        result.Authorizations.Should().OnlyContain(a => a.Errors.Count == 0 && a.Warnings.Count == 0);
        result.Created.Should().Be(4);
        _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-PA-0002").RequestedServices.Should().HaveCount(2);
        _store.Values.Single(a => a.AuthorizationNumber == "SAMPLE-PA-0004").Status.Should().Be(AuthorizationStatus.Pended);
    }

    [Theory]
    [InlineData("A1", AuthorizationStatus.Approved, "A1")]
    [InlineData("A6", AuthorizationStatus.Modified, "A2")]
    [InlineData("Not Certified", AuthorizationStatus.Denied, "A3")]
    [InlineData("pending", AuthorizationStatus.Pended, "A4")]
    [InlineData("In Review", AuthorizationStatus.InReview, null)]
    [InlineData("withdrawn", AuthorizationStatus.Cancelled, null)]
    public void Statuses_read_as_UM_systems_and_X12_name_them(string value, AuthorizationStatus status, string? decision)
        => AuthorizationImportValues.Status(value).Should().Be((status, decision));

    [Fact]
    public void An_imported_authorization_refuses_every_CHO_transition()
    {
        var imported = new Authorization
        {
            Status = AuthorizationStatus.Pended,
            ExternalSource = new AuthorizationExternalSource { Format = "csv", SourceSystem = "jiva" },
        };

        var act = () => AuthorizationLifecyclePolicy.Validate(imported, AuthorizationStatus.Approved);

        act.Should().Throw<ImportedAuthorizationReadOnlyException>().WithMessage("*imported from jiva*read-only*")
            .And.Should().BeAssignableTo<AuthorizationTransitionException>();
    }

    [Fact]
    public void An_imported_authorization_cannot_have_its_timeframe_extended()
    {
        var imported = new Authorization
        {
            Status = AuthorizationStatus.Pended,
            LineOfBusiness = LineOfBusiness.Medicaid,
            ExternalSource = new AuthorizationExternalSource { Format = "csv" },
        };

        var act = () => AuthorizationDeadlinePolicy.Extend(imported,
            new ExtendDecisionDeadline { Basis = DecisionExtensionBasis.ProviderRequested, Reason = "more time" },
            "reviewer", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>().WithMessage("*imported*read-only*");
    }

    [Fact]
    public void Imported_ids_are_deterministic_and_tenant_scoped()
    {
        AuthorizationImportService.ImportedId("t1", "PA-1").Should().Be(AuthorizationImportService.ImportedId("t1", "PA-1"));
        AuthorizationImportService.ImportedId("t1", "PA-1").Should().NotBe(AuthorizationImportService.ImportedId("t2", "PA-1"));
        AuthorizationImportService.ImportedId("t1", "PA-1").Should().StartWith("imp-").And.HaveLength(36);
    }
}
