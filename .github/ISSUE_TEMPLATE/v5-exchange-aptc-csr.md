---
name: 'ACA Exchange - APTC & CSR Support'
about: Add Advanced Premium Tax Credit and Cost Sharing Reduction handling for Exchange plans
title: '[v5.0] ACA Exchange - APTC & CSR Subsidy Management'
labels: 'feature, exchange, priority:high'
assignees: ''
---

## 🎯 Objective

Implement Advanced Premium Tax Credit (APTC) and Cost Sharing Reduction (CSR) subsidy management to support ACA Exchange/Marketplace lines of business. Enable Cloud Health Office to serve individual market health plans and state-based marketplaces.

**Priority:** 🟡 **HIGH** (v5.0 - Q2 2026)  
**Effort:** 4-6 weeks (2 developers)  
**Market Opportunity:** State-based marketplaces, QHP issuers ($2-5M ARR potential)  
**Regulatory:** ACA compliance, IRS Form 1095-A reporting

---

## 📋 Success Criteria

- [ ] APTC calculation engine (monthly premium reduction based on FPL%)
- [ ] CSR plan variant management (73%, 87%, 94% actuarial value tiers)
- [ ] Member subsidy tracking in Coverage Service
- [ ] Monthly subsidy reconciliation workflow (834 → APTC payment)
- [ ] Annual 1095-A tax form generation (IRS reporting)
- [ ] APTC reversal handling (loss of eligibility mid-year)
- [ ] Qualified Life Event (QLE) subsidy recalculation
- [ ] FFM/SBM enrollment data ingestion (834 with subsidy segments)

---

## 🏥 Business Context

### What Are APTC and CSR?

**APTC (Advanced Premium Tax Credit):**
- Monthly subsidy paid to insurers to reduce member premiums
- Calculated based on Federal Poverty Level (FPL) percentage (100%-400%)
- Example: Member qualifies for $450/month APTC, pays $200/month instead of $650/month
- Must reconcile with actual tax credit at year-end (IRS Form 8962)

**CSR (Cost Sharing Reduction):**
- Reduces deductibles, copays, and OOP max for low-income members (100%-250% FPL)
- Creates special plan variants with enhanced actuarial value:
  - **73% AV**: 200%-250% FPL (reduces Silver plan from 70% to 73% AV)
  - **87% AV**: 150%-200% FPL (reduces Silver plan to 87% AV)
  - **94% AV**: 100%-150% FPL (reduces Silver plan to 94% AV)
- Example: Silver plan normally has $4,000 deductible, CSR 87% variant has $1,200 deductible

### Why This Matters

Without APTC/CSR support, Cloud Health Office **cannot support**:
- State-based marketplaces (CA, NY, CO, CT, etc.)
- QHP issuers selling on Exchange
- Individual market health plans
- Medicaid redetermination "step-down" to Exchange

---

## 🔧 Implementation Steps

### Phase 1: Data Model Extensions (Week 1)

**1.1 Add APTC Fields to Coverage Model**

File: `services/coverage-service/Models/Coverage.cs`

```csharp
/// <summary>
/// Exchange subsidy information (APTC + CSR)
/// </summary>
public class ExchangeSubsidyInfo
{
    /// <summary>
    /// Monthly APTC amount (paid to insurer)
    /// </summary>
    public decimal MonthlyAPTC { get; set; }

    /// <summary>
    /// Household income as % of Federal Poverty Level (100-400%)
    /// </summary>
    public decimal FPLPercentage { get; set; }

    /// <summary>
    /// CSR variant (null, 73, 87, 94)
    /// </summary>
    public int? CSRVariant { get; set; }

    /// <summary>
    /// Second Lowest Cost Silver Plan (SLCSP) benchmark premium
    /// </summary>
    public decimal SLCSPBenchmark { get; set; }

    /// <summary>
    /// Member monthly premium after APTC
    /// </summary>
    public decimal MemberMonthlyPremium { get; set; }

    /// <summary>
    /// Full premium (before APTC)
    /// </summary>
    public decimal FullMonthlyPremium { get; set; }

    /// <summary>
    /// APTC effective date (can differ from coverage effective date)
    /// </summary>
    public DateTime APTCEffectiveDate { get; set; }

    /// <summary>
    /// APTC termination date (loss of eligibility)
    /// </summary>
    public DateTime? APTCTerminationDate { get; set; }
}

// Add to Coverage model
public ExchangeSubsidyInfo? ExchangeSubsidy { get; set; }
```

