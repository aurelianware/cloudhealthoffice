# ADR 004: Remove Azure Logic Apps

## Status

**Accepted**

## Context

Cloud Health Office originally used Azure Logic Apps (Standard) for EDI orchestration workflows including X12 275/278 ingestion, authorization requests, appeals processing, and claim status queries. These workflows were defined as JSON workflow definitions deployed to Azure Logic App Standard instances.

As the platform matured, limitations became apparent: Logic Apps JSON workflows are difficult to unit test, tightly coupled to Azure, and run on a separate runtime from the C# microservices that handle business logic.

## Decision

Migrate all orchestration from Azure Logic Apps to Argo Workflows backed by C# microservices. This provides:

- **Testability**: Argo workflow templates and C# services can be tested in CI without Azure dependencies
- **Portability**: Argo Workflows runs on any Kubernetes cluster, enabling multi-cloud and on-prem deployment
- **Unified runtime**: All business logic runs as C# microservices orchestrated by Argo, eliminating the split between Logic Apps JSON and application code

The `infrastructure/logicapps/` directory (21 JSON workflow definitions, connection configs, and Bicep templates) and `src/logicapps/` (TypeScript support code) have been deleted from the repository.

## Consequences

- All Logic Apps JSON workflow definitions have been removed from the repository
- All EDI orchestration is now handled exclusively by Argo Workflows (see `infrastructure/argo-workflows/`)
- The migration export script (`scripts/migration/export-logic-apps.ps1`) is retained for reference during the transition period
- Documentation and configuration files throughout the repo still contain references to Logic Apps that should be updated to reflect the new architecture
- Teams must use Argo Workflows for any new orchestration work
- Azure Logic App infrastructure in deployed environments should be decommissioned separately
