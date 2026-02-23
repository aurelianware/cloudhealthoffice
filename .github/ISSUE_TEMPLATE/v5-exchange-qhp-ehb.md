---
name: 'ACA Exchange - QHP Certification & EHB Validation'
about: Implement Qualified Health Plan certification tracking and Essential Health Benefits validation
title: '[v5.0] ACA Exchange - QHP Certification & EHB Compliance'
labels: 'feature, exchange, compliance, priority:high'
assignees: ''
---

## 🎯 Objective

Implement Qualified Health Plan (QHP) certification tracking and Essential Health Benefits (EHB) validation to ensure Exchange plans meet ACA requirements. Enable accurate plan comparison, benefit compliance monitoring, and regulatory reporting.

**Priority:** 🟡 **HIGH** (v5.0 - Q2 2026)  
**Effort:** 3-4 weeks (1-2 developers)  
**Regulatory:** ACA Section 1311 compliance, state insurance department certification  
**Depends On:** #375 (APTC/CSR Support) - CSR plan variants need EHB validation

---

## 📋 Success Criteria

- [ ] EHB category tracking for all Exchange benefit plans
- [ ] QHP certification status and metadata storage
- [ ] Plan comparison tool (apples-to-apples across metal tiers)
- [ ] Actuarial value (AV) calculator and validator
- [ ] Essential Community Provider (ECP) network compliance tracking
- [ ] Maximum Out-of-Pocket (MOOP) limit enforcement (updated annually)
- [ ] Deductible limit validation for HSA-qualified HDHPs
- [ ] Plan year rollover workflow (new QHP IDs annually)

---

## 🏥 Business Context

### What is a Qualified Health Plan (QHP)?

**QHP Requirements:**
- Must cover all 10 Essential Health Benefits (EHB)
- Meet minimum actuarial value (60% Bronze, 70% Silver, 80% Gold, 90% Platinum)
- Network adequacy standards (sufficient providers by specialty)
- Essential Community Provider (ECP) participation (20% of network)
- Maximum Out-of-Pocket limits (updated annually by HHS)
- State insurance department certification
- Annual recertification required

**Why This Matters:**
- Plans sold on Exchange MUST be QHP-certified
- Non-compliant plans face penalties and decertification
- State-based marketplaces audit QHP status quarterly
- Members can only use APTC/CSR with certified QHPs

---

## 🔧 Implementation Steps

### Phase 1: EHB Category Tracking (Week 1)

**1.1 Add EHB Metadata to Benefit Model**

File: `services/benefit-plan-service/Models/BenefitPlan.cs`

```csharp
/// <summary>
/// Essential Health Benefit categories (ACA-required)
/// All QHPs must cover all 10 categories
/// </summary>
public enum EHBCategory
{
    [Description("Ambulatory Patient Services")]
    AmbulatoryPatientServices = 1,
    
    [Description("Emergency Services")]
    EmergencyServices = 2,
    
    [Description("Hospitalization")]
    Hospitalization = 3,
    
    [Description("Maternity and Newborn Care")]
    MaternityAndNewbornCare = 4,
    
    [Description("Mental Health and Substance Use Disorder Services")]
    MentalHealthAndSubstanceUse = 5,
    
    [Description("Prescription Drugs")]
    PrescriptionDrugs = 6,
    
    [Description("Rehabilitative and Habilitative Services and Devices")]
    RehabilitativeAndHabilitative = 7,
    
    [Description("Laboratory Services")]
    LaboratoryServices = 8,
    
    [Description("Preventive and Wellness Services and Chronic Disease Management")]
    PreventiveAndWellness = 9,
    
    [Description("Pediatric Services, including Oral and Vision Care")]
    PediatricServices = 10
}

/// <summary>
/// Extended Benefit model with EHB category
/// </summary>
public class Benefit
{
    // ... existing properties ...
    
    /// <summary>
    /// Essential Health Benefit category (required for QHPs)
    /// </summary>
    [JsonPropertyName("ehbCategory")]
    public EHBCategory? EHBCategory { get; set; }
    
    /// <summary>
    /// Is this benefit categorized as an EHB?
    /// </summary>
    [JsonPropertyName("isEssentialHealthBenefit")]
    public bool IsEssentialHealthBenefit { get; set; }
}
```

