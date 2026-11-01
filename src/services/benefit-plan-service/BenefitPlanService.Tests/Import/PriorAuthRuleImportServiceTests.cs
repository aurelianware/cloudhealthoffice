using System.Text;
using BenefitPlanService.Import.PriorAuthRules;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.PriorAuthRuleEngine.Persistence;
using CloudHealthOffice.PriorAuthRuleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Import;

/// <summary>
/// A plan's prior-auth-required list loaded into the PA rule store that CRD,
/// PAS and the published required-services list read: payer-style files and
/// the list's own CSV, ranges and prefixes, dry run, replace-on-reimport, and
/// the platform rules (exemptions especially) the tenant set replaces.
/// </summary>
public sealed class PriorAuthRuleImportServiceTests
{
    private const string Tenant = "tenant-a";

    // Shaped like a payer's published PA list: a title row, a category, codes with
    // ranges, a PA-required column and a place-of-service column.
    private const string PayerCsv =
        "Acme Health STAR Prior Authorization List - effective 2027-01-01\n" +
        "\n" +
        "Service Category,CPT/HCPCS,Description,Place of Service,PA Required\n" +
        "Advanced imaging,70551 - 70553,MRI brain,,Y\n" +
        "Advanced imaging,72148,MRI lumbar spine,,Y\n" +
        "Outpatient surgery,27447,Knee replacement,22 24,Y\n" +
        "DME,K*,Any K code,,Y\n" +
        "Office visits,99213,Office visit,,N\n" +
        "Drugs,J9035,Bevacizumab,,Y\n" +
        "Typo,7055,Bad code,,Y\n";

    private static Stream Csv(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static PriorAuthRuleImportOptions Options(bool dryRun = true, string? program = "STAR", bool keepExemptions = true) => new()
    {
        StateCode = "TX", LineOfBusiness = PaLineOfBusiness.Medicaid, Program = program,
        SourceName = "acme-pa-list.csv", DryRun = dryRun, KeepExemptions = keepExemptions,
    };

    private static (PriorAuthRuleImportService Svc, FakeRepo Repo) Build()
    {
        var repo = new FakeRepo();
        return (new PriorAuthRuleImportService(repo, NullLogger<PriorAuthRuleImportService>.Instance), repo);
    }

    private static PaRuleDocument Platform(string id, string type, RuleCategory category, string? program = "STAR", params string[] codes) => new()
    {
        RuleId = id, RuleName = id, StateCode = "TX", Lob = PaLineOfBusiness.Medicaid, Program = program, TenantId = null,
        Category = category, Scope = RuleScope.Platform, Priority = 10, RuleType = type, ProcedureCodes = codes,
    };

    [Fact]
    public async Task Dry_run_reads_a_payer_list_and_writes_nothing()
    {
        var (svc, repo) = Build();

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(), Tenant, "admin");

        report.Outcome.Should().Be(PriorAuthRuleImportOutcome.WouldWrite);
        report.RuleSet.Should().Be("tenant-a/TX/Medicaid/STAR");
        report.HeaderRow.Should().Be(3);
        report.Columns["codes"].Should().Be("CPT/HCPCS");
        report.Columns["name"].Should().Be("Service Category");
        report.Columns["paRequired"].Should().Be("PA Required");
        report.RowsRead.Should().Be(7);
        report.RowsLoaded.Should().Be(5);
        report.Skipped.Should().ContainKey("the PA-required column says no");
        report.Skipped.Should().ContainKey("not a 5-character CPT, HCPCS or CDT code");
        repo.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Rows_become_rules_by_category_with_ranges_expanded()
    {
        var (svc, _) = Build();

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(), Tenant, "admin");

        var imaging = report.Rules.Single(r => r.RuleId == "import:advanced-imaging");
        imaging.RuleType.Should().Be("ProcedureRequiresAuth");
        imaging.Condition.Should().Be(PaRequirementCondition.Always);
        imaging.Codes.Should().Equal("70551", "70552", "70553", "72148");

        report.Rules.Single(r => r.RuleId == "import:outpatient-surgery").PlacesOfService.Should().Equal("22", "24");
        report.Rules.Single(r => r.RuleId == "import:dme").CodePrefixes.Should().Equal("K");
        report.DrugCodes.Should().Equal("J9035");
        report.Warnings.Should().Contain(w => w.Contains("drug code"));
    }

    [Fact]
    public async Task Write_stores_tenant_rules_the_engine_and_list_resolve_to()
    {
        var (svc, repo) = Build();

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(dryRun: false), Tenant, "admin");

