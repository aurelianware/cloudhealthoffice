---
name: "[v4.0] Advanced Analytics and Reporting"
about: Implement analytics dashboards for claims trends, fraud detection, and payer performance
title: "[v4.0] Implement Advanced Analytics and Reporting Dashboards"
labels: enhancement, analytics, v4.0, priority-medium
assignees: aurelianware
---

## Overview
Add advanced analytics for claims trends, fraud risk detection, and payer performance metrics. Integrate with existing Prometheus/Grafana monitoring stack and tie to Stripe for metered premium reports. Use tenant management to ensure data isolation per payer.

## Objectives
- ✅ Real-time claims analytics dashboards (adjudication time, denial rates, cost trends)
- ✅ Fraud detection with basic ML (anomaly detection for suspicious claims)
- ✅ Payer performance scorecards (network efficiency, provider satisfaction)
- ✅ Exportable reports (PDF/CSV) gated behind Stripe subscriptions
- ✅ Anonymized PHI in aggregates (HIPAA compliance)
- ✅ Prepare foundation for Q3 2026 advanced AI features

## Architecture

```
┌─────────────────────────────────────────────────┐
│          Analytics & Reporting Stack            │
├─────────────────────────────────────────────────┤
│                                                 │
│  ┌─────────────┐   ┌─────────────┐   ┌────────┐│
│  │   Grafana   │   │ PowerBI/    │   │ Jupyter││
│  │  Dashboards │   │  Embedded   │   │ Notebooks││
│  └──────┬──────┘   └──────┬──────┘   └───┬────┘│
│         │                  │               │     │
│         └──────────────────┴───────────────┘     │
│                          │                       │
│                   ┌──────▼──────┐                │
│                   │   Analytics  │               │
│                   │   Service    │               │
│                   └──────┬───────┘               │
│                          │                       │
│         ┌────────────────┴────────────────┐      │
│         │                                  │      │
│    ┌────▼────┐  ┌──────────┐  ┌──────────▼───┐  │
│    │ Cosmos  │  │   Kafka   │  │  PostgreSQL  │  │
│    │   DB    │  │  Stream   │  │  Analytics   │  │
│    └─────────┘  └──────────┘  └──────────────┘  │
│                                                  │
│         ML Pipeline (Python/PyTorch)             │
│         Stripe Metering (Premium Reports)        │
└──────────────────────────────────────────────────┘
```

## Implementation Steps

### Phase 1: Analytics Service (Weeks 1-2)

#### 1.1 Create Analytics Microservice
```bash
mkdir -p services/analytics-service
cd services/analytics-service
dotnet new webapi -n AnalyticsService
```

**Project Structure:**
```
services/analytics-service/
├── Controllers/
│   ├── AnalyticsController.cs
│   ├── ReportsController.cs
│   └── MetricsController.cs
├── Services/
│   ├── IAnalyticsService.cs
│   ├── AnalyticsService.cs
│   ├── IReportGenerator.cs
│   └── ReportGenerator.cs (PDF/CSV export)
├── Models/
│   ├── ClaimAnalytics.cs
│   ├── TrendData.cs
│   └── PerformanceMetrics.cs
├── Data/
│   ├── AnalyticsRepository.cs (PostgreSQL for aggregates)
│   └── EventConsumer.cs (Kafka consumer)
└── ML/
    ├── FraudDetectionModel.cs
    └── AnomalyDetector.cs
```

#### 1.2 API Endpoints

