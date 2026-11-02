using CloudHealthOffice.FeeScheduleEngine.Configuration;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Medicaid;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudHealthOffice.FeeScheduleEngine.Tests.Medicaid;

/// <summary>
/// Claim pricing by the Texas Medicaid APR-DRG method: a claim that names
/// the method is priced out of the imported HHSC tables and allocated across
/// its lines; anything that stops that pends the whole claim; every other
/// claim goes to the engine untouched.
/// </summary>
public class MedicaidDrgRateResolutionServiceTests
{
    private const string Tenant = "tenant-1";
    private const string Hospital = "1234567893";
    private const string Physician = "1999999992";

    private readonly InMemoryMedicaidDrgRateRepository _rates = new();
    private readonly RecordingEngine _engine = new();

    private MedicaidDrgRateResolutionService Sut() =>
        new(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance);

    private static readonly DateTime Discharge = new(2026, 10, 5);

    public MedicaidDrgRateResolutionServiceTests()
    {
        LoadWeights(new DateTime(2025, 9, 1), ("139-3", 1.2m, 5.0m, 12m), ("540-1", 0.5m, 2.5m, 6m));
        LoadHospitals("Urban", new DateTime(2026, 9, 1), (Hospital, "100000001", 6000m, null));
    }

    private void LoadWeights(DateTime effective, params (string Key, decimal Weight, decimal Mlos, decimal Threshold)[] rows)
    {
        _rates.UpsertWeightTableAsync(new MedicaidDrgWeightTable
        {
            Id = MedicaidDrgWeightTable.MakeId(Tenant, "TX", effective),
            TenantId = Tenant,
            State = "TX",
            EffectiveDate = effective,
            GrouperVersion = "40",
            UniversalMean = 10161.08m,
            Rows = rows.Select(r => new MedicaidDrgWeightRow
            {
                DrgSoi = r.Key, RelativeWeight = r.Weight, MeanLengthOfStay = r.Mlos, DayOutlierThreshold = r.Threshold,
            }).ToList(),
        }).GetAwaiter().GetResult();
    }

    private void LoadHospitals(string hospitalClass, DateTime effective,
        params (string Npi, string Tpi, decimal Sda, decimal? DeliverySda)[] rows)
    {
        _rates.UpsertHospitalTableAsync(new MedicaidHospitalRateTable
        {
            Id = MedicaidHospitalRateTable.MakeId(Tenant, "TX", hospitalClass, effective),
            TenantId = Tenant,
            State = "TX",
            HospitalClass = hospitalClass,
            EffectiveDate = effective,
            Rows = rows.Select(r => new MedicaidHospitalRateRow
            {
                Npi = r.Npi, StateProviderId = r.Tpi, HospitalName = "Test Hospital",
                BaseRate = r.Sda, FinalBaseRate = r.Sda, DeliveryBaseRate = r.DeliverySda,
            }).ToList(),
        }).GetAwaiter().GetResult();
    }

    private static List<PricingRequest> Stay(
        string drg = "139-3", int age = 45, string status = "01", int los = 4,
        string? method = MedicaidDrgMethods.Texas, params decimal[] billed)
    {
        if (billed.Length == 0) billed = [3000m, 1000m];
        return billed.Select((amount, i) => new PricingRequest
        {
            TenantId = Tenant,
            ProcedureCode = string.Empty,
            RevenueCode = i == 0 ? "0120" : "0250",
            ProviderNpi = Physician,
            FacilityNpi = Hospital,
            ServiceDate = Discharge.AddDays(-los),
            BilledAmount = amount,
            LineNumber = i + 1,
            TotalLineCount = billed.Length,
            DrgCode = drg,
            LengthOfStay = los,
            IsInstitutional = true,
            MedicaidDrgMethod = method,
            DischargeStatus = status,
            PatientAgeYears = age,
            DischargeDate = Discharge,
        }).ToList();
    }

