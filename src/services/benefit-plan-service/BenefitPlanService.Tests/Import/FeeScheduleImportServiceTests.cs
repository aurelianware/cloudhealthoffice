using System.Text;
using BenefitPlanService.Import.FeeSchedules;
using BenefitPlanService.Models;
using BenefitPlanService.Tests.Fakes;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// Fee schedule CSV import into the canonical store claims are priced from:
/// header found by alias below title rows, unpriceable rows left out, duplicate
/// rates surfaced, dry run by default, plan default and standalone ids.
/// </summary>
public sealed class FeeScheduleImportServiceTests
{
    private const string Tenant = "tenant-a";
    private static readonly DateTime Effective = new(2026, 9, 1);

    // Shaped like a state Medicaid export: title rows, a provider-type column,
    // non-facility and facility fees, effective dates and non-dollar entries.
    private const string StateCsv =
        "Texas Medicaid Fee Schedule - Physician\n" +
        "Generated 10/01/2026,,,,,,\n" +
        "\n" +
        "Provider Type,Procedure Code,Modifier,Description,Non-Facility Fee,Facility Fee,Effective Date\n" +
        "Physician,99213,,Office visit est,$62.10,$44.80,09/01/2026\n" +
        "Physician,99214,,Office visit est,\"$1,091.25\",,09/01/2026\n" +
        "Physician,71046,26,Chest x-ray 2v,9.85,9.85,09/01/2026\n" +
        "Physician,71046,TC,Chest x-ray 2v,21.40,,09/01/2026\n" +
        "Physician,J3490,,Unclassified drug,BR,,09/01/2026\n" +
        "Physician,99499,,Unlisted E/M,0.00,,09/01/2026\n" +
        "Physician,99215,,Office visit est,80.00,,01/01/2027\n" +
        "Physician,99212,,Office visit est,30.00,,09/01/2025\n" +
        "Physician,99212,,Office visit est,31.50,,09/01/2026\n" +
        "Physician,9921,,Typo,10.00,,09/01/2026\n" +
        "Nurse Practitioner,99213,,Office visit est,52.79,,09/01/2026\n";