**1.2 Add QHP Certification Metadata**

```csharp
/// <summary>
/// QHP certification information (Exchange plans only)
/// </summary>
public class QHPCertificationInfo
{
    /// <summary>
    /// QHP ID assigned by CMS or state marketplace
    /// Format: {State}{Issuer}{Plan}{Variant} (14 chars)
    /// Example: 11512NC0040001 (GA BCBS Silver 001)
    /// </summary>
    [StringLength(14, MinimumLength = 14)]
    public string QHPId { get; set; } = string.Empty;
    
    /// <summary>
    /// Standard Component ID (HIOS ID)
    /// </summary>
    [StringLength(10)]
    public string HIOSId { get; set; } = string.Empty;
    
    /// <summary>
    /// Plan year (QHP IDs change annually)
    /// </summary>
    public int PlanYear { get; set; }
    
    /// <summary>
    /// Certification status
    /// </summary>
    public QHPCertificationStatus Status { get; set; }
    
    /// <summary>
    /// Date plan was certified
    /// </summary>
    public DateTime CertificationDate { get; set; }
    
    /// <summary>
    /// Certification expiration (Dec 31 of plan year)
    /// </summary>
    public DateTime ExpirationDate { get; set; }
    
    /// <summary>
    /// State insurance department that certified the plan
    /// </summary>
    [StringLength(2)]
    public string CertifyingState { get; set; } = string.Empty;
    
    /// <summary>
    /// Certifying marketplace (FFM or SBM)
    /// </summary>
    public string Marketplace { get; set; } = "FFM"; // FFM or state code
    
    /// <summary>
    /// Actuarial value percentage (calculated)
    /// Must match metal tier: Bronze 60%, Silver 70%, Gold 80%, Platinum 90%
    /// </summary>
    [Range(0, 100)]
    public decimal ActuarialValue { get; set; }
    
    /// <summary>
    /// Is this plan available on-Exchange?
    /// </summary>
    public bool IsOnExchange { get; set; }
    
    /// <summary>
    /// Is this plan available off-Exchange?
    /// </summary>
    public bool IsOffExchange { get; set; }
    
    /// <summary>
    /// Service area counties (FIPS codes)
    /// </summary>
    public List<string> ServiceAreaCounties { get; set; } = new();
}

public enum QHPCertificationStatus
{
    Pending,
    Certified,
    Decertified,
    Expired,
    UnderReview
}

// Add to BenefitPlan model
public QHPCertificationInfo? QHPCertification { get; set; }
public bool IsQHP => QHPCertification?.Status == QHPCertificationStatus.Certified;
```

---

### Phase 2: EHB Validation Engine (Week 2)

**2.1 Create EHB Compliance Validator**

File: `services/benefit-plan-service/Services/EHBValidator.cs`