    [Fact]
    public async Task A_claim_without_a_method_goes_to_the_engine_untouched()
    {
        var requests = Stay(method: null);

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.Same(_engine.Answer, result);
        Assert.Equal(1, _engine.BatchCalls);
    }

    [Fact]
    public async Task None_is_the_same_as_no_method()
    {
        await Sut().ResolveBatchAsync(Stay(method: MedicaidDrgMethods.None));

        Assert.Equal(1, _engine.BatchCalls);
    }

    [Fact]
    public async Task An_adult_stay_pays_sda_times_weight_once_allocated_by_charges()
    {
        var result = await Sut().ResolveBatchAsync(Stay());

        Assert.Equal(0, _engine.BatchCalls);
        Assert.Equal(7200m, result.TotalAllowedAmount); // $6,000 × 1.2
        Assert.Equal([5400m, 1800m], result.LineResults.Select(r => r.AllowedAmount));
        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Medicaid, r.RateSource);
            Assert.Equal(FeeScheduleType.Drg, r.FeeScheduleType);
            Assert.True(r.IsPerStayRate);
            Assert.Equal(7200m, r.BaseAmount);
            Assert.Contains(r.CalculationSteps, s => s.Contains("relative weight 1.2", StringComparison.Ordinal));
            Assert.Equal(MedicaidDrgWeightTable.MakeId(Tenant, "TX", new DateTime(2025, 9, 1)), r.FeeScheduleId);
        });
    }

    [Fact]
    public async Task The_shares_always_add_up_to_the_stay_payment()
    {
        var result = await Sut().ResolveBatchAsync(Stay(billed: [100m, 100m, 100m]));

        Assert.Equal(7200m, result.TotalAllowedAmount);
        Assert.Equal([2400m, 2400m, 2400m], result.LineResults.Select(r => r.AllowedAmount));
    }

    [Fact]
    public async Task The_hospital_is_found_by_the_facility_npi_not_the_rendering_physician()
    {
        var requests = Stay().Select(r => r with { FacilityNpi = null }).ToList();

        var result = await Sut().ResolveBatchAsync(requests);

        // Falls back to ProviderNpi, which is the physician here: no SDA row.
        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Unresolved, r.RateSource);
            Assert.Contains(Physician, r.UnresolvedReason);
        });
    }

    [Fact]
    public async Task The_weight_table_in_force_on_the_discharge_date_is_used()
    {
        LoadWeights(new DateTime(2026, 11, 1), ("139-3", 2.0m, 5.0m, 12m));

        var result = await Sut().ResolveBatchAsync(Stay());

        Assert.Equal(7200m, result.TotalAllowedAmount); // the 2025 table, not next month's
    }

    [Fact]
    public async Task A_rural_delivery_stay_is_paid_at_the_delivery_sda()
    {
        LoadHospitals("Rural", new DateTime(2026, 9, 1), ("1111111112", "100000002", 9000m, 12000m));
        var requests = Stay(drg: "540-1").Select(r => r with { FacilityNpi = "1111111112" }).ToList();

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.Equal(6000m, result.TotalAllowedAmount); // $12,000 × 0.5
    }

    [Fact]
    public async Task A_short_drg_number_is_padded_to_the_state_key()
    {
        LoadWeights(new DateTime(2026, 1, 1), ("011-1", 10m, 20m, 40m));

        var result = await Sut().ResolveBatchAsync(Stay(drg: "11-1"));

        Assert.Equal(60000m, result.TotalAllowedAmount);
    }

    [Theory]
    [InlineData("139", "not a DRG-SOI key")]
    [InlineData("", "not a DRG-SOI key")]
    [InlineData("139-9", "not a DRG-SOI key")]
    [InlineData("721-2", "No Texas APR-DRG relative weight")]
    public async Task A_stay_without_a_usable_weight_pends(string drg, string reason)
    {
        var result = await Sut().ResolveBatchAsync(Stay(drg: drg));

        Assert.Equal(0, _engine.BatchCalls);
        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Unresolved, r.RateSource);
            Assert.Equal(0m, r.AllowedAmount);
            Assert.Contains(reason, r.UnresolvedReason);
        });
    }

    [Fact]
    public async Task A_discharge_before_any_weight_table_pends()
    {
        var requests = Stay().Select(r => r with { DischargeDate = new DateTime(2024, 1, 1) }).ToList();

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.All(result.LineResults, r => Assert.Contains("no Texas APR-DRG weight table", r.UnresolvedReason));
    }

    [Fact]
    public async Task A_hospital_not_in_the_sda_files_pends_and_is_not_priced_by_contract()
    {
        var requests = Stay().Select(r => r with { FacilityNpi = "1111111112" }).ToList();

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.Equal(0, _engine.BatchCalls);
        Assert.All(result.LineResults, r => Assert.Contains("no Texas SDA is loaded for hospital NPI 1111111112", r.UnresolvedReason));
    }

    [Fact]
    public async Task Two_rows_for_the_same_npi_that_disagree_pend()
    {
        LoadHospitals("Rural", new DateTime(2026, 9, 1), (Hospital, "100000009", 6500m, null));

        var result = await Sut().ResolveBatchAsync(Stay());

        Assert.All(result.LineResults, r => Assert.Contains("SDA rows that disagree", r.UnresolvedReason));
    }

    [Fact]
    public async Task A_client_under_21_pends_until_interim_rates_are_loaded()
    {
        var result = await Sut().ResolveBatchAsync(Stay(age: 15));

        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Unresolved, r.RateSource);
            Assert.Contains("interim rate", r.UnresolvedReason);
            Assert.NotEmpty(r.CalculationSteps);
        });
    }

    [Fact]
    public async Task A_client_under_21_is_priced_once_the_interim_rate_is_loaded()
    {
        var interim = new InMemoryMedicaidInterimRateRepository();
        await interim.UpsertAsync([new MedicaidInterimRate
        {
            Id = MedicaidInterimRate.MakeId(Tenant, "TX", Hospital, new DateTime(2026, 9, 1)),
            TenantId = Tenant, State = "TX", Npi = Hospital, EffectiveDate = new DateTime(2026, 9, 1),
            InpatientRate = 0.5m, SourceFile = "interim.csv",
        }]);
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance, interim);

        // 4 days, below the day outlier threshold. Cost = $4,000 × 0.5 = $2,000,
        // far below the cost outlier threshold, so no outlier: the DRG payment alone.
        var result = await sut.ResolveBatchAsync(Stay(age: 15));

        Assert.Equal(7200m, result.TotalAllowedAmount);
        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Medicaid, r.RateSource);
            Assert.StartsWith("Inpatient interim rate 0.5 effective 2026-09-01", r.CalculationSteps[0]);
        });
    }

    [Fact]
    public async Task A_high_cost_child_stay_earns_a_cost_outlier_from_the_interim_rate()
    {
        var interim = new InMemoryMedicaidInterimRateRepository();
        await interim.UpsertAsync([new MedicaidInterimRate
        {
            Id = MedicaidInterimRate.MakeId(Tenant, "TX", Hospital, new DateTime(2026, 9, 1)),
            TenantId = Tenant, State = "TX", Npi = Hospital, EffectiveDate = new DateTime(2026, 9, 1),
            InpatientRate = 0.5m, SourceFile = "interim.csv",
        }]);
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance, interim);

        // Threshold = max(11.14 × min(10,161.08, 6,000), 1.5 × 7,200) = $66,840.
        // Cost = $200,000 × 0.5 = $100,000; outlier = (100,000 − 66,840) × 60% × 90% = $17,906.40.
        var result = await sut.ResolveBatchAsync(Stay(age: 15, billed: [200000m]));

        Assert.Equal(7200m + 17906.40m, result.TotalAllowedAmount);
    }

    [Fact]
    public async Task An_interim_rate_effective_after_discharge_is_not_used()
    {
        var interim = new InMemoryMedicaidInterimRateRepository();
        await interim.UpsertAsync([new MedicaidInterimRate
        {
            Id = MedicaidInterimRate.MakeId(Tenant, "TX", Hospital, new DateTime(2026, 11, 1)),
            TenantId = Tenant, State = "TX", Npi = Hospital, EffectiveDate = new DateTime(2026, 11, 1),
            InpatientRate = 0.5m, SourceFile = "interim.csv",
        }]);
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance, interim);

        var result = await sut.ResolveBatchAsync(Stay(age: 15));

        Assert.All(result.LineResults, r => Assert.Contains("interim rate", r.UnresolvedReason));
    }

    [Fact]
    public async Task An_adult_transfer_is_paid_the_per_diem()
    {
        var result = await Sut().ResolveBatchAsync(Stay(status: "02", los: 2));

        Assert.Equal(2880m, result.TotalAllowedAmount); // 7,200 ÷ MLOS 5 × 2 days
    }

    [Fact]
    public async Task A_stay_with_no_length_of_stay_pends()
    {
        var requests = Stay().Select(r => r with { LengthOfStay = null }).ToList();

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.All(result.LineResults, r => Assert.Contains("no length of stay", r.UnresolvedReason));
    }

    [Fact]
    public async Task Lines_that_disagree_on_the_drg_pend()
    {
        var requests = Stay();
        requests[1] = requests[1] with { DrgCode = "140-1" };

        var result = await Sut().ResolveBatchAsync(requests);

        Assert.All(result.LineResults, r => Assert.Contains("different DRGs", r.UnresolvedReason));
    }

    [Fact]
    public async Task An_unknown_method_pends()
    {
        var result = await Sut().ResolveBatchAsync(Stay(method: "ohio"));

        Assert.All(result.LineResults, r => Assert.Contains("not supported", r.UnresolvedReason));
    }

    [Fact]
    public async Task A_single_line_is_priced_the_same_way()
    {
        var result = await Sut().ResolveAsync(Stay(billed: [5000m])[0]);

        Assert.Equal(7200m, result.AllowedAmount);
        Assert.Empty(result.Adjustments);
    }

    private InMemoryMedicaidPsychiatricPerDiemRepository PsychRates(params (DateTime Effective, decimal Rate)[] rates)
    {
        var store = new InMemoryMedicaidPsychiatricPerDiemRepository();
        store.UpsertAsync(rates.Select(r => new MedicaidPsychiatricPerDiem
        {
            Id = MedicaidPsychiatricPerDiem.MakeId(Tenant, "TX", Hospital, r.Effective),
            TenantId = Tenant, State = "TX", Npi = Hospital, HospitalName = "Test Psychiatric Hospital",
            FacilityClass = "Private Freestanding", EffectiveDate = r.Effective, DailyRate = r.Rate, SourceFile = "psych.pdf",
        }).ToList()).GetAwaiter().GetResult();
        return store;
    }

    private static List<PricingRequest> PsychStay(int age, DateTime admission, int days) =>
    [
        new()
        {
            TenantId = Tenant, ProcedureCode = string.Empty, RevenueCode = "0114", ProviderNpi = Physician, FacilityNpi = Hospital,
            ServiceDate = admission, Units = days, BilledAmount = 9000m, LineNumber = 1, TotalLineCount = 2,
            DrgCode = "753-2", LengthOfStay = days, IsInstitutional = true, MedicaidDrgMethod = MedicaidDrgMethods.Texas,
            PatientAgeYears = age, DischargeDate = admission.AddDays(days),
        },
        new()
        {
            TenantId = Tenant, ProcedureCode = string.Empty, RevenueCode = "0250", ProviderNpi = Physician, FacilityNpi = Hospital,
            ServiceDate = admission, Units = 3, BilledAmount = 600m, LineNumber = 2, TotalLineCount = 2,
            DrgCode = "753-2", LengthOfStay = days, IsInstitutional = true, MedicaidDrgMethod = MedicaidDrgMethods.Texas,
            PatientAgeYears = age, DischargeDate = admission.AddDays(days),
        },
    ];

    [Fact]
    public async Task A_psychiatric_hospital_is_paid_its_per_diem_not_a_drg()
    {
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance,
            psychRates: PsychRates((new DateTime(2026, 9, 1), 563.36m)));

        var result = await sut.ResolveBatchAsync(PsychStay(16, new DateTime(2026, 10, 1), 7));

        var room = result.LineResults.Single(r => r.LineNumber == 1);
        Assert.Equal(RateSource.Medicaid, room.RateSource);
        Assert.Equal(FeeScheduleType.PerDiem, room.FeeScheduleType);
        Assert.Equal(3943.52m, room.AllowedAmount); // 7 × 563.36
        Assert.True(room.IsPerStayRate);
        Assert.Equal(0m, result.LineResults.Single(r => r.LineNumber == 2).AllowedAmount);
        Assert.Contains(room.CalculationSteps, s => s.Contains("not a DRG"));
        Assert.Equal(0, _engine.BatchCalls);
    }

    [Fact]
    public async Task A_psychiatric_stay_over_the_adult_imd_limit_pends()
    {
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance,
            psychRates: PsychRates((new DateTime(2026, 9, 1), 563.36m)));

        var result = await sut.ResolveBatchAsync(PsychStay(35, new DateTime(2026, 10, 1), 16));

        Assert.All(result.LineResults, r =>
        {
            Assert.Equal(RateSource.Unresolved, r.RateSource);
            Assert.Contains("15-day monthly limit", r.UnresolvedReason);
        });
    }

    [Fact]
    public async Task A_psychiatric_stay_across_a_new_fiscal_year_pays_each_day_at_its_rate()
    {
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance,
            psychRates: PsychRates((new DateTime(2025, 9, 1), 529.59m), (new DateTime(2026, 9, 1), 563.36m)));

        // Aug 28 for 7 days: 4 days at the SFY 2026 rate, 3 at SFY 2027.
        var result = await sut.ResolveBatchAsync(PsychStay(16, new DateTime(2026, 8, 28), 7));

        Assert.Equal(4 * 529.59m + 3 * 563.36m, result.LineResults.Single(r => r.LineNumber == 1).AllowedAmount);
    }

    [Fact]
    public async Task A_hospital_without_a_per_diem_is_priced_by_drg()
    {
        var sut = new MedicaidDrgRateResolutionService(_engine, _rates, NullLogger<MedicaidDrgRateResolutionService>.Instance,
            psychRates: new InMemoryMedicaidPsychiatricPerDiemRepository());

        var result = await sut.ResolveBatchAsync(Stay());

        Assert.All(result.LineResults, r => Assert.Equal(FeeScheduleType.Drg, r.FeeScheduleType));
        Assert.Equal(7200m, result.LineResults.Sum(r => r.AllowedAmount));
    }

    [Fact]
    public void The_builder_wraps_the_registered_engine()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IMedicaidDrgRateRepository>(_rates);
        services.AddFeeScheduleEngine();
        services.AddScoped<IRateResolutionService>(_ => _engine);

        new FeeScheduleEngineBuilder(services).UseMedicaidDrgPricing();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<MedicaidDrgRateResolutionService>(scope.ServiceProvider.GetRequiredService<IRateResolutionService>());
    }

    /// <summary>Stands in for the contract and fee schedule engine.</summary>
    private sealed class RecordingEngine : IRateResolutionService
    {
        public PricingResultSet Answer { get; } = new();
        public int BatchCalls { get; private set; }

        public Task<PricingResult> ResolveAsync(PricingRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PricingResult { LineNumber = request.LineNumber });

        public Task<PricingResultSet> ResolveBatchAsync(IReadOnlyList<PricingRequest> requests, CancellationToken ct = default)
        {
            BatchCalls++;
            return Task.FromResult(Answer);
        }
    }
}