**1.2 Add CSR Plan Variants to BenefitPlan Model**

File: `services/benefit-plan-service/Models/BenefitPlan.cs`

```csharp
/// <summary>
/// CSR plan variant metadata (Exchange plans only)
/// </summary>
public class CSRVariantInfo
{
    /// <summary>
    /// Base plan ID (Silver plan this variant is derived from)
    /// </summary>
    public string BasePlanId { get; set; }

    /// <summary>
    /// CSR variant type (73, 87, 94)
    /// </summary>
    public int CSRVariant { get; set; }

    /// <summary>
    /// Actuarial value percentage (73.0, 87.0, 94.0)
    /// </summary>
    public decimal ActuarialValue { get; set; }

    /// <summary>
    /// FPL range eligibility (e.g., "150-200%" for CSR 87)
    /// </summary>
    public string FPLRange { get; set; }
}

// Add to BenefitPlan model
public CSRVariantInfo? CSRVariant { get; set; }
public bool IsCSRVariant => CSRVariant != null;
```

---

### Phase 2: APTC Calculation Engine (Week 2)

**2.1 Create APTC Calculator Service**

File: `services/coverage-service/Services/APTCCalculator.cs`

```csharp
public interface IAPTCCalculator
{
    Task<decimal> CalculateMonthlyAPTC(
        decimal householdIncome,
        int householdSize,
        int zipCode,
        int year,
        DateTime effectiveDate);
    
    Task<decimal> GetSLCSPBenchmark(int zipCode, int year);
}

public class APTCCalculator : IAPTCCalculator
{
    // Federal Poverty Level lookup table (updated annually)
    private readonly Dictionary<int, decimal> _fplThresholds = new()
    {
        { 1, 15060m },  // 2026 FPL for household of 1
        { 2, 20440m },  // 2026 FPL for household of 2
        { 3, 25820m },  // 2026 FPL for household of 3
        { 4, 31200m },  // 2026 FPL for household of 4
        // ... additional household sizes
    };

    // ACA premium contribution percentages by FPL% (IRS Table)
    // 2026 values (adjusted annually)
    private decimal GetPremiumContributionPercentage(decimal fplPercentage)
    {
        return fplPercentage switch
        {
            <= 150 => 0.00m,    // 0% contribution
            <= 200 => 0.02m,    // 2% contribution
            <= 250 => 0.04m,    // 4% contribution
            <= 300 => 0.06m,    // 6% contribution
            <= 400 => 0.085m,   // 8.5% contribution
            _ => 1.0m           // No subsidy above 400% FPL
        };
    }

    public async Task<decimal> CalculateMonthlyAPTC(
        decimal householdIncome,
        int householdSize,
        int zipCode,
        int year,
        DateTime effectiveDate)
    {
        // 1. Calculate FPL percentage
        var fplThreshold = _fplThresholds[householdSize];
        var fplPercentage = (householdIncome / fplThreshold) * 100;

        // 2. No subsidy above 400% FPL
        if (fplPercentage > 400) return 0m;

        // 3. Get SLCSP benchmark for rating area
        var slcspBenchmark = await GetSLCSPBenchmark(zipCode, year);

        // 4. Calculate required contribution
        var contributionPercentage = GetPremiumContributionPercentage(fplPercentage);
        var monthlyContribution = (householdIncome / 12) * contributionPercentage;

        // 5. APTC = SLCSP - Required Contribution
        var aptc = slcspBenchmark - monthlyContribution;

        return Math.Max(0, aptc); // Can't be negative
    }

    public async Task<decimal> GetSLCSPBenchmark(int zipCode, int year)
    {
        // TODO: Integrate with CMS Rate Review API or state marketplace data
        // For now, return placeholder
        // Actual implementation would query:
        // - CMS Multidimensional Insurance Data Analytics System (MIDAS)
        // - State-based marketplace APIs
        // - Rating area lookup tables
        
        throw new NotImplementedException("Integrate CMS Rate Review API");
    }
}
```

