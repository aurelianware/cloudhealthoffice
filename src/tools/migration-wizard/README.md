# Cloud Health Office - claims backend Migration Wizard

A Blazor web application for migrating members, providers, and benefit plans from core administrative system to Cloud Health Office's Cosmos DB.

## Features

- **TriZetto Open Access SOAP API Integration**: Connect to claims backend via TriZetto Open Access SOAP APIs
- **Data Export**: Export members, providers, and benefit plans to Cloud Health Office Cosmos DB
- **Mapping Report**: Generate comprehensive mapping reports with 95%+ auto-match capability
- **One-Click Cutover**: Flip API Management routing keys to switch traffic to Cloud Health Office
- **Azure Key Vault Integration**: Securely retrieve credentials from Azure Key Vault

## Prerequisites

- .NET 8.0 SDK or later
- Azure subscription with:
  - Cosmos DB account (using the Cloud Health Office database schema)
  - API Management instance (for traffic routing)
  - Azure Key Vault (for secure credential storage)
- Access to TriZetto Open Access SOAP APIs (claims backend)

## Configuration

### Azure Key Vault Setup (Recommended for Production)

1. Create an Azure Key Vault and add the following secrets:

   | Secret Name | Description |
   |-------------|-------------|
   | `TriZetto--Username` | TriZetto Open Access username |
   | `TriZetto--Password` | TriZetto Open Access password |
   | `CosmosDb--Key` | Cosmos DB primary access key |

2. Grant access to the Key Vault:
   - If running locally: Add your Azure AD user to the Key Vault Access Policy
   - If running in Azure: Enable Managed Identity and grant it "Key Vault Secrets User" role

3. Update `appsettings.json` with your Key Vault URI:

   ```json
   {
     "KeyVault": {
       "VaultUri": "https://your-keyvault.vault.azure.net/"
     }
   }
   ```

### Local Development Configuration

For local development without Key Vault, you can use `appsettings.Development.json` (not committed to source control):

```json
{
  "TriZetto": {
    "Username": "your-dev-username",
    "Password": "your-dev-password"
  },
  "CosmosDb": {
    "Key": "your-cosmos-primary-key"
  }
}
```

### Full Configuration Reference

```json
{
  "KeyVault": {
    "VaultUri": "https://your-keyvault.vault.azure.net/"
  },
  "TriZetto": {
    "EndpointUrl": "https://backend-server.example.com/OpenAccess/Services",
    "TenantId": "default-tenant",
    "TimeoutSeconds": 120,
    "BypassCertificateValidation": false
  },
  "CosmosDb": {
    "Endpoint": "https://your-cosmos-account.documents.azure.com:443/",
    "DatabaseName": "cloudhealthoffice",
    "MembersContainer": "Members",
    "ProvidersContainer": "ProviderDirectory",
    "BenefitPlansContainer": "BenefitPlans",
    "DefaultThroughput": 400
  },
  "ApiManagement": {
    "ServiceName": "your-apim-service-name",
    "ResourceGroup": "your-resource-group",
    "SubscriptionId": "your-azure-subscription-id",
    "RoutingKeyName": "backend-routing",
    "BackendSystemId": "backend-backend",
    "CloudHealthOfficeBackendId": "cloudhealthoffice-backend"
  }
}
```

> **Security Note**: In production, always use Azure Key Vault with Managed Identity for credential management.

## Running the Application

```bash
# Navigate to the migration wizard directory
cd tools/migration-wizard

# Build the application
dotnet build

# Run the application
dotnet run
```

The application will be available at `http://localhost:5000`.

## Migration Process

### 1. Configure Settings
Update `appsettings.json` with your TriZetto Open Access, Cosmos DB, and API Management credentials.

### 2. Start Migration
Click **"Start Migration"** to begin the export process:
- Members are exported from claims backend and written to the `Members` container
- Providers are exported and written to the `ProviderDirectory` container
- Benefit plans are exported and written to the `BenefitPlans` container

### 3. Review Mapping Report
After export completes, review the mapping report:
- **Auto-Matched (Exact/High)**: Records that mapped with 98%+ field match
- **Partial Match**: Records requiring review with 75-97% field match
- **No Match**: Records with <75% field match requiring manual intervention

Target: 95%+ auto-match rate before cutover.

### 4. Start Cutover
When ready, click **"Start Cutover"** to flip the API Management routing:
- Named value `backend-routing` is updated to point to Cloud Health Office
- Traffic is immediately routed to the new system