```csharp
// Controllers/AnalyticsController.cs
[ApiController]
[Route("api/v1/analytics")]
public class AnalyticsController : ControllerBase
{
    [HttpGet("claims/trends")]
    public async Task<ClaimTrends> GetClaimTrends(
        [FromQuery] string tenantId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        return new ClaimTrends
        {
            TotalClaims = 15234,
            AdjudicatedClaims = 14891,
            DeniedClaims = 343,
            AvgAdjudicationTime = TimeSpan.FromMinutes(2.3),
            DailyVolume = new[] { /* time series data */ }
        };
    }
    
    [HttpGet("claims/denial-reasons")]
    public async Task<DenialReasonBreakdown> GetDenialReasons(
        [FromQuery] string tenantId,
        [FromQuery] DateTime startDate)
    {
        // Top denial reasons by count
        return new DenialReasonBreakdown
        {
            Reasons = new[]
            {
                new DenialReason { Code = "CO-50", Description = "Non-covered service", Count = 128 },
                new DenialReason { Code = "PR-1", Description = "Deductible", Count = 89 },
                new DenialReason { Code = "CO-97", Description = "Payment adjusted", Count = 56 }
            }
        };
    }
    
    [HttpGet("performance/payer-scorecard")]
    public async Task<PayerScorecard> GetPayerScorecard([FromQuery] string tenantId)
    {
        return new PayerScorecard
        {
            PayerName = "Blue Shield California",
            Metrics = new
            {
                ClaimsVolume90d = 12450,
                AvgTimeToPayment = TimeSpan.FromDays(14.2),
                AutoApprovalRate = 0.92,
                DenialRate = 0.023,
                ProviderSatisfactionScore = 4.3, // out of 5
                NetworkSize = 18234,
                MemberCount = 245000
            },
            Trends = new
            {
                ClaimsVolumeChange = 0.15, // +15% vs prior 90d
                PaymentSpeedChange = -0.08  // -8% (improvement)
            }
        };
    }
}
```

#### 1.3 Real-Time Data Pipeline

**Kafka Consumer for Aggregation:**
```csharp
// Data/EventConsumer.cs
public class ClaimEventConsumer : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new ConsumerBuilder<string, ClaimEvent>(_config).Build();
        consumer.Subscribe("claims-adjudication");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = consumer.Consume(stoppingToken);
            var claimEvent = result.Message.Value;
            
            // Aggregate in PostgreSQL
            await _analyticsRepo.UpdateAggregatesAsync(new
            {
                TenantId = claimEvent.TenantId,
                Date = claimEvent.AdjudicatedAt.Date,
                TotalClaims = 1,
                TotalAmount = claimEvent.BilledAmount,
                AvgAdjudicationTime = claimEvent.ProcessingTime
            });
        }
    }
}
```

**PostgreSQL Schema for Aggregates:**
```sql
CREATE TABLE claim_daily_aggregates (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(255) NOT NULL,
    date DATE NOT NULL,
    total_claims INT DEFAULT 0,
    adjudicated_claims INT DEFAULT 0,
    denied_claims INT DEFAULT 0,
    total_billed_amount DECIMAL(12,2) DEFAULT 0,
    total_paid_amount DECIMAL(12,2) DEFAULT 0,
    avg_adjudication_time_ms INT DEFAULT 0,
    created_at TIMESTAMP DEFAULT NOW(),
    updated_at TIMESTAMP DEFAULT NOW(),
    UNIQUE(tenant_id, date)
);

CREATE INDEX idx_tenant_date ON claim_daily_aggregates(tenant_id, date DESC);
```

### Phase 2: Grafana Dashboards (Week 2-3)

#### 2.1 Deploy Grafana
```yaml
# k8s/monitoring/grafana-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: grafana
  namespace: cho-svcs
spec:
  replicas: 1
  template:
    spec:
      containers:
      - name: grafana
        image: grafana/grafana:10.2.0
        env:
        - name: GF_SECURITY_ADMIN_PASSWORD
          valueFrom:
            secretKeyRef:
              name: grafana-secret
              key: admin-password
        - name: GF_DATABASE_TYPE
          value: postgres
        - name: GF_DATABASE_HOST
          value: postgres.cho-svcs:5432
        volumeMounts:
        - name: grafana-storage
          mountPath: /var/lib/grafana
```

#### 2.2 Create Dashboards

