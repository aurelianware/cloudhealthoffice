# ValueAdds277 Configuration-to-Workflow Parameter Mapping

> **Note:** This document was originally written for Azure Logic Apps workflows. CHO has since migrated to Argo Workflows on AKS — see [ADR-004](../adr/004-remove-logic-apps.md) for details. The configuration schema and parameter mapping concepts still apply; the runtime is now Argo DAG steps backed by C# microservices instead of Logic App actions.

## Configuration Schema Location

**File**: `core/schemas/clearinghouse-integration-config.schema.json`
**Path**: `definitions.ECSModule.properties.valueAdds277`

## Workflow Parameters Location

**Original (Legacy)**: `logicapps/workflows/ecs_summary_search/workflow.json` (removed)
**Current**: Argo workflow YAML in `infrastructure/argo-workflows/` with parameters passed via Kubernetes ConfigMaps

## Field Mapping

| Configuration Path | Workflow Parameter | Default | Description |
|-------------------|-------------------|---------|-------------|
| `valueAdds277.claimFields.financial` | `includeFinancialFields` | `true` | Include 8 financial fields (BILLED, ALLOWED, etc.) |
| `valueAdds277.claimFields.clinical` | `includeClinicalFields` | `true` | Include 4 clinical fields (drgCode, diagnosisCodes, etc.) |
| `valueAdds277.claimFields.demographics` | `includeDemographicFields` | `true` | Include 4 demographic objects (patient, subscriber, providers) |
| `valueAdds277.claimFields.remittance` | `includeRemittanceFields` | `true` | Include 4 remittance fields (payeeName, checkNumber, etc.) |
| `valueAdds277.integrationFlags.*` | `includeIntegrationFlags` | `true` | Include 6 integration flags (eligibleForAppeal, etc.) |
| `valueAdds277.serviceLineFields.enabled` | `includeServiceLineDetails` | `true` | Include service line array with 10+ fields per line |

## Configuration Example

```json
{
  "modules": {
    "ecs": {
      "enabled": true,
      "valueAdds277": {
        "enabled": true,
        "claimFields": {
          "financial": true,
          "remittance": true,
          "clinical": true,
          "demographics": true,
          "statusDetails": true
        },
        "serviceLineFields": {
          "enabled": true,
          "includeFinancials": true,
          "includeModifiers": true
        },
        "integrationFlags": {
          "attachments": true,
          "corrections": true,
          "appeals": true,
          "messaging": true,
          "chat": false,
          "remittanceViewer": true
        }
      }
    }
  }
}
```

## Workflow Parameter Usage

The workflow uses these parameters in the claims mapping step. Originally this was a Logic App `Select` action; it is now handled by the ECS summary C# service in the Argo DAG. The mapping logic is equivalent:

```json
{
  "BILLED": "conditionally included if includeFinancialFields is true",
  "drgCode": "conditionally included if includeClinicalFields is true",
  "patient": "conditionally included if includeDemographicFields is true",
  "payeeName": "conditionally included if includeRemittanceFields is true",
  "eligibleForAppeal": "true if claimStatus is Denied or Partially Paid and includeIntegrationFlags is true",
  "serviceLines": "conditionally included if includeServiceLineDetails is true"
}
```

## Configuration Scenarios

### Scenario 1: Full ValueAdds277 (Recommended)

**Use Case**: Maximum provider value, all modules enabled  
**Configuration**: All flags set to `true`  
**Response Size**: ~6KB per claim  
**Provider Time Savings**: 21 minutes per claim lookup (81% reduction)

### Scenario 2: Financial + Remittance Only

**Use Case**: Billing office workflow, no appeals integration  
**Configuration**:
```json
{
  "claimFields": {
    "financial": true,
    "remittance": true,
    "clinical": false,
    "demographics": false,
    "statusDetails": false
  },
  "serviceLineFields": {
    "enabled": false
  },
  "integrationFlags": {
    "attachments": false,
    "corrections": false,
    "appeals": false,
    "messaging": true,
    "chat": false,
    "remittanceViewer": true
  }
}
```
**Response Size**: ~3KB per claim

### Scenario 3: Clinical Focus (Appeals + Denials)

**Use Case**: Appeals department workflow  
**Configuration**:
```json
{
  "claimFields": {
    "financial": true,
    "remittance": false,
    "clinical": true,
    "demographics": true,
    "statusDetails": true
  },
  "serviceLineFields": {
    "enabled": true
  },
  "integrationFlags": {
    "attachments": true,
    "corrections": true,
    "appeals": true,
    "messaging": true,
    "chat": false,
    "remittanceViewer": false
  }
}
```
**Response Size**: ~5KB per claim

### Scenario 4: Minimal (Backward Compatible)

**Use Case**: Legacy systems, bandwidth constraints  
**Configuration**:
```json
{
  "enabled": false
}
```
**Response Size**: ~2KB per claim (standard ECS)

## Testing Configuration

To test different configurations:

1. **Update workflow parameters** via Kubernetes ConfigMap or Helm values:
   ```bash
   kubectl -n cloudhealthoffice edit configmap ecs-config
   # Set includeFinancialFields=false, then restart the ECS pod
   ```

2. **Query ECS API** and verify response:
   ```bash
   curl -X POST \
     -H "Authorization: Bearer $TOKEN" \
     -H "Content-Type: application/json" \
     -d @test-request.json \
     https://api.<your-domain>/api/ecs/summary-search
   ```

3. **Validate Response** contains/excludes expected fields:
   ```bash
   # Should be null when includeFinancialFields=false
   jq '.claims[0].BILLED' response.json
   ```

## Integration Flag Behavior

### eligibleForAppeal

**Configuration**: `integrationFlags.appeals`  
**Logic**: `true` if claim status is "Denied" or "Partially Paid"  
**Additional Fields Populated**:
- `appealMessage`: "This claim may be eligible for appeal. Timely filing deadline: YYYY-MM-DD"
- `appealType`: "Reconsideration"
- `appealTimelyFilingDate`: Calculated as +180 days from finalized date

**Provider Workflow**:
1. Query claim via ECS
2. See `eligibleForAppeal: true`
3. Click "Dispute Claim" button in UI
4. Appeal workflow pre-populated with metadata
5. Submit appeal with supporting documentation

### eligibleForAttachment

**Configuration**: `integrationFlags.attachments`  
**Logic**: `true` if claim status is "Pending", "In Process", or "Suspended"  
**Provider Workflow**:
1. Query claim via ECS
2. See `eligibleForAttachment: true`
3. Click "Send Attachments" button in UI
4. Upload medical records via HIPAA 275 workflow

### eligibleForRemittanceViewer

**Configuration**: `integrationFlags.remittanceViewer`  
**Logic**: `true` if claim status is "Paid" or "Partially Paid"  
**Provider Workflow**:
1. Query claim via ECS
2. See `eligibleForRemittanceViewer: true`
3. Click "View Remittance" button in UI
4. Display complete remittance details (835 data)

## Backend Field Mapping

The workflow expects claims backend backend to provide these fields:

| ValueAdds277 Field | claims backend Field(s) | Fallback |
|-------------------|---------------|----------|
| `BILLED` | `BILLED` or `billedAmount` | `billedAmount` |
| `ALLOWED` | `ALLOWED` or `allowedAmount` | `allowedAmount` |
| `INSURANCE_TOTAL_PAID` | `INSURANCE_TOTAL_PAID` or `paidAmount` | `paidAmount` |
| `PATIENT_RESPONSIBILITY` | `PATIENT_RESPONSIBILITY` or `patientResponsibility` | `patientResponsibility` |
| `COPAY` | `COPAY` | `null` |
| `COINSURANCE` | `COINSURANCE` | `null` |
| `DEDUCTIBLE` | `DEDUCTIBLE` | `null` |
| `DISCOUNT` | `DISCOUNT` | `null` |
| `drgCode` | `drgCode` | `null` |
| `diagnosisCodes` | `diagnosisCodes` | `null` |
| `patient.*` | `patient.*` | Constructed from `memberName` |

**Note**: Workflow uses `coalesce()` functions to provide graceful degradation when backend fields are missing.

## Performance Impact

| Configuration | Avg Response Time | Response Size | Backend Query Time |
|--------------|-------------------|---------------|-------------------|
| Disabled | 200ms | 2KB | 150ms |
| Financial only | 220ms | 3KB | 150ms |
| Full ValueAdds277 | 250ms | 6KB | 150ms |

**Key Findings**:
- Response time increase is minimal (50ms)
- Most time spent in backend query (150ms)
- Response transformation is efficient (~100ms)
- Network transfer time depends on bandwidth

## Troubleshooting

### Issue: Financial fields returning null

**Cause**: `includeFinancialFields` parameter is `false`  
**Solution**: Set parameter to `true` in workflow configuration (Kubernetes ConfigMap)

### Issue: Integration flags not appearing

**Cause**: `includeIntegrationFlags` parameter is `false`  
**Solution**: Set parameter to `true` in workflow configuration (Kubernetes ConfigMap)

### Issue: Service lines missing

**Cause**: `includeServiceLineDetails` parameter is `false`  
**Solution**: Set parameter to `true` in workflow configuration (Kubernetes ConfigMap)

### Issue: Backend doesn't have ValueAdds277 fields

**Cause**: claims backend backend not returning enhanced fields  
**Solution**: Workflow uses fallbacks. Map standard claims backend fields to ValueAdds277 equivalents in backend transformation layer.

## References

- [UNIFIED-CONFIG-SCHEMA.md](../UNIFIED-CONFIG-SCHEMA.md) - Complete configuration reference
- [ECS-INTEGRATION.md](../ECS-INTEGRATION.md) - ECS module documentation
- [VALUEADDS277-README.md](../VALUEADDS277-README.md) - Quick start guide
- [CONFIG-TO-WORKFLOW-GENERATOR.md](../CONFIG-TO-WORKFLOW-GENERATOR.md) - Automated workflow generation