    [Fact]
    public async Task DryRun_FindsHeaderBelowTitleRows_AndWritesNothing()
    {
        var (svc, repo, _) = Build();

        var report = await svc.ImportAsync(Csv(StateCsv), Options(filter: ("Provider Type", "Physician")), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.WouldWrite);
        report.HeaderRow.Should().Be(4);
        report.Columns["procedureCode"].Should().Be("Procedure Code");
        report.Columns["rate"].Should().Be("Non-Facility Fee");
        report.Columns["facilityRate"].Should().Be("Facility Fee");
        report.RowsFilteredOut.Should().Be(1);
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Parse_MapsRates_AndSkipsWhatCannotBePriced()
    {
        var (svc, _, _) = Build();

        var report = await svc.ImportAsync(Csv(StateCsv), Options(filter: ("Provider Type", "Physician"), includeLines: true), Tenant, "admin");

        report.Lines.Select(l => (l.ProcedureCode, l.Modifier)).Should().Equal(
            ("71046", "26"), ("71046", "TC"), ("99212", null), ("99213", null), ("99214", null));
        report.Lines.Should().OnlyContain(l => l.RateType == FeeScheduleRateType.FlatRate);

        var visit = report.Lines.Single(l => l.ProcedureCode == "99213");
        visit.Rate.Should().Be(62.10m);
        visit.FacilityRate.Should().Be(44.80m);
        report.Lines.Single(l => l.ProcedureCode == "99214").Rate.Should().Be(1091.25m);
        report.Lines.Single(l => l.ProcedureCode == "99212").Rate.Should().Be(31.50m, "the latest rate in force wins");

        report.Skipped.Should().ContainKey("no dollar rate (by report, manually priced or not covered)");
        report.Skipped.Keys.Should().Contain(k => k.StartsWith("$0.00 rate"));
        report.Skipped.Should().ContainKey("rate takes effect after the schedule's effective date");
        report.Skipped.Should().ContainKey("not a 5-character CPT, HCPCS or CDT code");
        report.DuplicateKeys.Should().Be(0);
        report.Warnings.Should().Contain(w => w.Contains("multiple procedure indicator"));
    }

    [Fact]
    public async Task ConflictingRates_RejectByDefault_NamingTheFilter()
    {
        var (svc, repo, _) = Build();

        var report = await svc.ImportAsync(Csv(StateCsv), Options(dryRun: false), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Rejected);
        report.DuplicateKeys.Should().Be(1);
        report.DuplicateExamples.Single().Code.Should().Be("99213");
        report.Errors.Single().Should().Contain("filter");
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task ConflictingRates_LowestPolicy_KeepsLowest()
    {
        var (svc, _, _) = Build();

        var report = await svc.ImportAsync(Csv(StateCsv), Options(onDuplicate: DuplicateRatePolicy.Lowest, includeLines: true), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.WouldWrite);
        report.Lines.Single(l => l.ProcedureCode == "99213").Rate.Should().Be(52.79m);
        report.Warnings.Should().Contain(w => w.Contains("onDuplicate=Lowest"));
    }

    [Fact]
    public async Task RealRun_Standalone_WritesMedicaidScheduleWithProvenance()
    {
        var (svc, repo, _) = Build();

        var report = await svc.ImportAsync(Csv(StateCsv),
            Options(dryRun: false, filter: ("Provider Type", "Physician"), type: FeeScheduleType.Medicaid), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Created);
        report.ScheduleId.Should().Be("tenant-a:tx-medicaid-physician-2026-09:20260901");
        var saved = repo.Stored.Should().ContainSingle().Subject;
        saved.TenantId.Should().Be(Tenant);
        saved.Type.Should().Be(FeeScheduleType.Medicaid);
        saved.SourceType.Should().Be(FeeScheduleSourceType.PublicGovernment);
        saved.SourceId.Should().Be("tx-tmhp-physician");
        saved.Jurisdiction.Should().Be("TX");
        saved.DefaultForPlanId.Should().BeNull();
        saved.EffectiveDate.Kind.Should().Be(DateTimeKind.Utc);
        saved.Checksum.Should().Be(report.Checksum).And.HaveLength(64);
        saved.Lines.Should().HaveCount(5);
    }

    [Fact]
    public async Task RealRun_PlanDefault_UsesThePlanDefaultId()
    {
        var planId = Guid.NewGuid();
        var (svc, repo, _) = Build(planId);

        var report = await svc.ImportAsync(Csv(StateCsv),
            Options(dryRun: false, filter: ("Provider Type", "Physician"), planId: planId), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.Created);
        var saved = repo.Stored.Single();
        saved.Id.Should().Be($"{Tenant}:plan-default:{planId:D}");
        saved.DefaultForPlanId.Should().Be(planId.ToString("D"));
        (await repo.GetDefaultForPlanAsync(Tenant, planId.ToString("D"), new DateTime(2026, 10, 1))).Should().NotBeNull();
    }

    [Fact]
    public async Task PlanDefault_ByPlanCode_FollowsThePlanAcrossAmendments()
    {
        var planId = Guid.NewGuid();
        var (svc, repo, _) = Build(planId);

        var report = await svc.ImportAsync(Csv(StateCsv),
            Options(dryRun: false, filter: ("Provider Type", "Physician")) with { PlanId = " TX-STAR-TEST " }, Tenant, "admin");

        report.DefaultForPlanId.Should().Be("TX-STAR-TEST");
        repo.Stored.Single().Id.Should().Be($"{Tenant}:plan-default:TX-STAR-TEST");

        var contracts = new Mock<IProviderContractRepository>();
        var engine = new RateResolutionService(repo, contracts.Object, NullLogger<RateResolutionService>.Instance);
        var amendedVersion = Guid.NewGuid().ToString("D");
        var line = new PricingRequest
        {
            TenantId = Tenant, PlanId = amendedVersion, PlanCode = "TX-STAR-TEST", ProviderNpi = "1234567893",
            ProcedureCode = "99213", PlaceOfServiceCode = "11", ServiceDate = new DateTime(2026, 10, 15), BilledAmount = 150m,
        };
        (await engine.ResolveAsync(line)).AllowedAmount.Should().Be(62.10m, "a later version of the plan still finds the plan-code default");
    }

    [Fact]
    public async Task PlanDefault_UnknownPlanCode_Throws()
    {
        var (svc, _, _) = Build(Guid.NewGuid());

        var act = () => svc.ImportAsync(Csv(StateCsv), Options(dryRun: false) with { PlanId = "NO-SUCH-PLAN" }, Tenant, "admin");

        (await act.Should().ThrowAsync<FeeSchedulePlanNotFoundException>()).Which.PlanId.Should().Be("NO-SUCH-PLAN");
    }

    [Fact]
    public async Task PlanDefault_PlanInAnotherTenant_Throws()
    {
        var (svc, repo, _) = Build(Guid.NewGuid());

        var act = () => svc.ImportAsync(Csv(StateCsv), Options(dryRun: false, planId: Guid.NewGuid()), Tenant, "admin");

        await act.Should().ThrowAsync<FeeSchedulePlanNotFoundException>();
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Reimport_SameFile_IsUnchanged_ChangedFileNeedsReplace()
    {
        var (svc, repo, _) = Build();
        var physician = ("Provider Type", "Physician");

        (await svc.ImportAsync(Csv(StateCsv), Options(dryRun: false, filter: physician), Tenant, "admin"))
            .Outcome.Should().Be(FeeScheduleImportOutcome.Created);

        (await svc.ImportAsync(Csv(StateCsv), Options(dryRun: false, filter: physician), Tenant, "admin"))
            .Outcome.Should().Be(FeeScheduleImportOutcome.Unchanged);

        var changed = StateCsv.Replace("$62.10", "$63.00");
        var refused = await svc.ImportAsync(Csv(changed), Options(dryRun: false, filter: physician), Tenant, "admin");
        refused.Outcome.Should().Be(FeeScheduleImportOutcome.Exists);
        refused.Errors.Single().Should().Contain("replace=true");

        var replaced = await svc.ImportAsync(Csv(changed), Options(dryRun: false, filter: physician, replace: true), Tenant, "admin");
        replaced.Outcome.Should().Be(FeeScheduleImportOutcome.Replaced);
        repo.Stored.Single().Lines.Single(l => l.ProcedureCode == "99213").Rate.Should().Be(63.00m);
        repo.Stored.Single().Revision.Should().Be(2);
    }

    [Fact]
    public async Task RevenueCodeLines_AndMultipleProcedureIndicator_AreRead()
    {
        var (svc, _, _) = Build();
        const string csv =
            "HCPCS,Mod,Rev Code,Rate,Mult Proc\n" +
            "27447,,,1450.00,2\n" +
            ",,120,1800,\n" +
            ",,0450,725.50,\n";

        var report = await svc.ImportAsync(Csv(csv), Options(includeLines: true), Tenant, "admin");

        report.Outcome.Should().Be(FeeScheduleImportOutcome.WouldWrite);
        report.Lines.Should().HaveCount(3);
        report.Lines.Single(l => l.ProcedureCode == "27447").MultipleProcedureIndicator
            .Should().Be(MultipleProcedureIndicator.StandardSurgery);
        report.Lines.Single(l => l.RevenueCode == "0120").Rate.Should().Be(1800m);
        report.Warnings.Should().NotContain(w => w.Contains("multiple procedure indicator"));
    }

    [Fact]
    public async Task ColumnOverrides_NameUnusualHeaders()
    {
        var (svc, _, _) = Build();
        const string csv = "Svc,Pay Amt\nD0120,31.00\nD1110,58.00\n";

        var act = () => svc.ImportAsync(Csv(csv), Options(), Tenant, "admin");
        (await act.Should().ThrowAsync<FeeScheduleFormatException>()).WithMessage("*codeColumn*");

        var report = await svc.ImportAsync(Csv(csv),
            Options(columns: new FeeScheduleColumnOverrides(Code: "Svc", Rate: "Pay Amt")), Tenant, "admin");
        report.LinesLoaded.Should().Be(2);
        report.Columns["rate"].Should().Be("Pay Amt");
    }

    [Fact]
    public async Task UnknownFilterColumn_ListsTheHeader()
    {
        var (svc, _, _) = Build();

        var act = () => svc.ImportAsync(Csv(StateCsv), Options(filter: ("Program", "STAR")), Tenant, "admin");

        (await act.Should().ThrowAsync<FeeScheduleFormatException>()).WithMessage("*Provider Type*");
    }


    [Fact]
    public async Task ImportedPlanDefault_PricesAnUncontractedLine_FacilityAndNonFacility()
    {
        var planId = Guid.NewGuid();
        var (svc, repo, _) = Build(planId);
        await svc.ImportAsync(Csv(StateCsv),
            Options(dryRun: false, filter: ("Provider Type", "Physician"), planId: planId), Tenant, "admin");

        var contracts = new Mock<IProviderContractRepository>();
        contracts.Setup(c => c.GetContractAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderContract?)null);
        var engine = new RateResolutionService(repo, contracts.Object, NullLogger<RateResolutionService>.Instance);

        PricingRequest Line(string code, string pos) => new()
        {
            TenantId = Tenant, PlanId = planId.ToString("D"), ProviderNpi = "1234567893",
            ProcedureCode = code, PlaceOfServiceCode = pos, ServiceDate = new DateTime(2026, 10, 15), BilledAmount = 150m,
        };

        var office = await engine.ResolveAsync(Line("99213", "11"));
        office.AllowedAmount.Should().Be(62.10m);
        office.RateSource.Should().Be(RateSource.Medicaid);

        (await engine.ResolveAsync(Line("99213", "22"))).AllowedAmount.Should().Be(44.80m, "outpatient hospital takes the facility fee");

        var byReport = await engine.ResolveAsync(Line("J3490", "11"));
        byReport.RateSource.Should().Be(RateSource.BilledCharges, "a by-report code is not loaded, so the line is not priced from the schedule");
    }

    [Theory]
    [InlineData("$1,234.56", 1234.56, null)]
    [InlineData(" 12.5 ", 12.5, null)]
    [InlineData("By Report", 0, "no dollar rate (by report, manually priced or not covered)")]
    [InlineData("N/A", 0, "no dollar rate (by report, manually priced or not covered)")]
    [InlineData("*", 0, "no dollar rate (by report, manually priced or not covered)")]
    [InlineData("(3.00)", 0, "negative rate")]
    [InlineData("twelve", 0, "unreadable rate")]
    [InlineData("", 0, "no rate")]
    public void ParseMoney_ReadsDollarAmounts(string text, double amount, string? problem)
    {
        var (value, reason) = FeeScheduleImportService.ParseMoney(text);
        reason.Should().Be(problem);
        if (problem is null) value.Should().Be((decimal)amount);
    }

    [Fact]
    public void Slug_IsStableAndBounded()
    {
        FeeScheduleImportService.Slug("TX Medicaid: Physician (2026-09)").Should().Be("tx-medicaid-physician-2026-09");
        FeeScheduleImportService.Slug("***").Should().Be("fee-schedule");
    }

    // ── helpers ──

    // DHCS's Medi-Cal rates_data.txt: a date line, then tab-separated rows with no header,
    // one row per procedure type and code (synthetic codes and descriptions).
    private const string MediCalRates =
        "10/15/2026\n" +
        "N\t99213\tOFFICE VISIT                  \t     2.40\t   $24.00\t   $26.18\t   $29.81\t001\t1\t1\t0.00\t    $0.00\tY\t\n" +
        "P\t99213\tOFFICE VISIT                  \t     2.40\t   $24.00\t   $26.18\t       --\t052\t0\t1\t0.00\t    $0.00\tY\t\n" +
        "X\t99213\tOFFICE VISIT                  \t    82.02\t   $82.02\t      ---\t       --\t009\t1\t1\t0.00\t    $0.00\tY\t\n" +
        "K\t27447\tKNEE REPLACEMENT              \t    40.00\t $1489.20\t      ---\t $1878.00\t004\t1\t0\t0.00\t    $0.00\tY\t\n" +
        "O\t27447\tKNEE REPLACEMENT              \t     8.00\t  $297.84\t      ---\t  $375.60\t004\t1\t0\t0.00\t    $0.00\tY\t\n" +
        "L\t71046\tCHEST X-RAY                   \t     7.19\t   $27.47\t      ---\t       --\t005\t0\t0\t0.34\t    $0.00\tY\t\n";

    [Fact]
    public async Task MediCalRatesTxt_HeaderlessTabFile_IsReadWithTheWorksheetColumns()
    {
        var (svc, _, _) = Build();

        var report = await svc.ImportAsync(Csv(MediCalRates),
            Options(filter: ("Proc Type", "I|K|L|M|N"), includeLines: true), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.Columns["procedureCode"].Should().Be("Proc Code");
        report.Columns["rate"].Should().Be("Basic Rate");
        report.RowsFilteredOut.Should().Be(4, "the date line and the podiatrist, TRI and assistant-surgeon rows are not in the filter");
        report.Lines.Select(l => (l.ProcedureCode, l.Rate)).Should().Equal(("27447", 1489.20m), ("71046", 27.47m), ("99213", 24.00m));
    }

    [Fact]
    public async Task FilterOrder_KeepsTheEarliestListedValue_TargetedRateIncreaseOverBase()
    {
        var (svc, _, _) = Build();

        var report = await svc.ImportAsync(Csv(MediCalRates),
            Options(filter: ("Proc Type", "X|I|K|L|M|N"), onDuplicate: DuplicateRatePolicy.FilterOrder, includeLines: true), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.Lines.Single(l => l.ProcedureCode == "99213").Rate.Should().Be(82.02m, "X (the targeted rate increase) is listed first");
        report.Lines.Single(l => l.ProcedureCode == "27447").Rate.Should().Be(1489.20m, "no TRI row: the base primary-surgeon rate");
    }

    [Fact]
    public async Task MediCalZip_ReadsTheRatesTxt_NotTheConversionFactors()
    {
        var (svc, _, _) = Build();
        var zipBytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("conv_data.txt", "10/15/2026\n001\tADULT\tPRIMARY CARE I\t001\t   $10.00\n");
            Add("rates_data.txt", MediCalRates);
        }
        zipBytes.Position = 0;

        await using var csv = FeeScheduleCsvReader.OpenCsv(zipBytes, "rates_files.zip");
        var report = await svc.ImportAsync(csv, Options(filter: ("Proc Type", "K|L|N")), Tenant, "admin");

        report.Errors.Should().BeEmpty();
        report.LinesLoaded.Should().Be(3);
    }

    [Fact]
    public async Task TmhpAdjustedFee_IsReadOverTheUnreducedFee()
    {
        var (svc, _, _) = Build();
        // TMHP static fee schedules carry the fee before and after mandated rate reductions.
        var csv = "Procedure Code,Fee,Adjusted Fee\n99213,50.00,48.75\n";

        var report = await svc.ImportAsync(Csv(csv), Options(includeLines: true), Tenant, "admin");

        report.Columns["rate"].Should().Be("Adjusted Fee");
        report.Lines.Single().Rate.Should().Be(48.75m);
    }

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    private static FeeScheduleImportOptions Options(
        bool dryRun = true,
        (string, string)? filter = null,
        bool includeLines = false,
        DuplicateRatePolicy onDuplicate = DuplicateRatePolicy.Reject,
        FeeScheduleType type = FeeScheduleType.Medicaid,
        Guid? planId = null,
        bool replace = false,
        FeeScheduleColumnOverrides? columns = null) => new()
    {
        Name = "TX Medicaid Physician 2026-09",
        EffectiveDate = Effective,
        Type = type,
        SourceId = "tx-tmhp-physician",
        SourceVersion = "2026-10-01",
        Jurisdiction = "tx",
        PlanId = planId?.ToString("D"),
        Filters = filter is { } f ? [f] : [],
        OnDuplicate = onDuplicate,
        Replace = replace,
        DryRun = dryRun,
        IncludeLines = includeLines,
        Columns = columns ?? new FeeScheduleColumnOverrides(),
    };

    private static (FeeScheduleImportService, InMemoryFeeScheduleRepository, InMemoryBenefitPlanRepository) Build(Guid? planId = null)
    {
        var repo = new InMemoryFeeScheduleRepository();
        var plans = new InMemoryBenefitPlanRepository();
        if (planId is { } id)
        {
            plans.CreateAsync(new BenefitPlan
            {
                Id = id.ToString("D"),
                TenantId = Tenant,
                PlanId = "TX-STAR-TEST",
                PlanName = "STAR test plan",
                EffectiveDate = new DateTime(2026, 9, 1),
            }).GetAwaiter().GetResult();
        }
        return (new FeeScheduleImportService(repo, plans, NullLogger<FeeScheduleImportService>.Instance), repo, plans);
    }
}