```csharp
public interface IEHBValidator
{
    Task<EHBValidationResult> ValidatePlanCompliance(string planId);
    Task<List<EHBCategory>> GetMissingEHBCategories(string planId);
}

public class EHBValidator : IEHBValidator
{
    public async Task<EHBValidationResult> ValidatePlanCompliance(string planId)
    {
        var plan = await _repository.GetByPlanIdAsync(planId);
        
        if (plan == null)
            return EHBValidationResult.PlanNotFound;
        
        // 1. Check that all 10 EHB categories are covered
        var coveredCategories = plan.Benefits
            .Where(b => b.IsEssentialHealthBenefit && b.EHBCategory.HasValue)
            .Select(b => b.EHBCategory.Value)
            .Distinct()
            .ToList();
        
        var allEHBCategories = Enum.GetValues<EHBCategory>();
        var missingCategories = allEHBCategories
            .Except(coveredCategories)
            .ToList();
        
        // 2. Validate actuarial value matches metal tier
        var expectedAV = plan.MetalLevel switch
        {
            MetalLevel.Bronze => 60m,
            MetalLevel.Silver => 70m,
            MetalLevel.Gold => 80m,
            MetalLevel.Platinum => 90m,
            MetalLevel.Catastrophic => 58m, // Catastrophic plans can be <60%
            _ => 0m
        };
        
        var avVariance = Math.Abs(plan.QHPCertification.ActuarialValue - expectedAV);
        var avCompliant = avVariance <= 2m; // Allow ±2% variance
        
        // 3. Validate MOOP limits
        var moopCompliant = ValidateMOOPLimits(plan);
        
        // 4. Check deductible for HSA-qualified plans
        var deductibleCompliant = ValidateHSADeductible(plan);
        
        return new EHBValidationResult
        {
            IsCompliant = missingCategories.Count == 0 && avCompliant && moopCompliant && deductibleCompliant,
            CoveredEHBCategories = coveredCategories.Count,
            TotalEHBCategoriesRequired = allEHBCategories.Length,
            MissingCategories = missingCategories,
            ActuarialValueCompliant = avCompliant,
            ActuarialValue = plan.QHPCertification?.ActuarialValue ?? 0,
            ExpectedActuarialValue = expectedAV,
            MOOPCompliant = moopCompliant,
            DeductibleCompliant = deductibleCompliant,
            ValidationDate = DateTime.UtcNow
        };
    }
    
    private bool ValidateMOOPLimits(BenefitPlan plan)
    {
        // 2026 MOOP limits (updated annually by HHS)
        var moopLimits = new Dictionary<int, (decimal individual, decimal family)>
        {
            { 2026, (9450m, 18900m) },
            { 2027, (9700m, 19400m) }  // Projected
        };
        
        if (!moopLimits.TryGetValue(plan.QHPCertification.PlanYear, out var limits))
            return false; // Unknown plan year
        
        // In-network MOOP cannot exceed federal limits
        return plan.CostSharing.IndividualOutOfPocketMax <= limits.individual &&
               plan.CostSharing.FamilyOutOfPocketMax <= limits.family;
    }
    
    private bool ValidateHSADeductible(BenefitPlan plan)
    {
        // Only applies to HSA-qualified HDHPs
        if (plan.PlanType != PlanType.HDHP || !plan.IsHSAQualified)
            return true; // Not applicable
        
        // 2026 HSA minimum deductibles
        var hsaLimits = new { Individual = 1650m, Family = 3300m };
        
        return plan.CostSharing.IndividualDeductible >= hsaLimits.Individual &&
               plan.CostSharing.FamilyDeductible >= hsaLimits.Family;
    }
}

public class EHBValidationResult
{
    public bool IsCompliant { get; set; }
    public int CoveredEHBCategories { get; set; }
    public int TotalEHBCategoriesRequired { get; set; }
    public List<EHBCategory> MissingCategories { get; set; } = new();
    public bool ActuarialValueCompliant { get; set; }
    public decimal ActuarialValue { get; set; }
    public decimal ExpectedActuarialValue { get; set; }
    public bool MOOPCompliant { get; set; }
    public bool DeductibleCompliant { get; set; }
    public DateTime ValidationDate { get; set; }
    
    public static EHBValidationResult PlanNotFound =>
        new() { IsCompliant = false, MissingCategories = new() };
}
```

**2.2 EHB Coverage Gap Identifier**

