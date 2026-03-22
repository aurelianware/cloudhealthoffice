# Archived Logic App Scripts

These scripts were used during the Azure Logic Apps era of Cloud Health Office,
before the migration to AKS with Argo Workflows. They are preserved here for
reference only and are **not used in the current architecture**.

## Migration Context

Cloud Health Office originally ran EDI processing workflows (275, 277, 278) on
Azure Logic Apps Standard with Azure Service Bus, Blob Storage, and Integration
Accounts. The platform was migrated to AKS with Argo Workflows, Kafka, and
containerized processing steps. These scripts supported the Logic App
infrastructure and the migration itself.

## Archived Scripts

| Script | Original Location | Purpose |
|--------|-------------------|---------|
| `export-logic-apps.ps1` | `scripts/migration/` | Exported Logic App workflow definitions and generated a migration comparison report |
| `parallel-run.sh` | `scripts/migration/` | Controlled traffic splitting between Logic Apps and Argo during the parallel-run migration phase |
| `bootstrap_repo.ps1` | `scripts/deploy/` | Scaffolded a new GitHub repo with Logic App workflow definitions and a deploy pipeline |
| `test-e2e.ps1` | `scripts/` | End-to-end test suite that validated Logic App existence, status, Service Bus topics, and workflows |
| `setup-integration-account.ps1` | `scripts/setup/` | Created an Azure Integration Account (Free tier) with trading partners for X12 processing |
| `setup-integration-account-complete.ps1` | `scripts/setup/` | Extended Integration Account setup including schemas and X12 agreements |

## See Also

- Current deployment: `scripts/deploy/deploy-core-services.sh`
- Current testing: `scripts/testing/`
- Argo workflow definitions: `argo/`
- Migration guide: `docs/ARGO-MIGRATION-GUIDE.md`