**Claims Overview Dashboard:**
- **Panel 1:** Total claims processed (gauge)
- **Panel 2:** Adjudication time trend (time series)
- **Panel 3:** Denial rate by reason (pie chart)
- **Panel 4:** Claims volume heatmap (by hour/day)
- **Panel 5:** Top providers by volume (bar chart)

**Payer Performance Dashboard:**
- **Panel 1:** Payer scorecard (stat panels)
- **Panel 2:** Network growth (time series)
- **Panel 3:** Provider satisfaction trend (line chart)
- **Panel 4:** Cost per claim (area chart)

**Fraud Detection Dashboard:**
- **Panel 1:** Anomaly score distribution (histogram)
- **Panel 2:** Flagged claims (table with drill-down)
- **Panel 3:** Fraud patterns (network graph - future)

#### 2.3 Embed in Portals
```razor
<!-- portal/CloudHealthOffice.Portal/Pages/Analytics.razor -->
@page "/analytics"
@attribute [Authorize(Policy = "PremiumFeatures")]

<iframe src="https://grafana.cloudhealthoffice.com/d/claims-overview?orgId=1&var-tenant=@TenantId&theme=light&kiosk"
        width="100%" height="800px" frameborder="0">
</iframe>

<MudButton OnClick="ExportToPdf">Export Dashboard</MudButton>
```

### Phase 3: Machine Learning (Weeks 3-4)

#### 3.1 Fraud Detection Model

**Python Service for ML:**
```python
# functions/ClaimRiskScorer/main.py
import torch
import numpy as np
from sklearn.ensemble import IsolationForest

class FraudDetector:
    def __init__(self):
        self.model = IsolationForest(contamination=0.01)  # 1% fraud rate
        
    def train(self, claims_data):
        """Train on historical claims with known fraud labels"""
        features = self.extract_features(claims_data)
        self.model.fit(features)
        
    def extract_features(self, claims):
        """Extract features for anomaly detection"""
        return np.array([
            claims['billed_amount'],
            claims['service_count'],
            claims['diagnosis_complexity'],
            claims['provider_risk_score'],
            claims['time_since_last_claim_hours'],
            claims['distance_from_member_miles']
        ])
        
    def predict_risk_score(self, claim):
        """Return risk score 0-1 (1 = high fraud risk)"""
        features = self.extract_features([claim])
        anomaly_score = self.model.decision_function(features)[0]
        # Normalize to 0-1 range
        return 1 / (1 + np.exp(anomaly_score))

# Flask API endpoint
from flask import Flask, request, jsonify
app = Flask(__name__)
detector = FraudDetector()

@app.route('/score', methods=['POST'])
def score_claim():
    claim = request.json
    risk_score = detector.predict_risk_score(claim)
    
    return jsonify({
        'claim_id': claim['id'],
        'risk_score': float(risk_score),
        'flagged': risk_score > 0.75,
        'reasons': get_risk_factors(claim) if risk_score > 0.75 else []
    })
```

#### 3.2 Integrate with Claims Service

```csharp
// services/claims-service/Services/FraudCheckService.cs
public class FraudCheckService
{
    private readonly HttpClient _mlClient;
    
    public async Task<FraudRiskScore> CheckClaimAsync(Claim claim)
    {
        var response = await _mlClient.PostAsJsonAsync(
            "http://claim-risk-scorer.cho-svcs/score",
            new
            {
                id = claim.ClaimId,
                billed_amount = claim.BilledAmount,
                service_count = claim.ServiceLines.Count,
                diagnosis_complexity = CalculateComplexity(claim.DiagnosisCodes),
                provider_risk_score = await GetProviderRiskScore(claim.ProviderId),
                time_since_last_claim_hours = await GetTimeSinceLastClaim(claim.MemberId),
                distance_from_member_miles = await CalculateDistance(claim)
            });
        
        return await response.Content.ReadFromJsonAsync<FraudRiskScore>();
    }
}
```

