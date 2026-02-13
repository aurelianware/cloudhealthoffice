# Argo Workflows - Multi-Tenant Trading Partner Path Update

## Overview

Argo workflows must be updated to support the new SFTP directory structure:
- **Old**: `/inbound/{transaction-type}/` (single namespace)
- **New**: `/tenants/{tenant-id}/{trading-partner-id}/inbound/{transaction-type}/` (multi-tenant, multi-partner)

## Required Parameter Updates

All X12 transaction workflows need two additional parameters:

### Before (Single-Tenant)
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "default-payer"
    - name: sftp-folder
      value: "/inbound/837"
```

### After (Multi-Tenant, Multi-Partner)
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "bcbs-florida"
      description: "Tenant ID (health plan)"
    - name: trading-partner-id
      value: "availity"
      description: "Trading partner ID (clearinghouse, provider, lab)"
    - name: transaction-type
      value: "837"
      description: "X12 transaction type"
    - name: sftp-folder
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/{{workflow.parameters.transaction-type}}"
    - name: archive-folder
      value: "/archive/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/{{workflow.parameters.transaction-type}}"
```

## Workflows Requiring Updates

### 1. X12 837 Claims Ingestion
**File**: `argo-workflows/x12-837-ingest.yaml`

**Changes**:
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "bcbs-florida"
    - name: trading-partner-id
      value: "availity"
    - name: transaction-type
      value: "837"
    - name: sftp-folder
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/{{workflow.parameters.transaction-type}}"
```

**Routing Logic**:
- Files arrive at: `/tenants/bcbs-florida/availity/inbound/837/claim-001.edi`
- Workflow reads tenant-id and trading-partner-id from path
- Claims Service receives both IDs for proper routing

### 2. X12 276 Claim Status Request
**File**: `argo-workflows/x12-276-claim-status-request.yaml`

**Changes**:
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "bcbs-florida"
    - name: trading-partner-id
      value: "change-healthcare"
    - name: transaction-type
      value: "276"
    - name: sftp-inbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/276"
    - name: sftp-outbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/outbound/277"
```

**Bi-Directional Flow**:
- Inbound 276: Tenant uploads request to `/tenants/{tenant}/{partner}/inbound/276/`
- Outbound 277: System writes response to `/tenants/{tenant}/{partner}/outbound/277/`

### 3. X12 278 Prior Authorization
**File**: `argo-workflows/x12-278-prior-auth.yaml`

**Changes**:
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "aetna"
    - name: trading-partner-id
      value: "availity"
    - name: transaction-type
      value: "278"
    - name: sftp-inbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/278"
    - name: sftp-outbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/outbound/278"
```

### 4. X12 834 Enrollment
**File**: `argo-workflows/x12-834-enrollment-import.yaml`

**Changes**:
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "cigna"
    - name: trading-partner-id
      value: "employer-tpa-1"
    - name: transaction-type
      value: "834"
    - name: sftp-folder
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/834"
```

**Use Case**: Employer TPA sends enrollment roster to health plan

### 5. X12 275 Attachment Solicited/Unsolicited
**File**: `argo-workflows/x12-275-attachment.yaml`

**Changes**:
```yaml
arguments:
  parameters:
    - name: tenant-id
      value: "bcbs-florida"
    - name: trading-partner-id
      value: "quest-diagnostics"
    - name: transaction-type
      value: "275"
    - name: attachment-direction
      value: "solicited"  # or "unsolicited"
    - name: sftp-inbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/inbound/275"
    - name: sftp-outbound-path
      value: "/tenants/{{workflow.parameters.tenant-id}}/{{workflow.parameters.trading-partner-id}}/outbound/275"
```

**Use Case**: Health plan requests lab results from Quest

## SFTP Client Container Updates

All SFTP fetch/upload steps must construct full paths:

### Old SFTP Fetch
```yaml
- name: fetch-from-sftp
  container:
    image: cloudhealthoffice/sftp-client:latest
    command: ["/bin/sh", "-c"]
    args:
      - |
        sftp -i /ssh/id_rsa logicapp@sftp-server.cho-sftp:22 <<EOF
          cd /inbound/837
          mget *.edi /work/
          exit
        EOF
```

### New SFTP Fetch (Tenant-Aware)
```yaml
- name: fetch-from-sftp
  container:
    image: cloudhealthoffice/sftp-client:latest
    command: ["/bin/sh", "-c"]
    args:
      - |
        TENANT_ID="{{workflow.parameters.tenant-id}}"
        PARTNER_ID="{{workflow.parameters.trading-partner-id}}"
        TRANS_TYPE="{{workflow.parameters.transaction-type}}"
        
        sftp -i /ssh/id_rsa ${TENANT_ID}@sftp-server.cho-sftp:22 <<EOF
          cd ${PARTNER_ID}/inbound/${TRANS_TYPE}
          mget *.edi /work/
          exit
        EOF
```

**Key Changes**:
- Use tenant-id as SFTP username (not shared `logicapp` account)
- Construct path from parameters: `{partner}/inbound/{trans}`
- Chroot jail automatically isolates tenant

