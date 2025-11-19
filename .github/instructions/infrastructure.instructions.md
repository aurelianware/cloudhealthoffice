# Infrastructure (Bicep) Instructions

**Applies to**: `infra/*.bicep`, `attachments_logicapps_package/*.bicep`

## Overview

Infrastructure as Code (IaC) using Azure Bicep for deploying HIPAA-compliant Logic Apps infrastructure.

## Main Templates

### infra/main.bicep
Primary infrastructure template that deploys:
- **Azure Data Lake Storage Gen2**: Hierarchical namespace enabled for HIPAA attachments
- **Service Bus Namespace**: Standard tier with three topics
  - `attachments-in`: For processed 275 attachment events
  - `rfai-requests`: For 277 RFAI outbound requests  
  - `edi-278`: For 278 Health Care Services Review transactions
- **Logic App Standard**: WS1 SKU for workflow runtime
- **Application Insights**: Telemetry and monitoring

### attachments_logicapps_package/main.bicep
Alternative package-based deployment template (if used)

## Best Practices

### Bicep Conventions
- Use descriptive parameter names with clear validation
- Set default values where appropriate
- Use `@description` decorators for all parameters
- Follow Azure naming conventions: `{baseName}-{resource-type}`

### Resource Configuration

#### Storage Account
```bicep
- Enable hierarchical namespace (Data Lake Gen2)
- Use Standard_LRS or Standard_GRS for replication
- Enable encryption at rest
- Configure with managed identity access
```

#### Service Bus
```bicep
- Use Standard tier (supports topics)
- Create all three topics: attachments-in, rfai-requests, edi-278
- Configure max message size appropriately
- Enable dead-letter queues
```

#### Logic App Standard
```bicep
- Use WS1 SKU minimum
- Enable Application Insights integration
- Configure system-assigned managed identity
- Set WEBSITE_CONTENTAZUREFILECONNECTIONSTRING if needed
```

### Parameter Patterns
```bicep
@description('Base name for all resources')
@minLength(3)
@maxLength(20)
param baseName string

@description('Azure region for deployment')
@allowed(['eastus', 'westus', 'centralus'])
param location string = 'eastus'
```

### Expected Warnings
- **use-parent-property**: Service Bus topics may generate this warning - it's cosmetic and safe to ignore
- These warnings do not affect deployment or runtime

## Validation

### Local Validation
```bash
# Compile Bicep to ARM template
az bicep build --file infra/main.bicep --outfile /tmp/arm.json

# Validate deployment (without deploying)
az deployment group validate \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters baseName=test-name

# Preview changes (What-If)
az deployment group what-if \
  --resource-group <rg-name> \
  --template-file infra/main.bicep \
  --parameters baseName=prod-name \
  --no-pretty-print
```

### Automated Validation
The `pr-lint.yml` workflow automatically validates:
- Bicep syntax compilation
- Parameter validation
- Output generation

## Deployment

### Manual Deployment
```bash
# Create resource group
az group create -n payer-attachments-rg -l eastus

# Deploy infrastructure
az deployment group create \
  -g payer-attachments-rg \
  -f infra/main.bicep \
  -p baseName=payer-attachments
```

### Via GitHub Actions
Infrastructure deployment is handled by workflow files:
- Uses matrix strategy for multi-environment
- Runs ARM What-If before deployment
- Validates before deploying

## Common Scenarios

### Adding a New Resource
1. Add parameter with validation and description
2. Define resource in Bicep with proper naming
3. Add outputs if needed for downstream use
4. Test compilation locally
5. Run What-If in DEV first
6. Update copilot-instructions.md if it's a major component

### Modifying Service Bus Topics
1. Remember all three topics: `attachments-in`, `rfai-requests`, `edi-278`
2. Maintain consistent configuration across topics
3. Don't change existing topic names (breaks workflows)
4. Update Logic App workflows if adding new topics

### Changing Storage Configuration
1. Data Lake Gen2 requires hierarchical namespace
2. Preserve existing container names
3. Consider impact on existing data partitioning
4. Update workflows if blob paths change

## Troubleshooting

### Bicep Build Failures
- Check parameter types and constraints
- Verify resource API versions are current
- Look for missing required properties
- Use `az bicep build` for detailed errors

### Deployment Failures
- Review ARM What-If output first
- Check resource naming conflicts
- Verify quota limits in subscription
- Review Activity Log in Azure Portal

## Security Considerations

### HIPAA Compliance
- All storage must have encryption at rest
- Use managed identities instead of connection strings
- Enable diagnostic logging
- Follow least-privilege access principles

### Managed Identity
- Enable system-assigned managed identity on Logic App
- Grant specific RBAC roles (Storage Blob Data Contributor, Service Bus Data Sender)
- Avoid using storage account keys in production

### Network Security
- Consider VNET integration for production
- Use private endpoints for storage and service bus if required
- Configure firewall rules appropriately