        report.Outcome.Should().Be(PriorAuthRuleImportOutcome.Written);
        var (key, rules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", Tenant, null, default);
        key!.ToString().Should().Be("tenant-a/TX/Medicaid/STAR");
        rules.Should().OnlyContain(r => r.TenantId == Tenant && r.Scope == RuleScope.Tenant && r.IsEnabled);
        rules.Select(r => r.RuleId).Should().Contain("import:advanced-imaging");
        rules.Should().OnlyContain(r => r.Description!.Contains("acme-pa-list.csv"));

        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", key, rules, DateTime.UtcNow);
        list.Entries.SelectMany(e => e.Codes).Should().Contain(["70552", "27447"]);
        list.ExcludedDrugCodes.Should().Equal("J9035");
    }

    [Fact]
    public async Task Reimport_disables_rules_the_file_no_longer_has()
    {
        var (svc, repo) = Build();
        await svc.ImportAsync(Csv(PayerCsv), Options(dryRun: false), Tenant, "admin");

        const string smaller = "Category,Code\nAdvanced imaging,70551\n";
        var report = await svc.ImportAsync(Csv(smaller), Options(dryRun: false), Tenant, "admin");

        report.RulesDisabled.Should().Contain(["import:dme", "import:outpatient-surgery"]);
        var (_, rules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", Tenant, null, default);
        rules.Select(r => r.RuleId).Should().Equal("import:advanced-imaging");
        rules.Single().ProcedureCodes.Should().Equal("70551");
    }

    [Fact]
    public async Task Replaced_platform_set_is_reported_and_its_exemptions_are_kept()
    {
        var (svc, repo) = Build();
        repo.Seed(
            Platform("TX-GOLD", "TxGoldCardExemption", RuleCategory.RegulatoryExemption),
            Platform("TX-MRI", "ProcedureRequiresAuth", RuleCategory.ClinicalCriteria, "STAR", "70551"));

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(dryRun: false), Tenant, "admin");

        report.ReplacesRuleSet.Should().Be("platform/TX/Medicaid/STAR");
        report.ExemptionsKept.Should().Equal("import:keep:TX-GOLD");
        report.RulesNoLongerApplied.Should().Equal("TX-MRI");
        var (_, rules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", Tenant, null, default);
        var kept = rules.Single(r => r.RuleId == "import:keep:TX-GOLD");
        kept.RuleType.Should().Be("TxGoldCardExemption");
        kept.TenantId.Should().Be(Tenant);

        // A second import refreshes the copy from the platform rule, not from the earlier copy.
        var again = await svc.ImportAsync(Csv(PayerCsv), Options(dryRun: false), Tenant, "admin");
        again.ExemptionsKept.Should().Equal("import:keep:TX-GOLD");
        again.RulesDisabled.Should().BeEmpty();
    }

    [Fact]
    public async Task Exemptions_can_be_dropped_on_request()
    {
        var (svc, repo) = Build();
        repo.Seed(Platform("TX-GOLD", "TxGoldCardExemption", RuleCategory.RegulatoryExemption));

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(keepExemptions: false), Tenant, "admin");

        report.ExemptionsKept.Should().BeEmpty();
        report.RulesNoLongerApplied.Should().Equal("TX-GOLD");
    }

    [Fact]
    public async Task Program_less_import_warns_about_platform_program_sets_that_still_win()
    {
        var (svc, repo) = Build();
        repo.Seed(Platform("TX-MRI", "ProcedureRequiresAuth", RuleCategory.ClinicalCriteria, "STAR", "70551"));

        var report = await svc.ImportAsync(Csv(PayerCsv), Options(program: null), Tenant, "admin");

        report.RuleSet.Should().Be("tenant-a/TX/Medicaid/any");
        report.Warnings.Should().Contain(w => w.Contains("(STAR)"));
    }