**2.2 Integrate APTC in Enrollment Import**

File: `services/enrollment-import-service/Services/EnrollmentImportService.cs`

```csharp
// Parse 834 subsidy segments (REF*17, REF*18)
private ExchangeSubsidyInfo? ParseSubsidyInfo(MemberEnrollment enrollment)
{
    // 834 transaction includes:
    // REF*17~APTC~450.00  (APTC amount)
    // REF*18~CSR~87       (CSR variant)
    
    if (enrollment.APTCAmount == null) return null;

    return new ExchangeSubsidyInfo
    {
        MonthlyAPTC = enrollment.APTCAmount.Value,
        CSRVariant = enrollment.CSRVariant,
        FPLPercentage = enrollment.FPLPercentage ?? 0,
        SLCSPBenchmark = enrollment.SLCSPBenchmark ?? 0,
        MemberMonthlyPremium = enrollment.MemberPremium ?? 0,
        FullMonthlyPremium = enrollment.FullPremium ?? 0,
        APTCEffectiveDate = enrollment.EffectiveDate,
        APTCTerminationDate = null
    };
}
```

---

### Phase 3: CSR Plan Variant Management (Week 3)

**3.1 CSR Plan Generator**

Create CSR variants automatically from base Silver plans:

File: `services/benefit-plan-service/Services/CSRPlanGenerator.cs`

```csharp
public interface ICSRPlanGenerator
{
    Task<BenefitPlan> GenerateCSRVariant(string basePlanId, int csrVariant);
}

public class CSRPlanGenerator : ICSRPlanGenerator
{
    public async Task<BenefitPlan> GenerateCSRVariant(string basePlanId, int csrVariant)
    {
        // 1. Load base Silver plan
        var basePlan = await _repository.GetByPlanIdAsync(basePlanId);
        
        // 2. Apply CSR adjustments to cost sharing
        var csrPlan = basePlan.Clone();
        csrPlan.PlanId = $"{basePlanId}-CSR{csrVariant}";
        csrPlan.PlanName = $"{basePlan.PlanName} (CSR {csrVariant})";
        
        // 3. Reduce cost sharing based on CSR variant
        var reductionFactor = csrVariant switch
        {
            73 => 0.96m,  // Slight reduction
            87 => 0.80m,  // Moderate reduction
            94 => 0.60m,  // Significant reduction
            _ => 1.0m
        };
        
        csrPlan.CostSharing.IndividualDeductible *= reductionFactor;
        csrPlan.CostSharing.FamilyDeductible *= reductionFactor;
        csrPlan.CostSharing.IndividualOutOfPocketMax *= reductionFactor;
        csrPlan.CostSharing.FamilyOutOfPocketMax *= reductionFactor;
        
        // 4. Store CSR variant metadata
        csrPlan.CSRVariant = new CSRVariantInfo
        {
            BasePlanId = basePlanId,
            CSRVariant = csrVariant,
            ActuarialValue = csrVariant,
            FPLRange = csrVariant switch
            {
                73 => "200-250%",
                87 => "150-200%",
                94 => "100-150%",
                _ => ""
            }
        };
        
        return csrPlan;
    }
}
```

---

### Phase 4: Subsidy Reconciliation (Week 4)

**4.1 Monthly APTC Reconciliation**

File: `services/billing-service/Services/SubsidyReconciliationService.cs`

