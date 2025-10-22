# HIPAA Attachments Logic Apps Package This package contains: - workflow.ingest275.json — **Inbound 275 ingestion** workflow (SFTP → Blob → Decode X12 → SB → QNXT) - workflow.rfai277.json — **Outbound 277 RFAI** workflow (SB → Encode X12 → SFTP) - main.bicep — Starter infrastructure template (Storage, Service Bus, App Insights, Logic App Standard plan/app) ## Deploy (starter)
bash
az group create -n pchp-attachments-rg -l eastus
az deployment group create -g pchp-attachments-rg -f main.bicep -p baseName=pchp-attachments
> Note: The Bicep template is a **starter**. You will still need to: > - Create **API Connections** for sftp-ssh, azureblob, servicebus, and integrationaccount (or use managed connectors via LA Standard). > - Import the two workflow JSON files into your Logic App (Standard) as separate workflows. > - Configure Integration Account schemas/agreements for 275/277 (set x12_275_messagetype and x12_277_messagetype). > - Set workflow parameters (folders, namespaces, topics, IDs). > - Add Role Assignments for the Logic App’s managed identity to access Storage & Service Bus. ## RFAI Trigger Contract Messages to rfai-requests topic should be JSON:
json
{
  "claimNumber": "PCHP-12345",
  "memberId": "DExxxxxx",
  "providerNpi": "1234567890",
  "serviceFromDate": "2025-01-01",
  "rfaiReasonCode": "A7",
  "rfaiReference": "RFAI-001"
}
## X12 Message Types (set in parameters) - 275: X12_005010X210_275 (or your version) - 277 (RFAI): X12_005010X212_277 ## Next steps - Wire the **ValueAdds277** API to publish RFAI requests into rfai-requests - Extend error handling with dead-letter topics and alerting via App Insights - Add CI/CD (GitHub Actions/Azure DevOps) to deploy workflows & parameters Mine looks like this