    [Fact]
    public async Task The_published_list_csv_round_trips()
    {
        var (svc, repo) = Build();
        const string listCsv =
            "rule_id,name,codes,code_ranges,all_services,condition,places_of_service,diagnoses,unit_limit,visit_limit,denied_without_review\n" +
            "R1,Knee replacement,27447,,false,Always,21,,,,false\n" +
            "R2,Sleep study,95810,,false,WithDiagnosis,,G47.33,,,false\n" +
            "R3,Therapy,97110,,false,AboveQuantityLimit,,,,20,false\n" +
            "R4,Cosmetic,15775,,false,Always,,,,,true\n" +
            "R5,DME,,E*,false,Always,,,,,false\n";

        var report = await svc.ImportAsync(Csv(listCsv), Options(dryRun: false), Tenant, "admin");

        report.Skipped.Should().BeEmpty();
        report.Rules.Select(r => (r.RuleId, r.RuleType)).Should().Equal(
            ("import:r1", "ProcedureRequiresAuth"), ("import:r2", "DiagnosisRequired"), ("import:r3", "QuantityLimit"),
            ("import:r4", "ProcedureRequiresAuth"), ("import:r5", "ProcedureRequiresAuth"));
        report.Rules.Single(r => r.RuleId == "import:r2").Diagnoses.Should().Equal("G4733");
        report.Rules.Single(r => r.RuleId == "import:r4").DeniedWithoutReview.Should().BeTrue();

        var (key, rules) = await PaRuleSetResolver.ResolveAsync(repo, "TX", PaLineOfBusiness.Medicaid, "STAR", Tenant, null, default);
        var list = PaRequiredServicesList.Build("TX", PaLineOfBusiness.Medicaid, "STAR", key, rules, DateTime.UtcNow);
        list.UnclassifiedRuleIds.Should().BeEmpty();
        list.Entries.Single(e => e.RuleId == "import:r3").VisitLimit.Should().Be(20);
        list.Entries.Single(e => e.RuleId == "import:r5").CodePrefixes.Should().Equal("E");
    }

    [Theory]
    [InlineData("70553-70551", "code range ends before it starts")]
    [InlineData("70551-E0100", "code range ends are not two codes of the same shape")]
    [InlineData("10000-19999", "code range holds more than 1000 codes")]
    [InlineData("ABCDE*", "code prefix is not 1 to 4 letters or digits followed by *")]
    public void Bad_code_tokens_are_explained(string token, string reason)
    {
        PriorAuthRuleImportService.TryAddCodes(token, new HashSet<string>(), new HashSet<string>(), out var why)
            .Should().BeFalse();
        why.Should().Be(reason);
    }

    [Fact]
    public void Ranges_keep_letters_and_zero_padding()
    {
        var codes = new SortedSet<string>();
        PriorAuthRuleImportService.TryAddCodes("E0098-E0101", codes, new HashSet<string>(), out _).Should().BeTrue();
        PriorAuthRuleImportService.TryAddCodes("0001U-0002U", codes, new HashSet<string>(), out _).Should().BeTrue();
        codes.Should().Equal("0001U", "0002U", "E0098", "E0099", "E0100", "E0101");
        PriorAuthRuleImportService.Tokens("70551 thru 70553, 72148").Should().Equal("70551-70553", "72148");
    }

    [Fact]
    public async Task A_file_without_a_code_column_is_rejected()
    {
        var (svc, _) = Build();
        var act = () => svc.ImportAsync(Csv("Name,Fee\nMRI,100\n"), Options(), Tenant, "admin");
        await act.Should().ThrowAsync<PriorAuthRuleFormatException>();
    }

    /// <summary>Same contract as the Mongo repository: exact-key reads of enabled rules, writes keyed by full identity.</summary>
    private sealed class FakeRepo : IPaRuleRepository
    {
        private readonly Dictionary<string, PaRuleDocument> _docs = new();
        public int Writes { get; private set; }

        private static string Id(PaRuleDocument r) =>
            $"{r.StateCode}:{(int)r.Lob}:{r.Program ?? "any"}:{r.TenantId ?? "platform"}:{r.RuleId}";

        public void Seed(params PaRuleDocument[] rules)
        {
            foreach (var r in rules) _docs[Id(r)] = r;
        }

        public Task<IReadOnlyList<PaRuleDocument>> GetRulesAsync(RuleSetKey key, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PaRuleDocument>>(_docs.Values.Where(r => r.IsEnabled && r.StateCode == key.StateCode
                && r.Lob == key.Lob && r.Program == key.Program && r.TenantId == key.TenantId).ToList());

        public Task UpsertAsync(PaRuleDocument rule, CancellationToken ct = default) => BulkUpsertAsync([rule], ct);

        public Task BulkUpsertAsync(IEnumerable<PaRuleDocument> rules, CancellationToken ct = default)
        {
            foreach (var r in rules) { r.Id = Id(r); _docs[r.Id] = r; Writes++; }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string ruleId, string stateCode, string? tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PaRuleDocument>> ListAsync(string? tenantId = null, string? stateCode = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PaRuleDocument>>(_docs.Values.Where(r =>
                (tenantId is null || r.TenantId == tenantId) && (stateCode is null || r.StateCode == stateCode)).ToList());
    }
}