```csharp
/// <summary>
/// Identify specific benefits that should be added to meet EHB requirements
/// </summary>
public class EHBGapAnalyzer
{
    private readonly Dictionary<EHBCategory, List<string>> _requiredServiceCategories = new()
    {
        { EHBCategory.AmbulatoryPatientServices, new List<string> 
            { "Office Visit - Primary Care", "Office Visit - Specialist", "Outpatient Surgery" } },
        { EHBCategory.EmergencyServices, new List<string>
            { "Emergency Room", "Emergency Transportation" } },
        { EHBCategory.Hospitalization, new List<string>
            { "Inpatient Hospital", "Skilled Nursing Facility" } },
        { EHBCategory.MaternityAndNewbornCare, new List<string>
            { "Prenatal Care", "Delivery", "Postpartum Care" } },
        { EHBCategory.MentalHealthAndSubstanceUse, new List<string>
            { "Outpatient Mental Health", "Inpatient Mental Health", "Substance Abuse Treatment" } },
        { EHBCategory.PrescriptionDrugs, new List<string>
            { "Generic Drugs", "Preferred Brand Drugs", "Non-Preferred Brand Drugs", "Specialty Drugs" } },
        { EHBCategory.RehabilitativeAndHabilitative, new List<string>
            { "Physical Therapy", "Occupational Therapy", "Speech Therapy" } },
        { EHBCategory.LaboratoryServices, new List<string>
            { "Diagnostic Lab", "Imaging - X-Ray", "Imaging - MRI/CT" } },
        { EHBCategory.PreventiveAndWellness, new List<string>
            { "Annual Physical", "Immunizations", "Preventive Screenings" } },
        { EHBCategory.PediatricServices, new List<string>
            { "Pediatric Dental", "Pediatric Vision", "Well-Child Visits" } }
    };
    
    public async Task<List<BenefitRecommendation>> GetRecommendedBenefits(string planId)
    {
        var plan = await _repository.GetByPlanIdAsync(planId);
        var validationResult = await _validator.ValidatePlanCompliance(planId);
        
        var recommendations = new List<BenefitRecommendation>();
        
        foreach (var missingCategory in validationResult.MissingCategories)
        {
            var requiredServices = _requiredServiceCategories[missingCategory];
            
            foreach (var service in requiredServices)
            {
                recommendations.Add(new BenefitRecommendation
                {
                    EHBCategory = missingCategory,
                    ServiceCategory = service,
                    Reason = $"Required to meet {missingCategory} EHB coverage",
                    Priority = "Critical",
                    SampleBenefit = CreateSampleBenefit(missingCategory, service, plan.MetalLevel)
                });
            }
        }
        
        return recommendations;
    }
    
    private Benefit CreateSampleBenefit(EHBCategory category, string serviceCategory, MetalLevel? metalLevel)
    {
        // Generate sample benefit with typical cost sharing for metal tier
        var copayMultiplier = metalLevel switch
        {
            MetalLevel.Bronze => 1.5m,
            MetalLevel.Silver => 1.0m,
            MetalLevel.Gold => 0.7m,
            MetalLevel.Platinum => 0.5m,
            _ => 1.0m
        };
        
        return new Benefit
        {
            ServiceCategory = serviceCategory,
            EHBCategory = category,
            IsEssentialHealthBenefit = true,
            InNetworkCopay = GetTypicalCopay(serviceCategory) * copayMultiplier,
            DeductibleApplies = ShouldApplyDeductible(serviceCategory),
            PriorAuthRequired = RequiresPriorAuth(serviceCategory)
        };
    }
}
```

---

### Phase 3: Actuarial Value Calculator (Week 3)

**3.1 AV Calculator Service**

File: `services/benefit-plan-service/Services/ActuarialValueCalculator.cs`

