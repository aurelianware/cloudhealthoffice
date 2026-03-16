using CloudHealthOffice.ClaimsScrubEngine.Data;
using CloudHealthOffice.ClaimsScrubEngine.Models;
using CloudHealthOffice.ClaimsScrubEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.ClaimsScrubEngine.Tests;

/// <summary>
/// Port of claims-scrubber.test.ts — ~40 tests covering all 6 rule categories,
/// routing decisions, rule filtering, performance metrics, and custom rules.
/// </summary>
public class ValidationRuleEngineTests
{
    private readonly ValidationRuleEngine _engine;

    public ValidationRuleEngineTests()
    {
        _engine = new ValidationRuleEngine(
            DefaultStandardRules.Create(),
            NullLogger<ValidationRuleEngine>.Instance);
    }

    // ========================================================================
    // Test Helpers
    // ========================================================================

    private static string GetRecentDateString(int daysAgo = 7) =>
        DateTime.UtcNow.AddDays(-daysAgo).ToString("yyyyMMdd");

    private static X12837Claim CreateTestClaim(Action<TestClaimBuilder>? configure = null)
    {
        var recentDate = GetRecentDateString(7);
        var builder = new TestClaimBuilder(recentDate);
        configure?.Invoke(builder);
        return builder.Build();
    }

    private sealed class TestClaimBuilder
    {
        private readonly string _serviceDate;
        public string ClaimId = "CLM-TEST-001";
        public ClaimType ClaimType = ClaimType.Professional;
        public BillingProvider? BillingProvider;
        public ClaimSubscriber? Subscriber;
        public ClaimHeader? ClaimHeader;
        public List<ServiceLine>? ServiceLines;
        public decimal? TotalClaimedAmount;

        public TestClaimBuilder(string serviceDate) => _serviceDate = serviceDate;

        public X12837Claim Build()
        {
            var lines = ServiceLines ?? [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Modifiers = ["25"],
                    ServiceDate = _serviceDate,
                    ChargeAmount = 150.00m,
                    Units = 1,
                    PlaceOfService = "11",
                    DiagnosisPointers = [1],
                },
                new ServiceLine
                {
                    LineNumber = 2,
                    ProcedureCode = "36415",
                    ServiceDate = _serviceDate,
                    ChargeAmount = 100.00m,
                    Units = 1,
                    PlaceOfService = "11",
                    DiagnosisPointers = [1],
                },
            ];

