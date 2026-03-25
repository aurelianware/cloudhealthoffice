using ClaimsScrubbingService.Models;
using ClaimsScrubbingService.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.ClaimsScrubbingService.Tests;

public class ValidationRuleEngineTests
{
    private readonly ValidationRuleEngine _engine;

    public ValidationRuleEngineTests()
    {
        _engine = new ValidationRuleEngine(NullLogger<ValidationRuleEngine>.Instance);
    }

    // ========================================================================
    // Test Helpers
    // ========================================================================

    private static string RecentDate(int daysAgo = 7) =>
        DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyyMMdd");

    private static X12837Claim CreateValidClaim(Action<X12837Claim>? configure = null)
    {
        var date = RecentDate();
        var claim = new X12837Claim
        {
            ClaimId = "CLM-TEST-001",
            ClaimType = "837P",
            TransactionControlNumber = "000000001",
            InterchangeControlNumber = "000000001",
            TransactionDate = date,
            Submitter = new ClaimSubmitter
            {
                Name = "Test Submitter",
                IdentificationCode = "SUB123",
                IdentificationQualifier = "46",
            },
            Receiver = new ClaimReceiver
            {
                Name = "Test Health Plan",
                IdentificationCode = "PAYER001",
                IdentificationQualifier = "PI",
            },
            BillingProvider = new BillingProvider
            {
                Npi = "1234567893",
                Name = "Test Medical Center",
                EntityType = "2",
                TaxId = "123456789",
                TaxIdQualifier = "EI",
                Address = new ProviderAddress
                {
                    Line1 = "123 Main St",
                    City = "Austin",
                    State = "TX",
                    PostalCode = "78701",
                },
            },
            Subscriber = new ClaimSubscriber
            {
                MemberId = "MEM123456789",
                FirstName = "John",
                LastName = "Doe",
                DateOfBirth = "19850615",
                Gender = "M",
                GroupNumber = "GRP001",
            },
            ClaimHeader = new ClaimHeader
            {
                PatientControlNumber = "PCN001",
                TotalChargeAmount = 250.00m,
                PlaceOfServiceCode = "11",
                PrincipalDiagnosisCode = "Z00.00",
                DiagnosisCodes = new() { new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 } },
            },
            ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = "99213", Modifiers = new() { "25" },
                    ServiceDate = date, ChargeAmount = 150.00m, Units = 1, PlaceOfService = "11",
                    DiagnosisPointers = new() { 1 },
                },
                new ServiceLine
                {
                    LineNumber = 2, ProcedureCode = "36415",
                    ServiceDate = date, ChargeAmount = 100.00m, Units = 1, PlaceOfService = "11",
                    DiagnosisPointers = new() { 1 },
                },
            },
            TotalClaimedAmount = 250.00m,
            ParsedAt = DateTime.UtcNow.ToString("o"),
        };
        configure?.Invoke(claim);
        return claim;
    }

    // ========================================================================
    // Rule initialization
    // ========================================================================

    [Fact]
    public void Should_initialize_with_20_standard_rules()
    {
        var rules = _engine.GetRules();
        Assert.Equal(23, rules.Count);
    }

    [Theory]
    [InlineData("data-completeness")]
    [InlineData("code-validity")]
    [InlineData("date-logic")]
    [InlineData("amount-logic")]
    [InlineData("provider-validation")]
    [InlineData("modifier-validation")]
    public void Should_have_rules_in_each_category(string category)
    {
        var rules = _engine.GetRulesByCategory(category);
        Assert.NotEmpty(rules);
    }

    // ========================================================================
    // Valid claim passes all rules
    // ========================================================================

    [Fact]
    public async Task Valid_claim_is_clean_with_zero_errors()
    {
        var claim = CreateValidClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("clean", result.Status);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
        Assert.True(result.FirstPassEligible);
        Assert.Equal("CLM-TEST-001", result.ClaimId);
        Assert.Equal("837P", result.ClaimType);
        Assert.True(result.RulesExecuted > 0);
        Assert.Equal(result.RulesExecuted, result.RulesPassed);
        Assert.Equal(0, result.RulesFailed);
    }

    // ========================================================================
    // DC001 — Subscriber Identifier Required
    // ========================================================================

    [Fact]
    public async Task DC001_empty_memberId_rejected()
    {
        var claim = CreateValidClaim(c => c.Subscriber.MemberId = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC001" && !r.Passed);
    }

    [Fact]
    public async Task DC001_whitespace_memberId_rejected()
    {
        var claim = CreateValidClaim(c => c.Subscriber.MemberId = "   ");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Contains(result.Results, r => r.RuleId == "DC001" && !r.Passed);
    }

    // ========================================================================
    // DC002 — Subscriber DOB Required
    // ========================================================================

    [Fact]
    public async Task DC002_empty_dob_rejected()
    {
        var claim = CreateValidClaim(c => c.Subscriber.DateOfBirth = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC002" && !r.Passed);
    }

    // ========================================================================
    // DC003 — Billing Provider NPI Required
    // ========================================================================

    [Fact]
    public async Task DC003_empty_npi_rejected()
    {
        var claim = CreateValidClaim(c => c.BillingProvider.Npi = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC003" && !r.Passed);
    }

    // ========================================================================
    // DC004 — Diagnosis Code Required
    // ========================================================================

    [Fact]
    public async Task DC004_no_diagnosis_codes_rejected()
    {
        var claim = CreateValidClaim(c => c.ClaimHeader.DiagnosisCodes = new());
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC004" && !r.Passed);
    }

    [Fact]
    public async Task DC004_null_diagnosis_codes_rejected()
    {
        var claim = CreateValidClaim(c => c.ClaimHeader.DiagnosisCodes = null);
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Contains(result.Results, r => r.RuleId == "DC004" && !r.Passed);
    }

    // ========================================================================
    // DC005 — Minimum Service Lines
    // ========================================================================

    [Fact]
    public async Task DC005_empty_service_lines_rejected()
    {
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new();
            c.TotalClaimedAmount = 0;
        });
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Contains(result.Results, r => r.RuleId == "DC005" && !r.Passed);
    }

    // ========================================================================
    // DC006 — Service Date Required
    // ========================================================================

    [Fact]
    public async Task DC006_missing_service_date_rejected()
    {
        var claim = CreateValidClaim(c => c.ServiceLines[0].ServiceDate = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Contains(result.Results, r => r.RuleId == "DC006" && !r.Passed);
    }

    // ========================================================================
    // CV001 — Valid ICD-10 Code Format
    // ========================================================================

    [Theory]
    [InlineData("Z00.00", true)]
    [InlineData("J06.9", true)]
    [InlineData("A01", true)]
    [InlineData("S72.001A", true)]
    [InlineData("999", false)]
    [InlineData("ZZZ", false)]
    public async Task CV001_icd10_format_validation(string code, bool shouldPass)
    {
        var claim = CreateValidClaim(c =>
            c.ClaimHeader.DiagnosisCodes = new() { new DiagnosisCode { Code = code, Qualifier = "ABK" } });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv001 = result.Results.First(r => r.RuleId == "CV001");

        Assert.Equal(shouldPass, cv001.Passed);
    }

    [Fact]
    public async Task CV001_skips_non_icd10_qualifier()
    {
        var claim = CreateValidClaim(c =>
            c.ClaimHeader.DiagnosisCodes = new() { new DiagnosisCode { Code = "999", Qualifier = "BK" } });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv001 = result.Results.First(r => r.RuleId == "CV001");

        Assert.True(cv001.Passed); // BK qualifier (ICD-9) is not checked by CV001
    }

    // ========================================================================
    // CV002 — Valid CPT Code Format
    // ========================================================================

    [Theory]
    [InlineData("99213", true)]
    [InlineData("36415", true)]
    [InlineData("9921", false)]     // too short
    [InlineData("ABC", false)]      // invalid
    public async Task CV002_cpt_format_validation(string code, bool shouldPass)
    {
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = code,
                    ServiceDate = RecentDate(), ChargeAmount = 100m, Units = 1,
                },
            };
            c.TotalClaimedAmount = 100m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv002 = result.Results.First(r => r.RuleId == "CV002");

        Assert.Equal(shouldPass, cv002.Passed);
    }

    [Fact]
    public async Task CV002_allows_hcpcs_codes_starting_with_letter()
    {
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = "J1234",
                    ServiceDate = RecentDate(), ChargeAmount = 100m, Units = 1,
                },
            };
            c.TotalClaimedAmount = 100m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv002 = result.Results.First(r => r.RuleId == "CV002");

        Assert.True(cv002.Passed); // HCPCS codes forgiven by CV002
    }

    // ========================================================================
    // CV004 — Revenue Code (837I only)
    // ========================================================================

    [Fact]
    public async Task CV004_not_applied_to_professional_claims()
    {
        var claim = CreateValidClaim(); // 837P
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.DoesNotContain(result.Results, r => r.RuleId == "CV004");
    }

    [Fact]
    public async Task CV004_valid_revenue_code_passes()
    {
        var claim = CreateValidClaim(c =>
        {
            c.ClaimType = "837I";
            c.ServiceLines[0].RevenueCode = "0250";
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv004 = result.Results.FirstOrDefault(r => r.RuleId == "CV004");

        Assert.NotNull(cv004);
        Assert.True(cv004.Passed);
    }

    [Fact]
    public async Task CV004_invalid_revenue_code_rejected()
    {
        var claim = CreateValidClaim(c =>
        {
            c.ClaimType = "837I";
            c.ServiceLines[0].RevenueCode = "ABC";
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var cv004 = result.Results.First(r => r.RuleId == "CV004");

        Assert.False(cv004.Passed);
    }

    // ========================================================================
    // CV005 — Place of Service Code (837P only)
    // ========================================================================

    [Theory]
    [InlineData("11", true)]
    [InlineData("21", true)]
    [InlineData("99", true)]
    [InlineData("98", false)]
    [InlineData("00", false)]
    public async Task CV005_place_of_service_validation(string pos, bool shouldPass)
    {
        var claim = CreateValidClaim(c => c.ClaimHeader.PlaceOfServiceCode = pos);

        var result = await _engine.ValidateClaimAsync(claim);
        var cv005 = result.Results.First(r => r.RuleId == "CV005");

        Assert.Equal(shouldPass, cv005.Passed);
    }

    // ========================================================================
    // DL001 — Service Date Not Future
    // ========================================================================

    [Fact]
    public async Task DL001_future_service_date_rejected()
    {
        var futureDate = DateTime.UtcNow.AddYears(1).ToString("yyyyMMdd");
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = "99213",
                    ServiceDate = futureDate, ChargeAmount = 100m, Units = 1,
                },
            };
            c.TotalClaimedAmount = 100m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dl001 = result.Results.First(r => r.RuleId == "DL001");

        Assert.False(dl001.Passed);
    }

    [Fact]
    public async Task DL001_past_service_date_passes()
    {
        var claim = CreateValidClaim(); // uses 7 days ago
        var result = await _engine.ValidateClaimAsync(claim);
        var dl001 = result.Results.First(r => r.RuleId == "DL001");

        Assert.True(dl001.Passed);
    }

    // ========================================================================
    // DL002 — Filing Limit (warning)
    // ========================================================================

    [Fact]
    public async Task DL002_old_service_date_flagged_as_warning()
    {
        var oldDate = DateTime.UtcNow.AddDays(-400).ToString("yyyyMMdd");
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = "99213",
                    ServiceDate = oldDate, ChargeAmount = 100m, Units = 1,
                },
            };
            c.TotalClaimedAmount = 100m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dl002 = result.Results.First(r => r.RuleId == "DL002");

        Assert.False(dl002.Passed);
        Assert.Equal("warning", dl002.Severity);
    }

    // ========================================================================
    // DL003 — Discharge After Admission (837I only)
    // ========================================================================

    [Fact]
    public async Task DL003_discharge_before_admission_rejected()
    {
        var claim = CreateValidClaim(c =>
        {
            c.ClaimType = "837I";
            c.ClaimHeader.AdmissionDate = "20240115";
            c.ClaimHeader.DischargeDate = "20240114"; // before admission
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dl003 = result.Results.First(r => r.RuleId == "DL003");

        Assert.False(dl003.Passed);
    }

    [Fact]
    public async Task DL003_not_applied_to_professional_claims()
    {
        var claim = CreateValidClaim(); // 837P
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.DoesNotContain(result.Results, r => r.RuleId == "DL003");
    }

    // ========================================================================
    // DL004 — Patient DOB Before Service
    // ========================================================================

    [Fact]
    public async Task DL004_service_before_dob_rejected()
    {
        var futureDob = DateTime.UtcNow.AddYears(1).ToString("yyyyMMdd");
        var claim = CreateValidClaim(c => c.Subscriber.DateOfBirth = futureDob);

        var result = await _engine.ValidateClaimAsync(claim);
        var dl004 = result.Results.First(r => r.RuleId == "DL004");

        Assert.False(dl004.Passed);
    }

    // ========================================================================
    // AL001 — Charge Amounts Positive
    // ========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task AL001_non_positive_charge_rejected(decimal amount)
    {
        var claim = CreateValidClaim(c =>
        {
            c.ServiceLines = new()
            {
                new ServiceLine
                {
                    LineNumber = 1, ProcedureCode = "99213",
                    ServiceDate = RecentDate(), ChargeAmount = amount, Units = 1,
                },
            };
            c.TotalClaimedAmount = amount;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var al001 = result.Results.First(r => r.RuleId == "AL001");

        Assert.False(al001.Passed);
    }

    // ========================================================================
    // AL002 — Total Matches Line Sum (warning)
    // ========================================================================

    [Fact]
    public async Task AL002_mismatched_total_flagged()
    {
        var claim = CreateValidClaim(c => c.TotalClaimedAmount = 999.99m); // lines sum to 250

        var result = await _engine.ValidateClaimAsync(claim);
        var al002 = result.Results.First(r => r.RuleId == "AL002");

        Assert.False(al002.Passed);
        Assert.Equal("warning", al002.Severity);
    }

    [Fact]
    public async Task AL002_matching_total_passes()
    {
        var claim = CreateValidClaim(); // total = 250, lines sum = 250
        var result = await _engine.ValidateClaimAsync(claim);
        var al002 = result.Results.First(r => r.RuleId == "AL002");

        Assert.True(al002.Passed);
    }

    [Fact]
    public async Task AL002_within_penny_tolerance_passes()
    {
        var claim = CreateValidClaim(c => c.TotalClaimedAmount = 250.01m);
        var result = await _engine.ValidateClaimAsync(claim);
        var al002 = result.Results.First(r => r.RuleId == "AL002");

        Assert.True(al002.Passed);
    }

    // ========================================================================
    // AL003 — Units Positive
    // ========================================================================

    [Fact]
    public async Task AL003_negative_units_rejected()
    {
        var claim = CreateValidClaim(c => c.ServiceLines[0].Units = -1);
        var result = await _engine.ValidateClaimAsync(claim);
        var al003 = result.Results.First(r => r.RuleId == "AL003");

        Assert.False(al003.Passed);
    }

    [Fact]
    public async Task AL003_zero_units_rejected()
    {
        var claim = CreateValidClaim(c => c.ServiceLines[0].Units = 0);
        var result = await _engine.ValidateClaimAsync(claim);
        var al003 = result.Results.First(r => r.RuleId == "AL003");

        Assert.False(al003.Passed);
    }

    // ========================================================================
    // PV001 — Valid NPI Format (Luhn)
    // ========================================================================

    [Fact]
    public async Task PV001_valid_npi_passes()
    {
        var claim = CreateValidClaim(c => c.BillingProvider.Npi = "1234567893");
        var result = await _engine.ValidateClaimAsync(claim);
        var pv001 = result.Results.First(r => r.RuleId == "PV001");

        Assert.True(pv001.Passed);
    }

    [Fact]
    public async Task PV001_invalid_luhn_npi_rejected()
    {
        var claim = CreateValidClaim(c => c.BillingProvider.Npi = "1234567890");
        var result = await _engine.ValidateClaimAsync(claim);
        var pv001 = result.Results.First(r => r.RuleId == "PV001");

        Assert.False(pv001.Passed);
    }

    [Fact]
    public async Task PV001_short_npi_rejected()
    {
        var claim = CreateValidClaim(c => c.BillingProvider.Npi = "12345");
        var result = await _engine.ValidateClaimAsync(claim);
        var pv001 = result.Results.First(r => r.RuleId == "PV001");

        Assert.False(pv001.Passed);
    }

    [Fact]
    public async Task PV001_non_digit_npi_rejected()
    {
        var claim = CreateValidClaim(c => c.BillingProvider.Npi = "ABCDEFGHIJ");
        var result = await _engine.ValidateClaimAsync(claim);
        var pv001 = result.Results.First(r => r.RuleId == "PV001");

        Assert.False(pv001.Passed);
    }

    // ========================================================================
    // PV002 — Tax ID Format (warning)
    // ========================================================================

    [Fact]
    public async Task PV002_valid_ein_passes()
    {
        var claim = CreateValidClaim(c =>
        {
            c.BillingProvider.TaxId = "123456789";
            c.BillingProvider.TaxIdQualifier = "EI";
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var pv002 = result.Results.First(r => r.RuleId == "PV002");

        Assert.True(pv002.Passed);
    }

    [Fact]
    public async Task PV002_invalid_ein_flagged()
    {
        var claim = CreateValidClaim(c =>
        {
            c.BillingProvider.TaxId = "12345";
            c.BillingProvider.TaxIdQualifier = "EI";
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var pv002 = result.Results.First(r => r.RuleId == "PV002");

        Assert.False(pv002.Passed);
        Assert.Equal("warning", pv002.Severity);
    }

    // ========================================================================
    // MV001 — Valid Modifier Format
    // ========================================================================

    [Fact]
    public async Task MV001_valid_modifiers_pass()
    {
        var claim = CreateValidClaim(c =>
            c.ServiceLines[0].Modifiers = new() { "25", "GT" });

        var result = await _engine.ValidateClaimAsync(claim);
        var mv001 = result.Results.First(r => r.RuleId == "MV001");

        Assert.True(mv001.Passed);
    }

    [Fact]
    public async Task MV001_three_char_modifier_rejected()
    {
        var claim = CreateValidClaim(c =>
            c.ServiceLines[0].Modifiers = new() { "ABC" });

        var result = await _engine.ValidateClaimAsync(claim);
        var mv001 = result.Results.First(r => r.RuleId == "MV001");

        Assert.False(mv001.Passed);
    }

    // ========================================================================
    // MV002 — No Duplicate Modifiers
    // ========================================================================

    [Fact]
    public async Task MV002_duplicate_modifiers_rejected()
    {
        var claim = CreateValidClaim(c =>
            c.ServiceLines[0].Modifiers = new() { "25", "25" });

        var result = await _engine.ValidateClaimAsync(claim);
        var mv002 = result.Results.First(r => r.RuleId == "MV002");

        Assert.False(mv002.Passed);
    }

    [Fact]
    public async Task MV002_unique_modifiers_pass()
    {
        var claim = CreateValidClaim(c =>
            c.ServiceLines[0].Modifiers = new() { "25", "59" });

        var result = await _engine.ValidateClaimAsync(claim);
        var mv002 = result.Results.First(r => r.RuleId == "MV002");

        Assert.True(mv002.Passed);
    }

    // ========================================================================
    // Routing decisions
    // ========================================================================

    [Fact]
    public async Task Clean_claim_routes_to_adjudication()
    {
        var claim = CreateValidClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("adjudication", result.Routing.Destination);
        Assert.False(result.Routing.RequiresManualReview);
    }

    [Fact]
    public async Task Claim_with_errors_routes_to_work_queue_high_priority()
    {
        var claim = CreateValidClaim(c => c.Subscriber.MemberId = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("work-queue", result.Routing.Destination);
        Assert.Equal("claims-errors", result.Routing.QueueName);
        Assert.Equal("high", result.Routing.Priority);
        Assert.True(result.Routing.RequiresManualReview);
    }

    [Fact]
    public async Task Claim_with_only_warnings_routes_to_work_queue_medium()
    {
        var claim = CreateValidClaim(c => c.TotalClaimedAmount = 999.99m);
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("flagged", result.Status);
        Assert.Equal("work-queue", result.Routing.Destination);
        Assert.Equal("claims-warnings", result.Routing.QueueName);
        Assert.Equal("medium", result.Routing.Priority);
    }

    // ========================================================================
    // Rule filtering (skipRules / onlyRules)
    // ========================================================================

    [Fact]
    public async Task SkipRules_excludes_specified_rules()
    {
        var claim = CreateValidClaim(c => c.Subscriber.MemberId = "");
        var result = await _engine.ValidateClaimAsync(claim, skipRules: new() { "DC001" });

        Assert.DoesNotContain(result.Results, r => r.RuleId == "DC001");
    }

    [Fact]
    public async Task OnlyRules_runs_only_specified_rules()
    {
        var claim = CreateValidClaim();
        var result = await _engine.ValidateClaimAsync(claim, onlyRules: new() { "DC001", "DC002" });

        Assert.Equal(2, result.RulesExecuted);
        Assert.All(result.Results, r => Assert.Contains(r.RuleId, new[] { "DC001", "DC002" }));
    }

    // ========================================================================
    // Claim type filtering
    // ========================================================================

    [Fact]
    public async Task Professional_claim_does_not_run_837I_only_rules()
    {
        var claim = CreateValidClaim(); // 837P
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.DoesNotContain(result.Results, r => r.RuleId == "CV004"); // revenue code — 837I only
        Assert.DoesNotContain(result.Results, r => r.RuleId == "DL003"); // discharge — 837I only
    }

    [Fact]
    public async Task Institutional_claim_does_not_run_837P_only_rules()
    {
        var claim = CreateValidClaim(c => c.ClaimType = "837I");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.DoesNotContain(result.Results, r => r.RuleId == "CV002"); // CPT — 837P only
        Assert.DoesNotContain(result.Results, r => r.RuleId == "CV005"); // POS — 837P only
    }

    // ========================================================================
    // Parallel execution
    // ========================================================================

    [Fact]
    public async Task Parallel_execution_produces_same_results()
    {
        var claim = CreateValidClaim();

        var seqResult = await _engine.ValidateClaimAsync(claim, parallelExecution: false);
        var parResult = await _engine.ValidateClaimAsync(claim, parallelExecution: true);

        Assert.Equal(seqResult.Status, parResult.Status);
        Assert.Equal(seqResult.RulesExecuted, parResult.RulesExecuted);
        Assert.Equal(seqResult.ErrorCount, parResult.ErrorCount);
        Assert.Equal(seqResult.WarningCount, parResult.WarningCount);
    }

    // ========================================================================
    // Performance metadata
    // ========================================================================

    [Fact]
    public async Task Result_includes_execution_time()
    {
        var claim = CreateValidClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.True(result.TotalValidationTimeMs >= 0);
        Assert.All(result.Results, r => Assert.NotNull(r.ExecutionTimeMs));
    }

    // ========================================================================
    // Custom rule registration
    // ========================================================================

    [Fact]
    public void AddRule_adds_custom_rule()
    {
        var engine = new ValidationRuleEngine(NullLogger<ValidationRuleEngine>.Instance);
        engine.AddRule(new ValidationRule
        {
            RuleId = "CUSTOM001", RuleName = "Custom Test", Description = "Test",
            Category = "custom", Severity = "warning",
            AppliesTo = new() { "837P" }, Enabled = true, Priority = 100, Type = "custom"
        });

        Assert.Contains(engine.GetRules(), r => r.RuleId == "CUSTOM001");
    }

    [Fact]
    public async Task Custom_rule_stub_always_passes()
    {
        var engine = new ValidationRuleEngine(NullLogger<ValidationRuleEngine>.Instance);
        engine.AddRule(new ValidationRule
        {
            RuleId = "CUSTOM001", RuleName = "Custom Test", Description = "Test",
            Category = "custom", Severity = "error",
            AppliesTo = new() { "837P" }, Enabled = true, Priority = 100, Type = "custom"
        });

        var claim = CreateValidClaim();
        var result = await engine.ValidateClaimAsync(claim);
        var custom = result.Results.First(r => r.RuleId == "CUSTOM001");

        Assert.True(custom.Passed);
    }

    // ========================================================================
    // Multiple errors accumulate
    // ========================================================================

    [Fact]
    public async Task Multiple_validation_failures_all_reported()
    {
        var claim = CreateValidClaim(c =>
        {
            c.Subscriber.MemberId = "";       // DC001
            c.Subscriber.DateOfBirth = "";     // DC002
            c.BillingProvider.Npi = "";        // DC003
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.True(result.ErrorCount >= 3);
        Assert.Contains(result.Results, r => r.RuleId == "DC001" && !r.Passed);
        Assert.Contains(result.Results, r => r.RuleId == "DC002" && !r.Passed);
        Assert.Contains(result.Results, r => r.RuleId == "DC003" && !r.Passed);
    }

    // ========================================================================
    // Status determination
    // ========================================================================

    [Fact]
    public async Task Errors_only_produces_rejected()
    {
        var claim = CreateValidClaim(c => c.Subscriber.MemberId = "");
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.False(result.FirstPassEligible);
    }

    [Fact]
    public async Task Warnings_only_produces_flagged()
    {
        var claim = CreateValidClaim(c => c.TotalClaimedAmount = 999.99m);
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("flagged", result.Status);
        Assert.False(result.FirstPassEligible);
    }

    [Fact]
    public async Task No_failures_produces_clean()
    {
        var claim = CreateValidClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("clean", result.Status);
        Assert.True(result.FirstPassEligible);
    }
}
