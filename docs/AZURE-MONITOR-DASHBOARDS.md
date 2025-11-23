# Azure Monitor Dashboards Guide

## Overview

Cloud Health Office includes three production-grade Azure Monitor Workbooks for real-time monitoring of HIPAA X12 EDI transaction processing across all payers. These dashboards provide comprehensive visibility into transaction latency, error rates, volume, payer integration health, and HIPAA compliance validation.

**All dashboards include automatic PHI redaction to ensure HIPAA compliance.**

---

## Table of Contents

- [Dashboard Overview](#dashboard-overview)
- [Deployment](#deployment)
- [Dashboard Details](#dashboard-details)
  - [EDI Transaction Metrics](#1-edi-transaction-metrics-dashboard)
  - [Payer Integration Health](#2-payer-integration-health-dashboard)
  - [HIPAA Compliance Monitoring](#3-hipaa-compliance-monitoring-dashboard)
- [Alerting Rules](#alerting-rules)
- [PHI Redaction](#phi-redaction)
- [Multi-Tenant Configuration](#multi-tenant-configuration)
- [Troubleshooting](#troubleshooting)
- [Best Practices](#best-practices)

---

## Dashboard Overview

### Available Dashboards

1. **EDI Transaction Metrics** - Real-time monitoring of all transaction types (275, 277, 278)
2. **Payer Integration Health** - Per-payer health monitoring with scoring
3. **HIPAA Compliance Monitoring** - PHI redaction validation and security audit logging

### Key Features

- ✅ **Real-Time Metrics**: 5-minute granularity with automatic refresh
- ✅ **Multi-Tenant Support**: Filter by payer ID across all dashboards
- ✅ **PHI Redaction**: Automatic exclusion of patient identifiers (SSN, DOB, Member IDs)
- ✅ **Alerting Ready**: Pre-configured KQL queries for Azure Monitor alerts
- ✅ **HIPAA Compliant**: All queries comply with HIPAA privacy requirements
- ✅ **Parameterized**: Flexible time ranges and filtering options

---

## Deployment

### Automatic Deployment

The workbooks are automatically deployed with the main infrastructure:

```bash
# Deploy infrastructure including workbooks
az deployment group create \
  --resource-group <resource-group-name> \
  --template-file infra/main.bicep \
  --parameters baseName=<base-name> \
               serviceBusName=<service-bus-name> \
               iaName=<integration-account-name> \
               sftpHost=<sftp-host> \
               sftpUsername=<sftp-username> \
               sftpPassword=<sftp-password>
```

### Verify Deployment

After deployment, verify workbooks are created:

```bash
# List workbooks in resource group
az monitor app-insights workbook list \
  --resource-group <resource-group-name> \
  --output table
```

### Access Dashboards

Workbooks are available in the Azure Portal:

1. Navigate to Azure Portal → **Monitor** → **Workbooks**
2. Select **Public Workbooks** tab
3. Look for workbooks with your `baseName` prefix:
   - `{baseName}-edi-metrics`
   - `{baseName}-payer-health`
   - `{baseName}-hipaa-compliance`

**Direct URLs** are provided in deployment outputs:

```bash
# Get workbook URLs from deployment outputs
az deployment group show \
  --resource-group <resource-group-name> \
  --name <deployment-name> \
  --query properties.outputs
```

---

## Dashboard Details

### 1. EDI Transaction Metrics Dashboard

**Purpose**: Monitor all EDI transaction types across all payers in real-time.

#### Key Metrics

| Metric | Description | Target |
|--------|-------------|--------|
| **Total Transactions** | Count of all transactions | Trending upward |
| **Success Rate** | % of successful transactions | ≥ 95% |
| **Avg Duration** | Average processing time | < 2000ms |
| **P95 Latency** | 95th percentile latency | < 5000ms |
| **P99 Latency** | 99th percentile latency | < 10000ms |
| **Error Count** | Failed transactions | Minimized |

#### Visualizations

1. **Transaction Overview Tiles**: High-level KPIs with color-coded health indicators
2. **Transaction Volume Over Time**: 5-minute bins showing volume trends
3. **Transaction Latency Chart**: P50/P95/P99 latency trends over time
4. **Error Rate Chart**: Failure distribution by transaction type
5. **Transactions by Payer Table**: Per-payer breakdown with success rates
6. **Recent Errors Table**: Last 50 errors with truncated messages (PHI redacted)
7. **Dependency Health Table**: Backend system health (SFTP, Service Bus, Storage, QNXT)

#### Parameters

- **Time Range**: Selectable (5min to 30 days)
- **Payer ID**: Filter by specific payer or "All"
- **Transaction Type**: Filter by 275, 277, 278, or "All"

#### Sample KQL Query

```kql
requests
| where timestamp > ago(1h)
| where name contains "ingest" or name contains "rfai"
| where customDimensions.payerId != ''
| summarize 
    TotalTransactions = count(),
    SuccessfulTransactions = countif(success == true),
    AvgDurationMs = avg(duration),
    P95DurationMs = percentile(duration, 95)
| extend SuccessRate = round(100.0 * SuccessfulTransactions / TotalTransactions, 2)
```

---

### 2. Payer Integration Health Dashboard

**Purpose**: Monitor individual payer health with comprehensive scoring system.

#### Health Score Calculation

Overall Health is calculated from four components (0-100 scale):

1. **Success Rate** (0-100%): Direct mapping from transaction success rate
2. **Latency Score**:
   - P95 < 2s: 100 points
   - P95 < 5s: 75 points
   - P95 < 10s: 50 points
   - P95 ≥ 10s: 25 points
3. **Volume Score**:
   - >100 transactions: 100 points
   - >50 transactions: 75 points
   - >10 transactions: 50 points
   - ≤10 transactions: 25 points
4. **Freshness Score**:
   - Last transaction < 1h ago: 100 points
   - Last transaction < 4h ago: 75 points
   - Last transaction < 24h ago: 50 points
   - Last transaction ≥ 24h ago: 25 points

**Overall Health** = Average of all four scores

#### Key Visualizations

1. **Payer Health Summary**: Single-payer health tile with color-coded score
2. **Transaction Types Table**: Breakdown by transaction type (275, 277, 278)
3. **Hourly Transaction Volume**: Stacked bar chart showing success/failure
4. **Latency Trends**: Line chart with P50/P95/P99 by transaction type
5. **Backend Integration Status**: Traffic light status for each dependency
6. **Error Distribution**: Pie chart of error types (4xx, 5xx)
7. **Recent Errors**: Last 100 errors for selected payer
8. **Integration Events Timeline**: X12 decode/encode, SFTP, Service Bus events

#### Health Status Indicators

| Status | Health Score | Action |
|--------|--------------|--------|
| ✅ Healthy | ≥ 90% | Monitor normally |
| ⚠️ Warning | 70-89% | Investigate soon |
| ❌ Critical | < 70% | **Immediate action required** |

#### Parameters

- **Time Range**: Selectable (1 hour to 30 days)
- **Payer ID**: Required - select specific payer

---

### 3. HIPAA Compliance Monitoring Dashboard

**Purpose**: Validate PHI redaction and monitor security controls.

#### Compliance Checks

1. **PHI Redaction Validation**
   - Scans all logs for potential PHI patterns
   - Detects SSN patterns (`\d{3}-\d{2}-\d{4}`)
   - Detects Member ID patterns (`[A-Z]{2}\d{9}`)
   - Detects PHI references (DOB, SSN, Patient Name keywords)

2. **Encryption in Transit**
   - Validates all HTTP calls use HTTPS
   - Flags any unencrypted connections

3. **Audit Logging**
   - Tracks PHI access events
   - Tracks data export events
   - Tracks configuration changes

4. **Authentication Monitoring**
   - Measures authentication rate
   - Identifies anonymous requests

5. **Data Retention**
   - Monitors archive operations to `hipaa-attachments` container
   - Validates data retention compliance

#### Key Visualizations

1. **HIPAA Compliance Overview**: Compliance score and status tile
2. **Potential PHI Pattern Detections**: Pie chart of detected patterns
3. **Data Transmission Encryption Status**: Encrypted vs. unencrypted breakdown
4. **Potential PHI Exposure Incidents**: Last 100 suspicious log entries
5. **Security Audit Events Timeline**: PHI access, exports, config changes
6. **Audit Trail Table**: Last 100 security events with user/IP tracking
7. **Data Archive Operations**: HIPAA retention monitoring
8. **Authentication Metrics**: Authentication rate and anonymous requests

#### Compliance Score

```
Compliance Score = 100% - (Suspicious Logs / Total Logs × 100%)
```

| Score | Status | Action |
|-------|--------|--------|
| 100% | ✅ Compliant | Continue monitoring |
| 99-100% | ⚠️ Review Required | Investigate suspicious patterns |
| < 99% | ❌ Critical | **Immediate remediation required** |

#### PHI Patterns Detected

The dashboard automatically scans for:

- **SSN Pattern**: `123-45-6789`
- **Member ID Pattern**: `AB123456789`
- **PHI Keywords**: DOB, SSN, Member ID, Patient Name, Date of Birth

**⚠️ Any detection should trigger immediate investigation.**

---

## Alerting Rules

### Recommended Alert Configuration

Configure Azure Monitor alerts based on these KQL queries:

#### 1. Low Success Rate Alert

**Condition**: Success rate < 95% for 15 minutes

```kql
requests
| where timestamp > ago(15m)
| where name contains "ingest" or name contains "rfai"
| summarize 
    Total = count(),
    Success = countif(success == true)
| extend SuccessRate = 100.0 * Success / Total
| where SuccessRate < 95
```

**Action**: Page on-call engineer

---

#### 2. High Latency Alert

**Condition**: P95 latency > 5000ms for 15 minutes

```kql
requests
| where timestamp > ago(15m)
| where name contains "ingest" or name contains "rfai"
| summarize P95Latency = percentile(duration, 95)
| where P95Latency > 5000
```

**Action**: Email operations team

---

#### 3. PHI Exposure Alert

**Condition**: Any potential PHI pattern detected in logs

```kql
traces
| where timestamp > ago(5m)
| where message matches regex @"\b\d{3}-\d{2}-\d{4}\b" or
        message matches regex @"\b[A-Z]{2}\d{9}\b" or
        message matches regex @"DOB|SSN|Member\s*ID|Patient\s*Name"
| count
| where Count > 0
```

**Action**: **CRITICAL** - Page security team immediately

---

#### 4. Unencrypted Traffic Alert

**Condition**: Any HTTP (non-HTTPS) calls detected

```kql
dependencies
| where timestamp > ago(5m)
| where type == "HTTP"
| where data startswith "http://"
| count
| where Count > 0
```

**Action**: **CRITICAL** - Block and remediate immediately

---

#### 5. Payer Integration Failure Alert

**Condition**: Zero transactions for a payer in 24 hours

```kql
requests
| where timestamp > ago(24h)
| where name contains "ingest" or name contains "rfai"
| summarize Count = count() by PayerId = tostring(customDimensions.payerId)
| where Count == 0
```

**Action**: Email integration team

---

#### 6. Dependency Failure Alert

**Condition**: Backend dependency success rate < 95%

```kql
dependencies
| where timestamp > ago(15m)
| where type in ("Azure Service Bus", "HTTP", "Azure blob")
| summarize 
    Total = count(),
    Success = countif(success == true)
    by DependencyName = name
| extend SuccessRate = 100.0 * Success / Total
| where SuccessRate < 95
```

**Action**: Email operations team

---

### Creating Alerts in Azure Portal

1. Navigate to **Monitor** → **Alerts** → **Create alert rule**
2. Select **Application Insights** as the scope
3. Add condition → **Custom log search**
4. Paste KQL query from above
5. Set threshold (e.g., `Count > 0`)
6. Configure action group (email, SMS, webhook)
7. Name and create rule

---

## PHI Redaction

### Automatic PHI Exclusion

All dashboards automatically exclude PHI from queries:

1. **No Direct PHI Fields**: Queries never access patient names, SSNs, DOBs, member IDs
2. **Message Truncation**: Error messages limited to 100-150 characters
3. **Aggregate-Only**: All metrics are aggregated, never showing individual records
4. **Non-PHI Identifiers**: Only use payer IDs, transaction types, error codes

### PHI Redaction Validation

The HIPAA Compliance dashboard validates redaction by:

1. Scanning logs for PHI patterns
2. Flagging any suspicious entries
3. Calculating compliance score
4. Providing audit trail for investigation

### Configuration in Application Insights

Ensure Application Insights has PHI redaction configured:

```bicep
// In main.bicep - Application Insights resource
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${baseName}-ai'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    // PHI redaction via Data Collection Rules (DCR)
    // Configure separately in Azure Portal
  }
}
```

**Manual DCR Configuration**:

1. Navigate to Application Insights → **Data Collection**
2. Create new DCR with transformation rules
3. Add regex replacements for PHI patterns:
   - SSN: `\b\d{3}-\d{2}-\d{4}\b` → `***-**-****`
   - Member ID: `\b[A-Z]{2}\d{9}\b` → `XX*********`

---

## Multi-Tenant Configuration

### Payer ID Filtering

All dashboards support multi-tenant filtering via payer ID:

```kql
// Filter by specific payer
| where customDimensions.payerId == '030240928'

// Filter by multiple payers
| where customDimensions.payerId in ('030240928', '123456789')

// All payers
| where customDimensions.payerId != ''
```

### Parameterized Deployment

Deploy workbooks with payer ID list:

```bicep
// In workbooks.bicep module call
module workbooks 'modules/workbooks.bicep' = {
  params: {
    baseName: baseName
    appInsightsId: insights.id
    payerIds: '030240928,123456789,987654321'  // Comma-separated
  }
}
```

### Custom Dimensions Required

Ensure Logic App workflows tag all requests with:

```json
{
  "customDimensions": {
    "payerId": "030240928",
    "transactionType": "275",
    "workflowName": "ingest275"
  }
}
```

---

## Troubleshooting

### Dashboard Shows No Data

**Symptom**: Workbook loads but all charts are empty

**Causes**:
1. No transactions have been processed yet
2. Application Insights not receiving telemetry
3. Time range selected has no data

**Solutions**:
1. Process test transaction (see `test-workflows.ps1`)
2. Verify Application Insights connection in Logic App settings
3. Extend time range to 24 hours or 7 days
4. Check Application Insights live metrics stream

```bash
# Verify Application Insights has data
az monitor app-insights query \
  --app <app-insights-name> \
  --analytics-query "requests | count" \
  --resource-group <resource-group-name>
```

---

### Custom Dimensions Not Populating

**Symptom**: Payer ID or Transaction Type dropdowns are empty

**Cause**: Logic App workflows not tagging telemetry with custom dimensions

**Solution**: Ensure workflows include custom properties in actions:

```json
// In workflow.json actions
{
  "type": "Http",
  "inputs": {
    // ... request details
  },
  "trackedProperties": {
    "payerId": "@parameters('payerId')",
    "transactionType": "275"
  }
}
```

---

### PHI Patterns Detected

**Symptom**: HIPAA Compliance dashboard shows suspicious patterns

**Immediate Actions**:
1. **STOP** - Pause affected workflows
2. Review flagged log entries in detail
3. Identify source of PHI exposure
4. Implement redaction in workflow
5. Report to security/compliance team
6. Document incident for HIPAA audit

**Prevention**:
- Never log raw EDI message content
- Always mask PHI before logging
- Use `substring()` to limit error message length
- Test with PHI scanner before deployment

---

### Workbook Query Errors

**Symptom**: "Query failed" or "Syntax error" in dashboard

**Causes**:
1. Invalid KQL syntax in workbook JSON
2. Application Insights schema changed
3. Required custom dimensions missing

**Solutions**:
1. Test query in Application Insights → Logs
2. Validate workbook JSON syntax
3. Redeploy workbook with corrected query
4. Check Application Insights schema

```bash
# Test KQL query directly
az monitor app-insights query \
  --app <app-insights-name> \
  --analytics-query "<your-query>" \
  --resource-group <resource-group-name>
```

---

## Best Practices

### Dashboard Usage

1. **Daily Review**: Check EDI Transaction Metrics dashboard daily
2. **Weekly Review**: Review Payer Integration Health for all payers weekly
3. **Continuous Monitoring**: Keep HIPAA Compliance dashboard open during deployments
4. **Alert Configuration**: Set up all recommended alerts (see [Alerting Rules](#alerting-rules))
5. **Incident Response**: Document all PHI exposure incidents per HIPAA requirements

### Performance Optimization

1. **Time Range**: Use shorter time ranges for faster query execution
2. **Payer Filtering**: Filter by specific payer for detailed analysis
3. **Refresh Rate**: Dashboards auto-refresh every 5 minutes
4. **Query Optimization**: Custom queries should use indexed fields (`timestamp`, `success`)

### Security Best Practices

1. **Access Control**: Restrict workbook access to authorized personnel only
2. **PHI Redaction**: Validate redaction monthly using compliance dashboard
3. **Audit Logging**: Review audit trail weekly
4. **Encryption**: Verify all traffic uses HTTPS (no HTTP)
5. **Data Retention**: Ensure HIPAA retention requirements met (6+ years)

### Multi-Tenant Management

1. **Payer Onboarding**: Add payer ID to `payerIds` parameter during deployment
2. **Health Monitoring**: Review health scores weekly for all payers
3. **SLA Tracking**: Track per-payer SLAs using integration health dashboard
4. **Capacity Planning**: Monitor transaction volume trends across all payers

### Alerting Strategy

| Priority | Response Time | Notification |
|----------|---------------|--------------|
| **Critical** (PHI exposure, unencrypted traffic) | < 5 minutes | Page + SMS + Email |
| **High** (Success rate < 95%, high latency) | < 30 minutes | Email + Slack |
| **Medium** (Dependency failure) | < 2 hours | Email |
| **Low** (No transactions 24h) | < 24 hours | Email |

---

## Reference Links

- [Azure Monitor Workbooks Documentation](https://docs.microsoft.com/en-us/azure/azure-monitor/visualize/workbooks-overview)
- [KQL Query Language Reference](https://docs.microsoft.com/en-us/azure/data-explorer/kusto/query/)
- [Application Insights Custom Dimensions](https://docs.microsoft.com/en-us/azure/azure-monitor/app/api-custom-events-metrics)
- [Azure Monitor Alerts](https://docs.microsoft.com/en-us/azure/azure-monitor/alerts/alerts-overview)
- [HIPAA Compliance in Azure](https://docs.microsoft.com/en-us/azure/compliance/offerings/offering-hipaa-us)

---

## Support

For issues or questions:
- Open GitHub issue with label `monitoring`
- Email: mark@aurelianware.com
- Include workbook name, error message, and screenshot