### 5. Monitor and Rollback
If issues occur after cutover:
- Click **"Rollback Cutover"** to revert traffic to claims backend
- Review errors and address issues before re-attempting cutover

## TriZetto Open Access SOAP APIs

The migration wizard connects to the following SOAP endpoints:

| Service | Endpoint | Description |
|---------|----------|-------------|
| Member Service | `/MemberService.svc` | Export member/subscriber data |
| Provider Service | `/ProviderService.svc` | Export provider directory |
| Benefit Service | `/BenefitService.svc` | Export benefit plan configurations |
| System Service | `/SystemService.svc` | Connection test and health checks |

### Sample SOAP Request

```xml
<?xml version="1.0" encoding="utf-8"?>
<soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" 
               xmlns:tns="http://trizetto.com/openaccess">
    <soap:Header>
        <tns:AuthenticationHeader>
            <tns:Username>your-username</tns:Username>
            <tns:Password>your-password</tns:Password>
            <tns:TenantId>default-tenant</tns:TenantId>
        </tns:AuthenticationHeader>
    </soap:Header>
    <soap:Body>
        <tns:GetMembers>
            <EffectiveDate>2024-01-01</EffectiveDate>
            <PageNumber>1</PageNumber>
            <PageSize>1000</PageSize>
        </tns:GetMembers>
    </soap:Body>
</soap:Envelope>
```

## Data Mapping

### Member Mapping

| claims backend Field | Cloud Health Office Field | Transformation |
|------------|---------------------------|----------------|
| MemberId | memberId | Direct |
| SubscriberId | subscriberId | Direct |
| FirstName | firstName | Direct |
| LastName | lastName | Direct |
| DateOfBirth | dateOfBirth | ISO 8601 format |
| Gender | gender | Normalized to M/F/U |
| PlanCode | planCode | Direct |
| GroupNumber | groupNumber | Direct |

### Provider Mapping

| claims backend Field | Cloud Health Office Field | Transformation |
|------------|---------------------------|----------------|
| ProviderId | providerId | Direct |
| Npi | npi | Luhn validated |
| TaxId | taxId | EIN format |
| TaxonomyCode | taxonomyCode | NUCC validated |
| ProviderType | providerType | Normalized |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                    Migration Wizard                              │
│                 (Blazor Web App)                                 │
└──────────────┬──────────────────────┬───────────────────────────┘
               │                      │
               ▼                      ▼
┌──────────────────────┐    ┌────────────────────────┐
│  TriZetto Open Access │    │    Cosmos DB           │
│    (claims backend SOAP API)    │    │  (Cloud Health Office) │
│                       │    │                        │
│  • Members            │───▶│  • Members             │
│  • Providers          │    │  • ProviderDirectory   │
│  • Benefit Plans      │    │  • BenefitPlans        │
└──────────────────────┘    └────────────────────────┘
                                      │
                                      ▼
                            ┌────────────────────────┐
                            │   API Management       │
                            │   (Routing Cutover)    │
                            │                        │
                            │  Named Value:          │
                            │  backend-routing       │
                            │  ↓                     │
                            │  backend-backend →        │
                            │  cloudhealthoffice-    │
                            │  backend               │
                            └────────────────────────┘
```

## Troubleshooting

### Connection Failures

1. Verify network connectivity to claims backend server
2. Check firewall rules allow HTTPS traffic
3. Validate credentials in appsettings.json
4. Ensure tenant ID matches your claims backend configuration

### Low Auto-Match Rate

1. Review field transformations in mapping report
2. Check for data quality issues in source system
3. Verify date formats and code normalizations
4. Contact support for custom mapping rules

### Cutover Failures

1. Ensure Azure credentials have API Management Contributor role
2. Verify named value exists in API Management
3. Check subscription ID and resource group are correct
4. Review Azure Activity Log for detailed error messages

## Security Considerations

- **Credentials**: Never commit credentials to source control. Use Azure Key Vault.
- **Network**: Deploy within Azure Virtual Network for private connectivity.
- **Logging**: PHI is not logged. Only record counts and metadata are captured.
- **Audit**: All cutover operations are logged to Application Insights.

## License

BSL 1.1 - See [LICENSE](../../LICENSE) for details.

---

**Cloud Health Office** – Advancing Healthcare EDI Integration
