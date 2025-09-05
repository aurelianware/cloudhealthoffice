
# Logic App Standard – 275 Ingestion Template

This template implements the 275 ingestion pattern described in *Electronic Attachment and Correspondence Management Integration Design* (277/275 with QNXT & Availity).

**Key features**
- Poll SFTP for new/updated files (providers → Availity → Health Plan)
- Store raw 275 file to Blob (`raw/275/`)
- **Decode X12** 275 via Integration Account connector
- Extract claim/member metadata (TRN02, NM1 segments, DTM)
- Branch: **Solicited** (has RFAI reference) vs **Unsolicited**
- Publish message to **Service Bus** topic (`attachments-in`)
- Call QNXT **Claim Linkage** API with retry policy
- Log custom event to **Application Insights**

> Source design: 275 Electronic Attachment and Correspondence Management Integration Design. fileciteturn10file0

## Prerequisites
- Logic Apps **Standard**
- Connections: `sftp-ssh`, `azureblob`, `servicebus`, `integrationaccount`
- Azure Storage account + container for raw files
- Service Bus namespace + topic (`attachments-in`) and dead-letter handling
- QNXT API endpoint to link document → claim
- Application Insights (optional; adjust `appinsights_custom_events_url`)

## Parameters to set
- `sftp_inbound_folder`: e.g., `/inbound/attachments`
- `blob_storage_account`: connection reference name
- `blob_raw_folder`: e.g., `raw/275`
- `sb_namespace`, `sb_topic`
- `qnxt_base_url`, `qnxt_api_token`
- `x12_sender_id`, `x12_receiver_id`
- `appinsights_custom_events_url`

## Decoding X12
Update `messageType` to match your TR3 version: `X12_005010X210_275` (or `006020X314` etc.). Ensure your Integration Account has the schema and agreement configured.

## Metadata mapping (placeholders)
- `claimNumber` ← `TRN02`
- `memberId` ← `2100D NM1*IL NM109`
- `providerNpi` ← `2100B NM1*PR NM109`
- `serviceFromDate` ← `DTM*472 DTM02`
- `rfaiReference` ← `REF` (value used to correlate RFAI)

Adjust expressions under **Extract_Metadata** accordingly.

## Error handling
- The HTTP action to QNXT uses retry policy (4 × 15s). Consider wrapping the whole flow in a `Scope` with **Run After** to route failures to a dead-letter Service Bus topic.

## Deployment
1. Create a **Logic App (Standard)**; add connections for SFTP, Blob, Service Bus, Integration Account.
2. Create a workflow named `ingest275` and replace its `workflow.json` with this file.
3. Set **Parameters** in the designer (or `host.json` app settings).
4. Test with sample 275 files in the SFTP inbound folder.

## Notes
- For unsolicited attachments, correlation may require additional keys (memberId + serviceFromDate + providerNpi).
- For solicited attachments, `rfaiReference` should be present from the 277 RFAI event.
- Add a second workflow for **outbound** 277 RFAI if needed.