```csharp
/// <summary>
/// Calculate actuarial value (AV) for plan certification
/// AV = Expected plan spending / Total expected spending
/// Uses ACA AV Calculator methodology
/// </summary>
public class ActuarialValueCalculator
{
    /// <summary>
    /// Calculate AV using standard population claims data
    /// </summary>
    public async Task<decimal> CalculateActuarialValue(string planId)
    {
        var plan = await _repository.GetByPlanIdAsync(planId);
        
        // 1. Load standard population (ACA standardized claims set)
        var standardClaims = await LoadStandardPopulationClaims();
        
        // 2. Apply plan benefits to each claim
        decimal totalAllowedAmount = 0;
        decimal totalPlanPays = 0;
        
        foreach (var claim in standardClaims)
        {
            var benefit = plan.Benefits.FirstOrDefault(b => 
                b.ServiceCategory == claim.ServiceCategory);
            
            if (benefit == null) continue;
            
            var costSharingResult = CalculateCostSharing(
                claim.AllowedAmount,
                benefit,
                plan.CostSharing,
                claim.AccumulatedDeductible,
                claim.AccumulatedOOP);
            
            totalAllowedAmount += claim.AllowedAmount;
            totalPlanPays += costSharingResult.PlanPays;
        }
        
        // 3. AV = Plan pays / Total allowed
        var actuarialValue = (totalPlanPays / totalAllowedAmount) * 100;
        
        return Math.Round(actuarialValue, 2);
    }
    
    private CostSharingResult CalculateCostSharing(
        decimal allowedAmount,
        Benefit benefit,
        CostSharing costSharing,
        decimal accumulatedDeductible,
        decimal accumulatedOOP)
    {
        decimal memberPays = 0;
        decimal deductibleRemaining = costSharing.IndividualDeductible - accumulatedDeductible;
        
        // 1. Copay (if not subject to deductible)
        if (!benefit.DeductibleApplies && benefit.InNetworkCopay.HasValue)
        {
            memberPays = benefit.InNetworkCopay.Value;
        }
        // 2. Deductible
        else if (deductibleRemaining > 0)
        {
            var deductibleApplies = Math.Min(allowedAmount, deductibleRemaining);
            memberPays += deductibleApplies;
            allowedAmount -= deductibleApplies;
        }
        
        // 3. Coinsurance (after deductible)
        if (allowedAmount > 0 && benefit.InNetworkCoinsurance.HasValue)
        {
            memberPays += allowedAmount * benefit.InNetworkCoinsurance.Value;
        }
        
        // 4. Out-of-pocket maximum
        var oopRemaining = costSharing.IndividualOutOfPocketMax - accumulatedOOP;
        memberPays = Math.Min(memberPays, oopRemaining);
        
        return new CostSharingResult
        {
            AllowedAmount = allowedAmount,
            MemberPays = memberPays,
            PlanPays = allowedAmount - memberPays
        };
    }
    
    /// <summary>
    /// Load ACA standard population claims for AV calculation
    /// Based on CMS Actuarial Value Calculator continuance tables
    /// </summary>
    private async Task<List<StandardClaim>> LoadStandardPopulationClaims()
    {
        // TODO: Integrate with CMS AV Calculator data
        // For now, return representative sample
        return new List<StandardClaim>
        {
            // Primary care visits
            new() { ServiceCategory = "Office Visit - Primary Care", AllowedAmount = 150, Count = 3.2m },
            new() { ServiceCategory = "Office Visit - Specialist", AllowedAmount = 200, Count = 1.5m },
            
            // Hospitalization
            new() { ServiceCategory = "Inpatient Hospital", AllowedAmount = 25000, Count = 0.08m },
            new() { ServiceCategory = "Emergency Room", AllowedAmount = 1500, Count = 0.4m },
            
            // Prescriptions
            new() { ServiceCategory = "Generic Drugs", AllowedAmount = 25, Count = 12m },
            new() { ServiceCategory = "Preferred Brand Drugs", AllowedAmount = 150, Count = 4m },
            
            // Imaging
            new() { ServiceCategory = "Imaging - X-Ray", AllowedAmount = 200, Count = 1.2m },
            new() { ServiceCategory = "Imaging - MRI/CT", AllowedAmount = 1200, Count = 0.3m },
            
            // Labs
            new() { ServiceCategory = "Diagnostic Lab", AllowedAmount = 75, Count = 4m },
            
            // ... additional standard claims
        };
    }
}

public class StandardClaim
{
    public string ServiceCategory { get; set; } = string.Empty;
    public decimal AllowedAmount { get; set; }
    public decimal Count { get; set; } // Utilization frequency
    public decimal AccumulatedDeductible { get; set; }
    public decimal AccumulatedOOP { get; set; }
}
```

---

### Phase 4: Plan Comparison Tool (Week 4)

**4.1 QHP Comparison API**

File: `services/benefit-plan-service/Controllers/QHPComparisonController.cs`