#### 3.3 Anomaly Alerting

```csharp
// Trigger alert if fraud risk > 75%
if (fraudScore.RiskScore > 0.75)
{
    await _alertService.SendAlertAsync(new Alert
    {
        Type = "FraudSuspicion",
        Severity = "High",
        TenantId = claim.TenantId,
        ClaimId = claim.ClaimId,
        Message = $"Claim flagged for fraud review. Risk score: {fraudScore.RiskScore:P0}",
        Reasons = fraudScore.Reasons
    });
    
    // Hold claim for manual review
    claim.Status = "PendingFraudReview";
    await _claimsRepo.UpdateAsync(claim);
}
```

### Phase 4: Report Generation (Week 4-5)

#### 4.1 PDF Export with QuestPDF

```csharp
// Services/ReportGenerator.cs
using QuestPDF.Fluent;
using QuestPDF.Helpers;

public class ReportGenerator : IReportGenerator
{
    public byte[] GenerateClaimsSummaryPdf(ClaimAnalytics analytics, string tenantName)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Header().Text($"{tenantName} - Claims Summary Report")
                    .FontSize(20).Bold();
                
                page.Content().Column(column =>
                {
                    column.Item().Text($"Report Period: {analytics.StartDate:d} - {analytics.EndDate:d}");
                    
                    column.Item().PaddingTop(10).Table(table =>
                    {
                        table.ColumnsDefinition(columns =>
                        {
                            columns.RelativeColumn();
                            columns.RelativeColumn();
                        });
                        
                        table.Cell().Text("Total Claims").Bold();
                        table.Cell().Text(analytics.TotalClaims.ToString("N0"));
                        
                        table.Cell().Text("Adjudicated Claims").Bold();
                        table.Cell().Text(analytics.AdjudicatedClaims.ToString("N0"));
                        
                        table.Cell().Text("Denial Rate").Bold();
                        table.Cell().Text($"{analytics.DenialRate:P2}");
                        
                        table.Cell().Text("Avg Adjudication Time").Bold();
                        table.Cell().Text($"{analytics.AvgAdjudicationTime.TotalMinutes:N1} min");
                    });
                    
                    column.Item().PaddingTop(20).Image(
                        GenerateTrendChart(analytics.DailyVolume)
                    );
                });
                
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Generated by Cloud Health Office - ");
                    text.Hyperlink("cloudhealthoffice.com", "https://cloudhealthoffice.com");
                });
            });
        }).GeneratePdf();
    }
}
```

#### 4.2 CSV Export

```csharp
public string GenerateClaimsCsv(List<Claim> claims)
{
    var csv = new StringBuilder();
    csv.AppendLine("Claim ID,Member ID,Provider NPI,Service Date,Billed Amount,Paid Amount,Status");
    
    foreach (var claim in claims)
    {
        csv.AppendLine($"{claim.ClaimId},{claim.MemberId},{claim.ProviderNpi}," +
                      $"{claim.ServiceDate:yyyy-MM-dd},{claim.BilledAmount:C}," +
                      $"{claim.PaidAmount:C},{claim.Status}");
    }
    
    return csv.ToString();
}
```

#### 4.3 Stripe Metering for Reports

```csharp
// Controllers/ReportsController.cs
[HttpPost("export/pdf")]
[Authorize(Policy = "PremiumFeatures")]
public async Task<IActionResult> ExportPdf([FromQuery] string tenantId)
{
    // Generate report
    var analytics = await _analyticsService.GetClaimTrendsAsync(tenantId, startDate, endDate);
    var pdf = _reportGenerator.GenerateClaimsSummaryPdf(analytics, tenantId);
    
    // Meter in Stripe (if usage-based tier)
    await _stripeService.ReportUsageAsync(tenantId, "report.generated", 1);
    
    return File(pdf, "application/pdf", $"claims-summary-{DateTime.UtcNow:yyyyMMdd}.pdf");
}
```