```csharp
public class SubsidyReconciliationService
{
    /// <summary>
    /// Reconcile APTC payments for a coverage month
    /// </summary>
    public async Task<ReconciliationResult> ReconcileMonthlyAPTC(
        string memberId,
        int year,
        int month)
    {
        // 1. Get coverage for month
        var coverage = await _coverageService.GetActiveCoverage(memberId, new DateTime(year, month, 1));
        
        if (coverage?.ExchangeSubsidy == null)
            return ReconciliationResult.NotApplicable;
        
        // 2. Get premium payment records
        var payment = await _paymentService.GetPremiumPayment(memberId, year, month);
        
        // 3. Verify APTC amount matches
        var expectedAPTC = coverage.ExchangeSubsidy.MonthlyAPTC;
        var actualAPTC = payment?.APTCAmount ?? 0;
        
        if (expectedAPTC != actualAPTC)
        {
            // Log discrepancy for investigation
            _logger.LogWarning(
                "APTC mismatch for member {MemberId} {Year}-{Month}: Expected {Expected}, Actual {Actual}",
                memberId, year, month, expectedAPTC, actualAPTC);
        }
        
        return new ReconciliationResult
        {
            MemberId = memberId,
            Year = year,
            Month = month,
            ExpectedAPTC = expectedAPTC,
            ActualAPTC = actualAPTC,
            Variance = actualAPTC - expectedAPTC,
            IsReconciled = expectedAPTC == actualAPTC
        };
    }
}
```

**4.2 Annual 1095-A Tax Form Generation**

File: `services/reporting-service/Services/Form1095AGenerator.cs`

```csharp
/// <summary>
/// Generate IRS Form 1095-A for annual tax reporting
/// Required for members who received APTC
/// </summary>
public class Form1095AGenerator
{
    public async Task<Form1095A> GenerateForm(string memberId, int taxYear)
    {
        // 1. Get all coverage months with APTC
        var coverageHistory = await _coverageService.GetCoverageHistory(
            memberId, 
            new DateTime(taxYear, 1, 1),
            new DateTime(taxYear, 12, 31));
        
        var monthsWithAPTC = coverageHistory
            .Where(c => c.ExchangeSubsidy?.MonthlyAPTC > 0)
            .ToList();
        
        // 2. Build 1095-A form
        return new Form1095A
        {
            MemberId = memberId,
            TaxYear = taxYear,
            RecipientInfo = await GetRecipientInfo(memberId),
            
            // Part II: Monthly coverage and premium information
            MonthlyPremiums = monthsWithAPTC.Select(c => new MonthlyPremiumInfo
            {
                Month = c.EffectiveDate.Month,
                SLCSPBenchmark = c.ExchangeSubsidy.SLCSPBenchmark,
                EnrolledPremium = c.ExchangeSubsidy.FullMonthlyPremium,
                AdvancedPTC = c.ExchangeSubsidy.MonthlyAPTC
            }).ToList(),
            
            // Part III: Annual totals
            TotalSLCSP = monthsWithAPTC.Sum(c => c.ExchangeSubsidy.SLCSPBenchmark),
            TotalEnrolledPremium = monthsWithAPTC.Sum(c => c.ExchangeSubsidy.FullMonthlyPremium),
            TotalAdvancedPTC = monthsWithAPTC.Sum(c => c.ExchangeSubsidy.MonthlyAPTC)
        };
    }
}
```

---

### Phase 5: Eligibility Change Handling (Week 5-6)

**5.1 APTC Reversal (Loss of Eligibility)**

