# Azure Monitor Alerting Setup Guide

## Quick Start

This guide helps you deploy production-ready alert rules for Cloud Health Office monitoring dashboards.

---

## Prerequisites

1. **Application Insights** deployed (done via main.bicep)
2. **Action Group** created for notifications
3. **Azure CLI** installed and authenticated

---

## Step 1: Create Action Group

An Action Group defines who gets notified and how (email, SMS, webhook, etc.).

### Option A: Create via Azure Portal

1. Navigate to **Monitor** → **Alerts** → **Action groups**
2. Click **+ Create**
3. Fill in details:
   - **Resource group**: Same as your Cloud Health Office deployment
   - **Name**: `CloudHealthOffice-Alerts`
   - **Display name**: `CHO Alerts`
4. Add notifications:
   - **Email**: Operations team email
   - **SMS**: On-call phone number (for critical alerts)
   - **Webhook**: PagerDuty/Slack integration (optional)
5. Click **Review + Create**

### Option B: Create via Azure CLI

```bash
# Create email notification action group
az monitor action-group create \
  --resource-group <resource-group-name> \
  --name CloudHealthOffice-Alerts \
  --short-name CHOAlerts \
  --action email ops-team ops@example.com

# Add SMS notification for critical alerts
az monitor action-group update \
  --resource-group <resource-group-name> \
  --name CloudHealthOffice-Alerts \
  --add action sms oncall +1234567890
```

---

## Step 2: Get Required Resource IDs

```bash
# Get Application Insights resource ID
APP_INSIGHTS_ID=$(az monitor app-insights component show \
  --resource-group <resource-group-name> \
  --app <app-insights-name> \
  --query id -o tsv)

echo "Application Insights ID: $APP_INSIGHTS_ID"

# Get Action Group resource ID
ACTION_GROUP_ID=$(az monitor action-group show \
  --resource-group <resource-group-name> \
  --name CloudHealthOffice-Alerts \
  --query id -o tsv)

echo "Action Group ID: $ACTION_GROUP_ID"
```

---

## Step 3: Deploy Alert Rules

Deploy all 6 recommended alert rules using the ARM template:

```bash
az deployment group create \
  --resource-group <resource-group-name> \
  --template-file docs/examples/azure-monitor-alerts-config.json \
  --parameters appInsightsId="$APP_INSIGHTS_ID" \
               actionGroupId="$ACTION_GROUP_ID"
```

This deploys:

1. **Low Success Rate Alert** (Severity 2: Warning)
2. **High Latency Alert** (Severity 2: Warning)
3. **PHI Exposure Alert** (Severity 0: Critical)
4. **Unencrypted Traffic Alert** (Severity 0: Critical)
5. **Payer Integration Failure Alert** (Severity 3: Informational)
6. **Dependency Failure Alert** (Severity 2: Warning)

---

## Step 4: Verify Deployment

```bash
# List all alert rules
az monitor scheduled-query list \
  --resource-group <resource-group-name> \
  --output table

# Check specific alert
az monitor scheduled-query show \
  --resource-group <resource-group-name> \
  --name CloudHealthOffice-<unique>-LowSuccessRate
```

---

## Alert Severity Levels

| Severity | Level | Use Case | Response Time |
|----------|-------|----------|---------------|
| **0** | Critical | PHI exposure, unencrypted traffic | < 5 minutes |
| **1** | Error | Not used in this config | < 15 minutes |
| **2** | Warning | Low success rate, high latency, dependency failures | < 30 minutes |
| **3** | Informational | No transactions in 24h | < 2 hours |
| **4** | Verbose | Not used in this config | Monitor only |

---

## Customizing Alert Rules

### Change Thresholds

Edit `azure-monitor-alerts-config.json` before deployment:

```json
// Example: Change success rate threshold from 95% to 98%
{
  "query": "... | where SuccessRate < 98",
  "threshold": 0
}
```

### Change Evaluation Frequency

```json
{
  "evaluationFrequency": "PT5M",  // Every 5 minutes
  "windowSize": "PT15M"           // Over 15 minute window
}
```

Common values:
- `PT5M` = 5 minutes
- `PT15M` = 15 minutes
- `PT1H` = 1 hour
- `P1D` = 1 day

### Add Custom Alerts

Use the Azure Portal or CLI:

```bash
az monitor scheduled-query create \
  --resource-group <resource-group-name> \
  --name MyCustomAlert \
  --scopes "$APP_INSIGHTS_ID" \
  --condition "count 'Heartbeat | where Status == \"Unhealthy\"' > 0" \
  --description "Custom alert description" \
  --action-groups "$ACTION_GROUP_ID" \
  --severity 2 \
  --evaluation-frequency PT5M \
  --window-size PT15M
```

---

## Testing Alerts

### Test Low Success Rate Alert

Temporarily introduce failures to test alerting:

```bash
# This will cause workflow failures
# Remove after testing!
```

⚠️ **Do NOT test in production without approval**

### Test PHI Exposure Alert

This alert should **never** fire in production. To test:

1. Deploy to a test environment
2. Temporarily log a test SSN pattern: `"123-45-6789"`
3. Wait 5 minutes for alert to fire
4. Verify notification received
5. Remove test code immediately

---

## Alert Actions

### Email Notifications

Default email includes:
- Alert name and description
- Severity level
- Firing threshold
- Link to Application Insights query
- Affected resource details

### SMS Notifications

SMS messages are brief:
- Alert name
- Severity
- Resource name

⚠️ **Limit SMS to critical alerts only to avoid spam**

### Webhook Integration

Forward alerts to incident management systems:

```bash
az monitor action-group update \
  --resource-group <resource-group-name> \
  --name CloudHealthOffice-Alerts \
  --add action webhook pagerduty https://events.pagerduty.com/integration/<key>/enqueue
```

Supported integrations:
- PagerDuty
- ServiceNow
- Slack
- Microsoft Teams
- Custom webhooks

---

## Alert Suppression

Suppress alerts during maintenance windows:

```bash
# Create suppression rule
az monitor alert-processing-rule create \
  --resource-group <resource-group-name> \
  --name MaintenanceWindow \
  --scopes "$APP_INSIGHTS_ID" \
  --rule-type Suppression \
  --schedule-start-datetime "2024-01-15T00:00:00" \
  --schedule-end-datetime "2024-01-15T04:00:00" \
  --schedule-recurrence-type Weekly \
  --schedule-recurrence-days Sunday
```

---

## Monitoring Alert Health

### View Fired Alerts

```bash
# List recent alerts
az monitor metrics alert list \
  --resource-group <resource-group-name> \
  --output table

# Get alert history
az monitor activity-log list \
  --resource-group <resource-group-name> \
  --caller "Azure Monitor Alerts" \
  --start-time 2024-01-01T00:00:00Z \
  --output table
```

### Alert Metrics

Track alert effectiveness:
- **False positive rate**: Alerts that fired but were not issues
- **Response time**: Time from alert to resolution
- **MTTD** (Mean Time To Detect): Time from incident to alert
- **MTTR** (Mean Time To Resolve): Time from alert to fix

---

## Cost Considerations

Alert rule pricing (approximate):
- **Standard alerts**: $0.10 per alert rule per month
- **Log search queries**: Included in Application Insights data ingestion
- **Action Group notifications**:
  - Email: Free (unlimited)
  - SMS: $0.01 per SMS
  - Webhook: Free (unlimited)

**Example monthly cost for 6 alerts**:
- 6 alert rules × $0.10 = $0.60/month
- 100 SMS notifications = $1.00/month
- **Total: ~$2/month**

---

## Best Practices

### 1. Alert Fatigue Prevention
- Set appropriate thresholds (95% success rate, not 100%)
- Use warning → critical escalation
- Implement alert suppression during maintenance
- Review and tune alerts monthly

### 2. On-Call Rotation
- Use Action Groups to define on-call schedule
- Rotate SMS notifications weekly
- Keep email DL for all alerts (audit trail)

### 3. Incident Response
- Document response procedures for each alert
- Create runbooks for common issues
- Track MTTR in dashboards

### 4. Alert Testing
- Test alerts quarterly in non-production
- Validate notification delivery
- Verify escalation paths

### 5. Compliance
- Retain alert history for HIPAA audits (6+ years)
- Document all PHI exposure incidents
- Review security alerts weekly

---

## Troubleshooting

### Alert Not Firing

1. Check alert is enabled:
   ```bash
   az monitor scheduled-query show \
     --resource-group <rg> \
     --name <alert-name> \
     --query enabled
   ```

2. Test KQL query in Application Insights → Logs
3. Verify evaluation frequency and window size
4. Check Action Group is attached

### Not Receiving Notifications

1. Verify Action Group email/SMS:
   ```bash
   az monitor action-group show \
     --resource-group <rg> \
     --name CloudHealthOffice-Alerts
   ```

2. Check spam/junk folders
3. Verify phone number format (include country code)
4. Test Action Group:
   ```bash
   az monitor action-group test-notifications create \
     --resource-group <rg> \
     --action-group-name CloudHealthOffice-Alerts \
     --alert-type servicehealth
   ```

### High False Positive Rate

1. Increase threshold (e.g., 95% → 90% success rate)
2. Extend window size (15min → 30min)
3. Reduce evaluation frequency (5min → 15min)
4. Add more specific filters to KQL query

---

## Support

For questions or issues:
- GitHub Issues: https://github.com/aurelianware/cloudhealthoffice/issues
- Email: mark@aurelianware.com
- Include alert name, resource group, and error message