## Trading Partner Service Integration

Workflows should query Trading Partner Service for routing metadata:

```yaml
- name: get-trading-partner-config
  container:
    image: curlimages/curl:latest
    command: ["/bin/sh", "-c"]
    args:
      - |
        curl -s \
          -H "X-Tenant-ID: {{workflow.parameters.tenant-id}}" \
          http://trading-partner-service.cloudhealthoffice/api/trading-partners/{{workflow.parameters.trading-partner-id}} \
          > /tmp/partner-config.json
        
        # Extract SFTP paths
        INBOUND_PATH=$(jq -r '.sftpConfig.paths.inbound["837"]' /tmp/partner-config.json)
        OUTBOUND_PATH=$(jq -r '.sftpConfig.paths.outbound["837"]' /tmp/partner-config.json)
        
        echo "Inbound: $INBOUND_PATH"
        echo "Outbound: $OUTBOUND_PATH"
```

**Benefits**:
- Centralized path configuration in CosmosDB
- No hardcoded paths in workflows
- Dynamic routing based on trading partner metadata

## Event-Driven Triggers

Argo Events sensors must watch tenant/trading-partner-specific paths:

### Old Sensor (Single Path)
```yaml
spec:
  triggers:
    - template:
        name: 837-ingest-trigger
        conditions: "has_file"
        k8s:
          parameters:
            - src:
                dataKey: body.path
                value: "/inbound/837/claim-*.edi"
```

### New Sensor (Multi-Tenant Paths)
```yaml
spec:
  triggers:
    - template:
        name: 837-ingest-trigger
        conditions: "has_file"
        k8s:
          parameters:
            - src:
                dataKey: body.path
                # Regex: /tenants/{tenant}/{partner}/inbound/837/*.edi
              dest: spec.arguments.parameters.0.value
              operation: prepend
            - src:
                dataKey: body.tenant_id
                # Extract tenant from path
              dest: spec.arguments.parameters.1.value
            - src:
                dataKey: body.trading_partner_id
                # Extract partner from path
              dest: spec.arguments.parameters.2.value
```

**Path Parsing**:
```bash
# Input: /tenants/bcbs-florida/availity/inbound/837/claim-001.edi
# Extract:
#   tenant-id: bcbs-florida
#   trading-partner-id: availity
#   transaction-type: 837
#   filename: claim-001.edi
```

## Migration Strategy

### Phase 1: Add Parameters (No Breaking Changes)
1. Add `trading-partner-id` parameter to all workflows
2. Default value: `"default"` (backward compatible)
3. Keep old path logic for existing tenants

### Phase 2: Dual-Path Support
1. Check if new path exists first: `/tenants/{tenant}/{partner}/inbound/{trans}/`
2. Fall back to old path: `/inbound/{trans}/`
3. Log deprecation warning for old path usage

### Phase 3: Full Cutover
1. All tenants migrated to new structure
2. Remove old path logic
3. Enforce trading-partner-id as required parameter

## Testing

### Test Workflow Submission
```bash
# Submit 837 ingest workflow with tenant + trading partner
argo submit \
  -n cloudhealthoffice \
  --from workflowtemplate/x12-837-ingest \
  -p tenant-id="bcbs-florida" \
  -p trading-partner-id="availity" \
  -p transaction-type="837"

# Submit 276 claim status with different partner
argo submit \
  -n cloudhealthoffice \
  --from workflowtemplate/x12-276-claim-status-request \
  -p tenant-id="bcbs-florida" \
  -p trading-partner-id="change-healthcare" \
  -p transaction-type="276"
```

### Verify Path Construction
```bash
# Get workflow logs
argo logs -n cloudhealthoffice @latest

# Check SFTP paths in workflow
# Should see: /tenants/bcbs-florida/availity/inbound/837/
```

## Security Considerations

1. **Tenant Isolation**: Each tenant has unique SFTP credentials (username = tenant-id)
2. **Chroot Jail**: Tenants cannot traverse to parent directories or other tenants
3. **Trading Partner Metadata**: Stored in CosmosDB with tenant-id partition key
4. **Workflow RBAC**: Service account `argo-workflow-sa` has read access to all tenant paths
5. **Audit Logging**: All file access logged with tenant + trading partner context

## Documentation Updates Needed

- [ ] Update workflow README files with new parameters
- [ ] Document Trading Partner Service API for path lookups
- [ ] Create runbook for adding new trading partners to existing tenants
- [ ] Update Argo Events sensor configuration examples
- [ ] Add troubleshooting guide for path-related errors

## Related Files

- [docs/SFTP-MULTI-TENANT-ARCHITECTURE.md](../docs/SFTP-MULTI-TENANT-ARCHITECTURE.md) - SFTP architecture
- [scripts/provision-sftp-tenant.sh](../scripts/provision-sftp-tenant.sh) - Tenant provisioning
- [scripts/provision-trading-partner.sh](../scripts/provision-trading-partner.sh) - Trading partner provisioning
- [services/trading-partner-service/Models/TradingPartner.cs](../services/trading-partner-service/Models/TradingPartner.cs) - Data model