```csharp
[ApiController]
[Route("api/v1/qhp-comparison")]
public class QHPComparisonController : ControllerBase
{
    /// <summary>
    /// Compare QHPs for member plan shopping
    /// Returns apples-to-apples comparison across metal tiers
    /// </summary>
    [HttpPost("compare")]
    [ProducesResponseType(typeof(QHPComparisonResult), 200)]
    public async Task<IActionResult> ComparePlans([FromBody] QHPComparisonRequest request)
    {
        var plans = await _repository.GetQHPsByServiceArea(
            request.State,
            request.County,
            request.PlanYear);
        
        var comparisonResult = new QHPComparisonResult
        {
            ServiceArea = $"{request.State}-{request.County}",
            PlanYear = request.PlanYear,
            Plans = plans.Select(p => new QHPComparisonItem
            {
                QHPId = p.QHPCertification.QHPId,
                PlanName = p.PlanName,
                Issuer = p.Payer,
                MetalLevel = p.MetalLevel.ToString(),
                ActuarialValue = p.QHPCertification.ActuarialValue,
                MonthlyPremium = GetPremiumForAge(p, request.Age),
                Deductible = p.CostSharing.IndividualDeductible,
                OutOfPocketMax = p.CostSharing.IndividualOutOfPocketMax,
                PrimaryCopay = GetBenefitCopay(p, "Office Visit - Primary Care"),
                SpecialistCopay = GetBenefitCopay(p, "Office Visit - Specialist"),
                ERCopay = GetBenefitCopay(p, "Emergency Room"),
                GenericDrugCopay = GetBenefitCopay(p, "Generic Drugs"),
                NetworkType = p.PlanType.ToString(),
                HasHSA = p.IsHSAQualified,
                EHBCompliant = await _validator.IsEHBCompliant(p.PlanId)
            }).ToList()
        };
        
        return Ok(comparisonResult);
    }
}

public class QHPComparisonRequest
{
    public string State { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty;
    public int PlanYear { get; set; }
    public int Age { get; set; }
    public int HouseholdSize { get; set; }
    public decimal HouseholdIncome { get; set; }
}

public class QHPComparisonItem
{
    public string QHPId { get; set; } = string.Empty;
    public string PlanName { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string MetalLevel { get; set; } = string.Empty;
    public decimal ActuarialValue { get; set; }
    public decimal MonthlyPremium { get; set; }
    public decimal Deductible { get; set; }
    public decimal OutOfPocketMax { get; set; }
    public decimal? PrimaryCopay { get; set; }
    public decimal? SpecialistCopay { get; set; }
    public decimal? ERCopay { get; set; }
    public decimal? GenericDrugCopay { get; set; }
    public string NetworkType { get; set; } = string.Empty;
    public bool HasHSA { get; set; }
    public bool EHBCompliant { get; set; }
    public decimal? EstimatedAPTC { get; set; } // If APTC-eligible
}
```

---

## 📊 Database Schema Changes

### BenefitPlan Collection Updates

```json
{
  "id": "PLAN_2026_001",
  "planId": "PLAN_2026_001",
  "planName": "Blue Shield Silver HMO",
  "lineOfBusiness": "Exchange",
  "metalLevel": "Silver",
  "qhpCertification": {
    "qhpId": "11512NC0040001",
    "hiosId": "11512NC004",
    "planYear": 2026,
    "status": "Certified",
    "certificationDate": "2025-09-15T00:00:00Z",
    "expirationDate": "2026-12-31T23:59:59Z",
    "certifyingState": "GA",
    "marketplace": "FFM",
    "actuarialValue": 70.2,
    "isOnExchange": true,
    "isOffExchange": true,
    "serviceAreaCounties": ["13121", "13135", "13089"]
  },
  "benefits": [
    {
      "serviceCategory": "Office Visit - Primary Care",
      "ehbCategory": "AmbulatoryPatientServices",
      "isEssentialHealthBenefit": true,
      "inNetworkCopay": 30.00,
      "deductibleApplies": false
    }
  ]
}
```

---

## 🧪 Testing Plan