```csharp
/// <summary>
/// Handle mid-year loss of APTC eligibility
/// Triggers: Income increase, household size change, Medicaid eligibility
/// </summary>
public async Task HandleAPTCReversal(string memberId, DateTime effectiveDate, string reason)
{
    var coverage = await _coverageService.GetActiveCoverage(memberId, effectiveDate);
    
    if (coverage?.ExchangeSubsidy == null) return;
    
    // 1. Terminate APTC
    coverage.ExchangeSubsidy.APTCTerminationDate = effectiveDate;
    coverage.ExchangeSubsidy.MonthlyAPTC = 0;
    
    // 2. Update member premium to full amount
    coverage.MonthlyPremium = coverage.ExchangeSubsidy.FullMonthlyPremium;
    
    // 3. Notify member of premium increase
    await _notificationService.SendPremiumChangeNotice(
        memberId,
        coverage.ExchangeSubsidy.MemberMonthlyPremium,
        coverage.MonthlyPremium,
        effectiveDate,
        reason);
    
    // 4. Generate 834 change transaction
    await _enrollmentService.Generate834Change(coverage, "APTC Termination");
    
    await _coverageService.UpdateCoverage(coverage);
}
```

**5.2 Qualified Life Event (QLE) Recalculation**

```csharp
/// <summary>
/// Recalculate APTC after Qualified Life Event
/// Examples: Marriage, birth, income change, job loss
/// </summary>
public async Task RecalculateAPTCForQLE(
    string memberId,
    QualifiedLifeEvent qle,
    decimal newHouseholdIncome,
    int newHouseholdSize)
{
    var coverage = await _coverageService.GetActiveCoverage(memberId, qle.EventDate);
    
    // 1. Recalculate APTC with new household information
    var newAPTC = await _aptcCalculator.CalculateMonthlyAPTC(
        newHouseholdIncome,
        newHouseholdSize,
        coverage.MemberZipCode,
        qle.EventDate.Year,
        qle.EventDate);
    
    // 2. Update coverage
    coverage.ExchangeSubsidy.MonthlyAPTC = newAPTC;
    coverage.ExchangeSubsidy.FPLPercentage = 
        (newHouseholdIncome / GetFPL(newHouseholdSize)) * 100;
    coverage.MonthlyPremium = 
        coverage.ExchangeSubsidy.FullMonthlyPremium - newAPTC;
    
    // 3. Effective date = QLE event date (or first of following month)
    var effectiveDate = new DateTime(qle.EventDate.Year, qle.EventDate.Month, 1)
        .AddMonths(1);
    
    await _coverageService.UpdateCoverage(coverage);
    
    // 4. Generate 834 change transaction
    await _enrollmentService.Generate834Change(coverage, $"QLE: {qle.EventType}");
}
```

---

## 📊 Database Schema Changes

### Coverage Collection (Cosmos DB)

```json
{
  "id": "COV_12345",
  "tenantId": "TENANT_001",
  "memberId": "MEM_98765",
  "lineOfBusiness": "Exchange",
  "exchangeSubsidy": {
    "monthlyAPTC": 450.00,
    "fplPercentage": 185.5,
    "csrVariant": 87,
    "slcspBenchmark": 650.00,
    "memberMonthlyPremium": 200.00,
    "fullMonthlyPremium": 650.00,
    "aptcEffectiveDate": "2026-01-01T00:00:00Z",
    "aptcTerminationDate": null
  }
}
```

---

## 🧪 Testing Plan

### Unit Tests
- [ ] APTC calculation for various FPL percentages (100%-400%)
- [ ] CSR variant generation (73%, 87%, 94%)
- [ ] Subsidy reversal logic
- [ ] 1095-A form generation

### Integration Tests
- [ ] 834 enrollment with APTC segments (REF*17, REF*18)
- [ ] Monthly reconciliation workflow
- [ ] QLE subsidy recalculation end-to-end

### Test Scenarios
```csharp
[Fact]
public void CalculateAPTC_At200PercentFPL_Returns$450()
{
    // Member with household income at 200% FPL
    var aptc = _calculator.CalculateMonthlyAPTC(
        householdIncome: 40880m,  // 200% of FPL for family of 2
        householdSize: 2,
        zipCode: 10001,
        year: 2026,
        effectiveDate: new DateTime(2026, 1, 1));
    
    Assert.Equal(450m, aptc, precision: 2);
}

[Fact]
public void GenerateCSR87Variant_ReducesDeductible()
{
    var basePlan = CreateBaseSilverPlan(deductible: 5000m);
    var csrPlan = _generator.GenerateCSRVariant(basePlan.PlanId, 87);
    
    // CSR 87 should reduce deductible to ~$1,000
    Assert.InRange(csrPlan.CostSharing.IndividualDeductible, 900m, 1100m);
    Assert.Equal(87m, csrPlan.CSRVariant.ActuarialValue);
}
```

