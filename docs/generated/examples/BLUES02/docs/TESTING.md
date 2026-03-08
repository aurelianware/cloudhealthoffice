# Testing Guide - Regional Blue Cross Blue Shield

## Testing Overview

This document provides testing instructions for Regional Blue Cross Blue Shield (BLUES02).

## Prerequisites

- Deployed infrastructure and workflows
- Test credentials configured in Key Vault
- Sample test data

## Testing Workflows


### ECS Summary Search

Test the ECS summary search workflow:

```bash
curl -X POST https://blues02-la.azurewebsites.net/api/ecs_summary_search/triggers/manual/invoke \
  -H "Content-Type: application/json" \
  -d '{
    "searchMethod": "ServiceDate",
    "requestId": "TEST-001",
    "serviceDateSearch": {
      "serviceFromDate": "2024-01-01",
      "serviceToDate": "2024-01-31",
      "providerId": "1234567890"
    }
  }'
```



### Appeals Processing

Test appeals workflow by:

1. Uploading test file to SFTP inbound folder
2. Monitor workflow execution in Azure Portal
3. Verify message published to Service Bus
4. Check Application Insights logs


## Test Data

Use the following test data:

- **Test Claim Number**: TEST-CLAIM-001
- **Test Member ID**: TEST-MEMBER-001
- **Test Provider NPI**: 1234567890

## Validation

After testing, verify:

- ✅ Workflows execute successfully
- ✅ Data is stored in correct containers
- ✅ Messages published to Service Bus
- ✅ Logs appear in Application Insights
- ✅ No errors in dead-letter queues

## Troubleshooting

Common issues:

1. **Authentication failures**: Check Key Vault secrets
2. **Timeout errors**: Increase timeout values in configuration
3. **SFTP connection issues**: Verify network connectivity and credentials

## Support

For testing issues, contact: edi-support@blues02.com