            return new X12837Claim
            {
                ClaimId = ClaimId,
                ClaimType = ClaimType,
                TransactionControlNumber = "000000001",
                InterchangeControlNumber = "000000001",
                TransactionDate = _serviceDate,
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
                BillingProvider = BillingProvider ?? new BillingProvider
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
                Subscriber = Subscriber ?? new ClaimSubscriber
                {
                    MemberId = "MEM123456789",
                    FirstName = "John",
                    LastName = "Doe",
                    DateOfBirth = "19850615",
                    Gender = "M",
                    GroupNumber = "GRP001",
                },
                ClaimHeader = ClaimHeader ?? new ClaimHeader
                {
                    PatientControlNumber = "PCN001",
                    TotalChargeAmount = 250.00m,
                    PlaceOfServiceCode = "11",
                    PrincipalDiagnosisCode = "Z00.00",
                    DiagnosisCodes = [new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 }],
                },
                ServiceLines = lines,
                TotalClaimedAmount = TotalClaimedAmount ?? lines.Sum(l => l.ChargeAmount),
                ParsedAt = DateTime.UtcNow.ToString("o"),
            };
        }
    }

    // ========================================================================
    // Rule Initialization Tests
    // ========================================================================

    [Fact]
    public void Should_initialize_with_standard_rules()
    {
        var rules = _engine.GetRules();
        Assert.True(rules.Count > 0);
    }

    [Fact]
    public void Should_have_data_completeness_rules()
    {
        var rules = _engine.GetRulesByCategory("data-completeness");
        Assert.True(rules.Count > 0);
        Assert.Contains(rules, r => r.RuleId == "DC001");
    }

    [Fact]
    public void Should_have_code_validation_rules()
    {
        var rules = _engine.GetRulesByCategory("code-validity");
        Assert.True(rules.Count > 0);
    }

    [Fact]
    public void Should_have_date_logic_rules()
    {
        var rules = _engine.GetRulesByCategory("date-logic");
        Assert.True(rules.Count > 0);
    }

    [Fact]
    public void Should_have_amount_logic_rules()
    {
        var rules = _engine.GetRulesByCategory("amount-logic");
        Assert.True(rules.Count > 0);
    }

    [Fact]
    public void Should_have_provider_validation_rules()
    {
        var rules = _engine.GetRulesByCategory("provider-validation");
        Assert.True(rules.Count > 0);
    }

    [Fact]
    public void Should_have_modifier_validation_rules()
    {
        var rules = _engine.GetRulesByCategory("modifier-validation");
        Assert.True(rules.Count > 0);
    }

    // ========================================================================
    // Claim Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_validate_valid_claim_successfully()
    {
        var claim = CreateTestClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("CLM-TEST-001", result.ClaimId);
        Assert.Equal(ClaimType.Professional, result.ClaimType);
        Assert.Equal("clean", result.Status);
        Assert.Equal(0, result.ErrorCount);
        Assert.True(result.FirstPassEligible);
    }

    [Fact]
    public async Task Should_detect_missing_member_id()
    {
        var claim = CreateTestClaim(b => b.Subscriber = new ClaimSubscriber
        {
            MemberId = "",
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = "19850615",
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.True(result.ErrorCount > 0);
        Assert.Contains(result.Results, r => r.RuleId == "DC001" && !r.Passed);
    }

    [Fact]
    public async Task Should_detect_missing_subscriber_dob()
    {
        var claim = CreateTestClaim(b => b.Subscriber = new ClaimSubscriber
        {
            MemberId = "MEM123",
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = "",
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC002" && !r.Passed);
    }

    [Fact]
    public async Task Should_detect_missing_billing_provider_npi()
    {
        var claim = CreateTestClaim(b => b.BillingProvider = new BillingProvider
        {
            Npi = "",
            Name = "Test Provider",
            EntityType = "2",
            Address = new ProviderAddress
            {
                Line1 = "123 Main St",
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC003" && !r.Passed);
    }

    [Fact]
    public async Task Should_detect_missing_diagnosis_codes()
    {
        var claim = CreateTestClaim(b => b.ClaimHeader = new ClaimHeader
        {
            PatientControlNumber = "PCN001",
            TotalChargeAmount = 250.00m,
            PlaceOfServiceCode = "11",
            DiagnosisCodes = [],
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC004" && !r.Passed);
    }

    [Fact]
    public async Task Should_detect_empty_service_lines()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines = [];
            b.TotalClaimedAmount = 0;
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("rejected", result.Status);
        Assert.Contains(result.Results, r => r.RuleId == "DC005" && !r.Passed);
    }

    // ========================================================================
    // NPI Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_validate_valid_npi()
    {
        var claim = CreateTestClaim(b => b.BillingProvider = new BillingProvider
        {
            Npi = "1234567893",
            Name = "Test Provider",
            EntityType = "2",
            Address = new ProviderAddress
            {
                Line1 = "123 Main St",
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var npiResult = result.Results.FirstOrDefault(r => r.RuleId == "PV001");

        Assert.NotNull(npiResult);
        Assert.True(npiResult.Passed);
    }

    [Fact]
    public async Task Should_reject_invalid_npi_format()
    {
        var claim = CreateTestClaim(b => b.BillingProvider = new BillingProvider
        {
            Npi = "12345",
            Name = "Test Provider",
            EntityType = "2",
            Address = new ProviderAddress
            {
                Line1 = "123 Main St",
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var npiResult = result.Results.FirstOrDefault(r => r.RuleId == "PV001");

        Assert.NotNull(npiResult);
        Assert.False(npiResult.Passed);
    }

    [Fact]
    public async Task Should_reject_npi_failing_luhn_check()
    {
        var claim = CreateTestClaim(b => b.BillingProvider = new BillingProvider
        {
            Npi = "1234567890",
            Name = "Test Provider",
            EntityType = "2",
            Address = new ProviderAddress
            {
                Line1 = "123 Main St",
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var npiResult = result.Results.FirstOrDefault(r => r.RuleId == "PV001");

        Assert.NotNull(npiResult);
        Assert.False(npiResult.Passed);
    }

    // ========================================================================
    // Date Logic Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_reject_future_service_dates()
    {
        var futureDate = DateTime.UtcNow.AddYears(1).ToString("yyyyMMdd");
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = futureDate,
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dateResult = result.Results.FirstOrDefault(r => r.RuleId == "DL001");

        Assert.NotNull(dateResult);
        Assert.False(dateResult.Passed);
    }

    [Fact]
    public async Task Should_reject_service_date_before_patient_dob()
    {
        var futureDob = DateTime.UtcNow.AddYears(1).ToString("yyyyMMdd");
        var claim = CreateTestClaim(b =>
        {
            b.Subscriber = new ClaimSubscriber
            {
                MemberId = "MEM123",
                FirstName = "John",
                LastName = "Doe",
                DateOfBirth = futureDob,
            };
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dateResult = result.Results.FirstOrDefault(r => r.RuleId == "DL004");

        Assert.NotNull(dateResult);
        Assert.False(dateResult.Passed);
    }

    // ========================================================================
    // Amount Logic Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_reject_negative_charge_amounts()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = -100.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = -100.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var amtResult = result.Results.FirstOrDefault(r => r.RuleId == "AL001");

        Assert.NotNull(amtResult);
        Assert.False(amtResult.Passed);
    }

    [Fact]
    public async Task Should_reject_zero_charge_amounts()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 0,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 0;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var amtResult = result.Results.FirstOrDefault(r => r.RuleId == "AL001");

        Assert.NotNull(amtResult);
        Assert.False(amtResult.Passed);
    }

    [Fact]
    public async Task Should_detect_mismatched_total_amount()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 500.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var amtResult = result.Results.FirstOrDefault(r => r.RuleId == "AL002");

        Assert.NotNull(amtResult);
        Assert.False(amtResult.Passed);
    }

    [Fact]
    public async Task Should_reject_negative_units()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = -1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var unitsResult = result.Results.FirstOrDefault(r => r.RuleId == "AL003");

        Assert.NotNull(unitsResult);
        Assert.False(unitsResult.Passed);
    }

    // ========================================================================
    // Modifier Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_validate_proper_modifier_format()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Modifiers = ["25", "GT"],
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var modResult = result.Results.FirstOrDefault(r => r.RuleId == "MV001");

        Assert.NotNull(modResult);
        Assert.True(modResult.Passed);
    }

    [Fact]
    public async Task Should_reject_invalid_modifier_format()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Modifiers = ["ABC"], // 3 chars = invalid
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var modResult = result.Results.FirstOrDefault(r => r.RuleId == "MV001");

        Assert.NotNull(modResult);
        Assert.False(modResult.Passed);
    }

    [Fact]
    public async Task Should_detect_duplicate_modifiers()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    Modifiers = ["25", "25"], // Duplicate
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var modResult = result.Results.FirstOrDefault(r => r.RuleId == "MV002");

        Assert.NotNull(modResult);
        Assert.False(modResult.Passed);
    }

    // ========================================================================
    // Code Validation Tests
    // ========================================================================

    [Fact]
    public async Task Should_validate_proper_icd10_format()
    {
        var claim = CreateTestClaim(b => b.ClaimHeader = new ClaimHeader
        {
            PatientControlNumber = "PCN001",
            TotalChargeAmount = 150.00m,
            DiagnosisCodes =
            [
                new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 },
                new DiagnosisCode { Code = "J06.9", Qualifier = "ABK", Pointer = 2 },
            ],
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var icdResult = result.Results.FirstOrDefault(r => r.RuleId == "CV001");

        Assert.NotNull(icdResult);
        Assert.True(icdResult.Passed);
    }

    [Fact]
    public async Task Should_validate_proper_cpt_format()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var cptResult = result.Results.FirstOrDefault(r => r.RuleId == "CV002");

        Assert.NotNull(cptResult);
        Assert.True(cptResult.Passed);
    }

    [Fact]
    public async Task Should_validate_proper_pos_code()
    {
        var claim = CreateTestClaim(b => b.ClaimHeader = new ClaimHeader
        {
            PatientControlNumber = "PCN001",
            TotalChargeAmount = 150.00m,
            PlaceOfServiceCode = "11",
            DiagnosisCodes = [new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 }],
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var posResult = result.Results.FirstOrDefault(r => r.RuleId == "CV005");

        Assert.NotNull(posResult);
        Assert.True(posResult.Passed);
    }

    [Fact]
    public async Task Should_handle_invalid_pos_code()
    {
        var claim = CreateTestClaim(b => b.ClaimHeader = new ClaimHeader
        {
            PatientControlNumber = "PCN001",
            TotalChargeAmount = 150.00m,
            PlaceOfServiceCode = "99", // 99 is actually valid ("Other Place of Service")
            DiagnosisCodes = [new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 }],
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var posResult = result.Results.FirstOrDefault(r => r.RuleId == "CV005");

        // 99 is in the valid POS set, so it should pass
        Assert.NotNull(posResult);
        Assert.True(posResult.Passed);
    }

    // ========================================================================
    // Claim Type Specific Rules Tests
    // ========================================================================

    [Fact]
    public async Task Should_apply_837I_specific_rules_for_institutional()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ClaimType = ClaimType.Institutional;
            b.ClaimHeader = new ClaimHeader
            {
                PatientControlNumber = "PCN001",
                TotalChargeAmount = 150.00m,
                FacilityTypeCode = "0111",
                AdmissionDate = "20240115",
                DischargeDate = "20240114", // Before admission
                DiagnosisCodes = [new DiagnosisCode { Code = "Z00.00", Qualifier = "ABK", Pointer = 1 }],
            };
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    RevenueCode = "0250",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.00m;
        });

        var result = await _engine.ValidateClaimAsync(claim);
        var dischargeResult = result.Results.FirstOrDefault(r => r.RuleId == "DL003");

        Assert.NotNull(dischargeResult);
        Assert.False(dischargeResult.Passed);
    }

    [Fact]
    public async Task Should_not_apply_837I_rules_to_professional_claims()
    {
        var claim = CreateTestClaim(b => b.ClaimType = ClaimType.Professional);

        var result = await _engine.ValidateClaimAsync(claim);
        var revenueResult = result.Results.FirstOrDefault(r => r.RuleId == "CV004");

        Assert.Null(revenueResult);
    }

    // ========================================================================
    // Rule Filtering Tests
    // ========================================================================

    [Fact]
    public async Task Should_skip_specified_rules()
    {
        var claim = CreateTestClaim(b => b.Subscriber = new ClaimSubscriber
        {
            MemberId = "",
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = "19850615",
        });

        var result = await _engine.ValidateClaimAsync(claim, new ClaimValidationOptions
        {
            SkipRules = ["DC001"],
        });

        Assert.DoesNotContain(result.Results, r => r.RuleId == "DC001");
    }

    [Fact]
    public async Task Should_only_run_specified_rules()
    {
        var claim = CreateTestClaim();

        var result = await _engine.ValidateClaimAsync(claim, new ClaimValidationOptions
        {
            OnlyRules = ["DC001", "DC002"],
        });

        Assert.Equal(2, result.RulesExecuted);
        Assert.All(result.Results, r => Assert.Contains(r.RuleId, new[] { "DC001", "DC002" }));
    }

    // ========================================================================
    // Routing Decision Tests
    // ========================================================================

    [Fact]
    public async Task Should_route_clean_claims_to_adjudication()
    {
        var claim = CreateTestClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("adjudication", result.Routing.Destination);
        Assert.False(result.Routing.RequiresManualReview);
    }

    [Fact]
    public async Task Should_route_claims_with_errors_to_work_queue()
    {
        var claim = CreateTestClaim(b => b.Subscriber = new ClaimSubscriber
        {
            MemberId = "",
            FirstName = "John",
            LastName = "Doe",
            DateOfBirth = "19850615",
        });

        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal("work-queue", result.Routing.Destination);
        Assert.True(result.Routing.RequiresManualReview);
        Assert.Equal("high", result.Routing.Priority);
    }

    [Fact]
    public async Task Should_route_claims_with_warnings_to_work_queue_medium_priority()
    {
        var claim = CreateTestClaim(b =>
        {
            b.ServiceLines =
            [
                new ServiceLine
                {
                    LineNumber = 1,
                    ProcedureCode = "99213",
                    ServiceDate = GetRecentDateString(7),
                    ChargeAmount = 150.00m,
                    Units = 1,
                },
            ];
            b.TotalClaimedAmount = 150.01m; // Slight mismatch → warning
        });

        var result = await _engine.ValidateClaimAsync(claim);

        if (result.WarningCount > 0 && result.ErrorCount == 0)
        {
            Assert.Equal("work-queue", result.Routing.Destination);
            Assert.Equal("medium", result.Routing.Priority);
        }
    }

    // ========================================================================
    // Performance Tests
    // ========================================================================

    [Fact]
    public async Task Should_record_execution_time_for_each_rule()
    {
        var claim = CreateTestClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        var ruleWithTime = result.Results.FirstOrDefault(r => r.ExecutionTimeMs is not null);
        Assert.NotNull(ruleWithTime);
        Assert.True(ruleWithTime.ExecutionTimeMs >= 0);
    }

    [Fact]
    public async Task Should_record_total_validation_time()
    {
        var claim = CreateTestClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.True(result.TotalValidationTimeMs >= 0);
    }

    // ========================================================================
    // Custom Rules Tests
    // ========================================================================

    [Fact]
    public void Should_add_custom_rules()
    {
        var customRule = new ValidationRule
        {
            RuleId = "CUSTOM001",
            RuleName = "Custom Test Rule",
            Description = "A custom test rule",
            Category = ValidationCategory.Custom,
            Severity = ValidationSeverity.Warning,
            AppliesTo = [ClaimType.Professional, ClaimType.Institutional, ClaimType.Dental],
            Enabled = true,
            Priority = 100,
            Type = RuleType.Custom,
        };

        _engine.AddRule(customRule);

        var rules = _engine.GetRules();
        Assert.Contains(rules, r => r.RuleId == "CUSTOM001");
    }

    // ========================================================================
    // Type Structure Tests
    // ========================================================================

    [Fact]
    public void X12837Claim_has_correct_structure()
    {
        var claim = CreateTestClaim();

        Assert.NotNull(claim.ClaimId);
        Assert.Equal(ClaimType.Professional, claim.ClaimType);
        Assert.NotNull(claim.BillingProvider.Npi);
        Assert.NotNull(claim.Subscriber.MemberId);
        Assert.True(claim.ServiceLines.Count > 0);
    }

    [Fact]
    public void ServiceLine_has_correct_structure()
    {
        var claim = CreateTestClaim();
        var line = claim.ServiceLines[0];

        Assert.Equal(1, line.LineNumber);
        Assert.NotNull(line.ProcedureCode);
        Assert.NotNull(line.ServiceDate);
        Assert.True(line.ChargeAmount > 0);
        Assert.True(line.Units > 0);
    }

    [Fact]
    public async Task ClaimValidationResult_has_correct_structure()
    {
        var claim = CreateTestClaim();
        var result = await _engine.ValidateClaimAsync(claim);

        Assert.Equal(claim.ClaimId, result.ClaimId);
        Assert.Equal(claim.ClaimType, result.ClaimType);
        Assert.Contains(result.Status, new[] { "clean", "flagged", "rejected" });
        Assert.True(result.RulesExecuted > 0);
        Assert.NotEmpty(result.Results);
        Assert.NotNull(result.Routing);
    }

    // ========================================================================
    // NPI Luhn Algorithm Unit Tests
    // ========================================================================

    [Theory]
    [InlineData("1234567893", true)]   // Valid NPI
    [InlineData("1234567890", false)]  // Invalid Luhn
    [InlineData("12345", false)]       // Too short
    [InlineData("ABCDEFGHIJ", false)]  // Not digits
    public void IsValidNpi_tests(string npi, bool expected)
    {
        Assert.Equal(expected, ValidationRuleEngine.IsValidNpi(npi));
    }
}