### Phase 5: Compliance & Anonymization (Week 5)

#### 5.1 PHI Masking

```csharp
public class PhiMasker
{
    public ClaimAnalytics AnonymizeForReporting(List<Claim> claims)
    {
        return new ClaimAnalytics
        {
            TotalClaims = claims.Count,
            AverageBilledAmount = claims.Average(c => c.BilledAmount),
            // DO NOT include: MemberId, SSN, Name, DOB, Address
            // DO include: Aggregates, trends, anonymized demographics
            AgeBrackets = claims
                .GroupBy(c => GetAgeBracket(c.MemberAge))
                .ToDictionary(g => g.Key, g => g.Count()),
            GenderDistribution = claims
                .GroupBy(c => c.MemberGender)
                .ToDictionary(g => g.Key, g => g.Count())
        };
    }
    
    private string GetAgeBracket(int age)
    {
        return age switch
        {
            < 18 => "0-17",
            < 35 => "18-34",
            < 50 => "35-49",
            < 65 => "50-64",
            _ => "65+"
        };
    }
}
```

#### 5.2 Audit Logging

```csharp
logger.LogInformation("User {UserId} generated analytics report for tenant {TenantId}. " +
                     "Date range: {StartDate} - {EndDate}. Row count: {RowCount}",
                     userId, tenantId, startDate, endDate, claims.Count);
// Never log actual claim data or PHI
```

## Tech Stack
- **Backend:** .NET 8 Analytics Service
- **Data Store:** PostgreSQL (aggregates), Cosmos DB (raw data)
- **Streaming:** Kafka (real-time events)
- **Visualization:** Grafana 10.2, ApexCharts.NET (embedded)
- **ML:** Python 3.11, PyTorch, scikit-learn, Isolation Forest
- **Reporting:** QuestPDF (PDF), CsvHelper (CSV)
- **Billing:** Stripe metering for premium reports

## Testing

### Load Tests
```bash
# Simulate 1000 concurrent analytics queries
k6 run --vus 1000 --duration 30s analytics-load-test.js
```

### Accuracy Tests
```python
# Validate ML model with labeled fraud dataset
from sklearn.metrics import precision_recall_fscore_support

y_true = [0, 0, 1, 1, 0, 1]  # Actual fraud labels
y_pred = model.predict(test_claims)

precision, recall, f1, _ = precision_recall_fscore_support(y_true, y_pred)
assert precision > 0.8  # 80% precision
assert recall > 0.7     # 70% recall
```

## Dependencies
- ✅ Tenant Management (data isolation)
- ✅ Stripe Billing (metering)
- ⏳ PostgreSQL deployment
- ⏳ Grafana setup
- ⏳ Python ML service

## Documentation
- [ ] Create [docs/ANALYTICS.md](../../docs/ANALYTICS.md)
- [ ] Update [ARCHITECTURE.md](../../ARCHITECTURE.md) with analytics flow
- [ ] Add Swagger docs for Analytics Service APIs

## Success Criteria
- ✅ Dashboards load <3s with 1-year data
- ✅ Fraud detection: >80% precision, >70% recall
- ✅ Reports generate in <10s
- ✅ Zero PHI leaks in aggregated reports
- ✅ Stripe metering tracks 100% of premium exports

## Timeline
- **Weeks 1-2:** Analytics Service + APIs
- **Weeks 2-3:** Grafana dashboards
- **Weeks 3-4:** ML fraud detection
- **Weeks 4-5:** Report generation + Stripe metering
- **Week 5:** Compliance audit + testing

**Total:** 5 weeks (2 FTE)

## References
- [Grafana Dashboards](https://grafana.com/docs/grafana/latest/dashboards/)
- [scikit-learn Anomaly Detection](https://scikit-learn.org/stable/modules/outlier_detection.html)
- [QuestPDF Documentation](https://www.questpdf.com/)
