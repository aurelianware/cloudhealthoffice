# Logic Apps Workflows Instructions

**Applies to**: `logicapps/workflows/*/workflow.json`

## Overview

Azure Logic Apps Standard workflows for processing HIPAA X12 EDI transactions (275, 277, 278).

## Workflow Files

### ingest275/workflow.json
**Purpose**: HIPAA 275 Attachment Ingestion  
**Trigger**: SFTP polling for new 275 files from Availity  
**Flow**:
1. Poll Availity SFTP for new .edi files
2. Archive raw file to Data Lake (`hipaa-attachments/raw/275/yyyy/MM/dd/`)
3. Decode X12 275 message via Integration Account
4. Extract claim/member/provider metadata
5. Call QNXT API to link attachment to claim
6. Publish event to Service Bus topic `attachments-in`
7. Delete file from SFTP after processing

**Key Properties**:
- Kind: `Stateful`
- Retry logic: 4 retries with 15-second intervals for QNXT calls
- Required connections: sftp-ssh, azureblob, servicebus, integrationaccount

### ingest278/workflow.json
**Purpose**: HIPAA 278 Health Care Services Review Information Processing  
**Trigger**: SFTP polling for new 278 files  
**Flow**:
1. Poll SFTP for new 278 transaction files
2. Archive to Data Lake (`hipaa-attachments/raw/278/yyyy/MM/dd/`)
3. Decode X12 278 message via Integration Account
4. Extract review information metadata
5. Publish event to Service Bus topic `edi-278`
6. Delete file from SFTP after processing

**Key Properties**:
- Kind: `Stateful`
- Required connections: sftp-ssh, azureblob, servicebus, integrationaccount

### replay278/workflow.json
**Purpose**: Deterministic HTTP endpoint for replaying 278 transactions  
**Trigger**: HTTP POST request  
**Flow**:
1. Accept HTTP POST with `blobUrl` and optional `fileName` parameters
2. Validate blobUrl contains `hipaa-attachments` path
3. Queue message to Service Bus topic `edi-278` for reprocessing
4. Return success/error response

**Key Properties**:
- Kind: `Stateful`
- HTTP trigger with anonymous auth (configure auth post-deployment)
- Validation: Ensures blobUrl is valid before queueing

**Example Request**:
```json
POST /api/replay278/triggers/HTTP_Replay_278_Request/invoke
{
  "blobUrl": "hipaa-attachments/raw/278/2024/01/15/review-request.edi",
  "fileName": "custom-replay-name"
}
```

### rfai277/workflow.json
**Purpose**: HIPAA 277 RFAI (Request for Additional Information) Outbound  
**Trigger**: Service Bus topic `rfai-requests` subscription  
**Flow**:
1. Consume RFAI request from Service Bus
2. Generate X12 277 message via Integration Account
3. Send 277 file to Availity via SFTP
4. Archive sent file to Data Lake
5. Update status in QNXT system

**Key Properties**:
- Kind: `Stateful`
- Required connections: servicebus, integrationaccount, sftp-ssh, azureblob

## Required Structure

### All workflow.json Files Must Have
```json
{
  "definition": {
    "$schema": "...",
    "actions": { /* workflow actions */ },
    "triggers": { /* workflow trigger */ },
    "parameters": { /* workflow parameters */ }
  },
  "kind": "Stateful",  // Must be Stateful for all 4 workflows
  "parameters": {
    /* parameter definitions */
  }
}
```

### Critical Keys
- `definition`: Complete workflow definition object
- `kind`: Must be `"Stateful"` (Logic Apps Standard requirement)
- `parameters`: Workflow parameter definitions

## Validation

### JSON Syntax
```bash
# Validate JSON syntax
jq . logicapps/workflows/*/workflow.json

# Validate required keys
find logicapps/workflows -name "workflow.json" -exec \
  jq -e 'has("definition") and has("kind") and has("parameters")' {} \;

# Verify all are Stateful
find logicapps/workflows -name "workflow.json" -exec \
  jq -e '.kind == "Stateful"' {} \;
```

### Automated Checks
The `pr-lint.yml` workflow validates:
- Valid JSON syntax
- Required keys present
- Stateful/Stateless kind value

## Best Practices

### Workflow Design
- Use meaningful action names that describe purpose
- Include error handling for external API calls
- Add retry policies for transient failures
- Use Service Bus for async/decoupled processing
- Archive all processed files to Data Lake