---

## 📚 Documentation Updates

### New Documents to Create
- [ ] `docs/EXCHANGE-APTC-GUIDE.md` - APTC calculation methodology
- [ ] `docs/CSR-PLAN-VARIANTS.md` - CSR variant generation
- [ ] `docs/SUBSIDY-RECONCILIATION.md` - Monthly and annual reconciliation
- [ ] `docs/FORM-1095A-GENERATION.md` - Tax reporting guide

### Update Existing Docs
- [ ] Update `FEATURES.md` - Add Exchange subsidy management
- [ ] Update `README.md` - Add Exchange to supported lines of business
- [ ] Update API documentation - Add APTC endpoints

---

## 🔗 Dependencies

### External Data Sources
- **CMS Rate Review API**: SLCSP benchmark premiums by rating area
- **Federal Poverty Level Tables**: Updated annually (HHS)
- **IRS Premium Contribution Tables**: Updated annually

### Prerequisite Issues
- None (standalone feature)

### Blocks
- #376 (QHP Certification & EHB Validation) - needs CSR plans
- #377 (Exchange Enrollment Periods) - needs APTC eligibility dates

---

## 💰 Financial Impact

### Revenue Opportunity
- **State-based marketplaces**: 10-15 potential customers ($200K-500K ARR each)
- **QHP issuers**: 50+ mid-size plans needing Exchange support ($50K-150K ARR each)
- **Total TAM**: $5-10M ARR in Exchange market segment

### Development Cost
- 4-6 weeks × 2 developers × $150/hr = $48K-72K
- External consulting (ACA compliance review): $10K-15K
- **Total**: $58K-87K investment

### ROI Timeline
- Break-even: 1-2 customer wins ($200K-500K ARR)
- Expected first sale: Q3 2026 (state marketplace RFP season)

---

## ✅ Acceptance Criteria

### Functional Requirements
- [ ] APTC calculated correctly for all FPL ranges (100%-400%)
- [ ] CSR 73/87/94 variants auto-generated from Silver plans
- [ ] 834 enrollment imports APTC and CSR data
- [ ] Monthly premium = Full premium - APTC
- [ ] 1095-A forms generate accurately for tax year
- [ ] APTC reversal adjusts premium immediately
- [ ] QLE triggers subsidy recalculation

### Non-Functional Requirements
- [ ] APTC calculation performance: <100ms
- [ ] FPL tables stored in config (not hardcoded)
- [ ] Audit trail for all subsidy changes
- [ ] HIPAA-compliant logging (no PII in logs)

---

## 📖 References

- [IRS Form 1095-A Instructions](https://www.irs.gov/pub/irs-pdf/i1095a.pdf)
- [ACA Premium Tax Credit Tables](https://www.irs.gov/affordable-care-act/individuals-and-families/premium-tax-credit-claiming-the-credit-and-reconciling-advance-credit-payments)
- [HHS Federal Poverty Guidelines](https://aspe.hhs.gov/topics/poverty-economic-mobility/poverty-guidelines)
- [CMS Cost Sharing Reduction](https://www.cms.gov/cciio/resources/fact-sheets-and-faqs/csr-payment)
- [X12 834 Subsidy Segments](https://x12.org/products/x12-transaction-sets/834-benefit-enrollment-maintenance)

---

**Labels:** `feature`, `exchange`, `priority:high`, `effort:large`, `v5.0`  
**Milestone:** v5.0 - Exchange Market Expansion (Q2 2026)
