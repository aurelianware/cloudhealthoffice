# Known Build Gaps

> **Current-status annotation — 2026-10-19:** The FFS Dockerfile/workflow omission was rechecked and remains (CAP-20). This is a build inventory finding, not a statement about fee-schedule-engine capability. See [Capability Reconciliation](../CAPABILITY_RECONCILIATION.md) for implementation evidence, test scope and remaining limits. Historical statements below describe their original baseline unless explicitly updated.


This file documents services or components in the repository that
exist but are not yet included in CI build/deploy workflows. Canonical
build matrix lives in `.github/workflows/deploy-azure-aks.yml`.

## `ffs-service`

Service exists at `src/services/ffs-service/` (csproj, Program.cs,
Models/) but does not yet have a `Dockerfile`. Excluded from
`.github/workflows/deploy-azure-aks.yml` and
`.github/workflows/docker-build.yml` matrices. Will be added once
the service is containerized.