### Unit Tests
- [ ] EHB validation for all 10 categories
- [ ] Actuarial value calculation accuracy (±2%)
- [ ] MOOP limit enforcement (2026 limits)
- [ ] HSA deductible validation

### Integration Tests
- [ ] QHP comparison API returns correct plans
- [ ] Plan year rollover updates QHP IDs
- [ ] EHB gap analysis recommends missing benefits

### Test Scenarios
```csharp
[Fact]
public void ValidateEHB_AllCategoriesCovered_IsCompliant()
{
    var plan = CreateSilverPlanWithAllEHB();
    var result = _validator.ValidatePlanCompliance(plan.PlanId).Result;
    
    Assert.True(result.IsCompliant);
    Assert.Equal(10, result.CoveredEHBCategories);
    Assert.Empty(result.MissingCategories);
}

[Fact]
public void CalculateAV_SilverPlan_Returns70Percent()
{
    var plan = CreateTypicalSilverPlan();
    var av = _avCalculator.CalculateActuarialValue(plan.PlanId).Result;
    
    Assert.InRange(av, 68m, 72m); // Allow ±2% variance
}

[Fact]
public void ValidateMOOP_Exceeds2026Limit_IsNonCompliant()
{
    var plan = CreatePlanWithExcessiveMOOP(10000m); // 2026 limit is $9,450
    var result = _validator.ValidatePlanCompliance(plan.PlanId).Result;
    
    Assert.False(result.MOOPCompliant);
}
```

---

## 📚 Documentation Updates

### New Documents
- [ ] `docs/QHP-CERTIFICATION-GUIDE.md` - QHP certification process
- [ ] `docs/EHB-COMPLIANCE.md` - Essential Health Benefits requirements
- [ ] `docs/ACTUARIAL-VALUE-CALCULATION.md` - AV methodology
- [ ] `docs/PLAN-COMPARISON-API.md` - QHP comparison endpoint

### Update Existing Docs
- [ ] Update `FEATURES.md` - Add QHP certification features
- [ ] Update `README.md` - Add Exchange compliance capabilities

---

## 🔗 Dependencies

### External Data Sources
- **CMS QHP Landscape Files**: Plan IDs, service areas, premiums
- **ACA Actuarial Value Calculator**: Standard population claims data
- **HHS Annual MOOP Limits**: Updated each February

### Prerequisite Issues
- #375 (APTC/CSR Support) - CSR plans need QHP certification

### Blocks
- #377 (Exchange Enrollment Periods) - QHP status affects enrollment eligibility

---

## 💰 Financial Impact

### Revenue Opportunity
- **State marketplace contracts**: $200K-500K ARR each
- **QHP issuers**: 100+ potential customers ($50K-150K ARR each)
- **Plan comparison tools**: Licensing to brokers/navigators ($10K-25K each)

### Development Cost
- 3-4 weeks × 2 developers × $150/hr = $36K-48K
- CMS data integration: $5K-10K
- **Total**: $41K-58K

---

## ✅ Acceptance Criteria

- [ ] All Exchange plans track QHP certification status
- [ ] EHB validator identifies missing categories
- [ ] Actuarial value calculated to ±2% accuracy
- [ ] MOOP limits enforced (2026: $9,450 individual)
- [ ] Plan comparison API returns apples-to-apples data
- [ ] Annual plan year rollover updates QHP IDs

---

## 📖 References

- [CMS QHP Certification Standards](https://www.cms.gov/CCIIO/Resources/Regulations-and-Guidance/Downloads/Final-2022-Letter-to-Issuers.pdf)
- [ACA Actuarial Value Calculator](https://www.cms.gov/cciio/resources/regulations-and-guidance/index.html#Premium Stabilization Programs)
- [Essential Health Benefits](https://www.healthcare.gov/glossary/essential-health-benefits/)
- [Annual MOOP Limits](https://www.cms.gov/newsroom/fact-sheets/2026-benefit-and-payment-parameters)

---

**Labels:** `feature`, `exchange`, `compliance`, `priority:high`, `v5.0`  
**Milestone:** v5.0 - Exchange Market Expansion (Q2 2026)