### Triggers
- **SFTP Trigger**: Use polling interval appropriate for volume
- **Service Bus Trigger**: Use topic subscriptions for fan-out
- **HTTP Trigger**: Validate inputs before processing

### Actions
- **SFTP Operations**: Always delete after successful processing
- **Blob Storage**: Use date-based partitioning (`yyyy/MM/dd`)
- **Service Bus**: Use topics for pub/sub patterns
- **Integration Account**: Required for X12 encoding/decoding

### Connection References
All workflows require configured API connections:
- `sftp-ssh`: Availity SFTP (ingest workflows)
- `azureblob`: Data Lake Storage (all workflows)
- `servicebus`: Service Bus topics (all workflows)
- `integrationaccount`: X12 processing (all workflows)

### Parameters
Common workflow parameters:
- `sftp_inbound_folder`: "/inbound/attachments"
- `blob_raw_folder`: "hipaa-attachments/raw/275"
- `blob_raw_folder_278`: "hipaa-attachments/raw/278"
- `sb_topic`: "attachments-in"
- `sb_topic_edi278`: "edi-278"
- `sb_topic_rfai`: "rfai-requests"
- `x12_messagetype_275`: "X12_005010X210_275"
- `x12_messagetype_277`: "X12_005010X212_277"
- `x12_messagetype_278`: "X12_005010X217_278"

## Integration Account Setup

### Required X12 Schemas
- **275 Attachment**: X12_005010X210_275
- **277 RFAI**: X12_005010X212_277  
- **278 Review**: X12_005010X217_278

### Trading Partners
- **Sender**: Availity (ID: 030240928)
- **Receiver**: PCHP-QNXT (ID: 66917)

## Deployment

### Package Creation
```bash
# Create deployment ZIP
cd logicapps
zip -r ../workflows.zip workflows/

# Verify ZIP structure
unzip -l ../workflows.zip
# Should show: workflows/ingest275/, workflows/ingest278/, 
#              workflows/replay278/, workflows/rfai277/
```

### Deploy to Logic App
```bash
# Deploy workflows ZIP
az webapp deploy \
  --resource-group <rg-name> \
  --name <logicapp-name> \
  --src-path workflows.zip \
  --type zip

# Restart Logic App
az webapp restart \
  --resource-group <rg-name> \
  --name <logicapp-name>
```

## Common Scenarios

### Adding a New Workflow
1. Create directory under `logicapps/workflows/`
2. Create `workflow.json` with required structure
3. Set `"kind": "Stateful"`
4. Define trigger and actions
5. Validate JSON syntax and structure
6. Test locally if possible
7. Deploy via ZIP package

### Modifying Existing Workflow
1. **Always backup** current workflow.json
2. Make targeted changes (minimal edits)
3. Validate JSON syntax: `jq . workflow.json`
4. Test in DEV environment first
5. Monitor Application Insights after deployment
6. Keep rollback plan ready

### Updating Service Bus Topics
1. Ensure topic exists in infrastructure (Bicep)
2. Update workflow parameters
3. Test message publishing/consumption
4. Verify dead-letter queue handling

### Adding Integration Account Schema
1. Import schema to Integration Account
2. Update workflow to reference new schema name
3. Configure trading partner agreement if needed
4. Test X12 encoding/decoding

## Troubleshooting

### Workflow Won't Start
- Check API connection authentication
- Verify managed identity has required permissions
- Review Application Insights for startup errors
- Check Logic App configuration settings

### SFTP Trigger Not Firing
- Verify SFTP connection is authenticated
- Check folder path is correct
- Ensure files match trigger pattern
- Review SFTP server logs

### Service Bus Errors
- Verify topic exists and is active
- Check managed identity has Data Sender role
- Review message size limits
- Check for dead-letter messages

### Integration Account Issues
- Verify schema is uploaded
- Check trading partner configuration
- Ensure agreement is active
- Review X12 message format

## Security & Compliance

### HIPAA Requirements
- All file transfers use SSH/SFTP encryption
- Data at rest encrypted in Data Lake
- No PHI in workflow names or parameters
- Enable diagnostic logging
- Audit all access

### Managed Identity
- Use for all Azure service connections
- Grant minimal required permissions
- Avoid connection strings/keys in workflows
- Review role assignments regularly

### Monitoring
- Enable Application Insights integration
- Track all workflow runs
- Alert on failures and performance issues
- Monitor Service Bus metrics
