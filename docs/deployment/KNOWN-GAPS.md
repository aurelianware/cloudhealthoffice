# Known Build Gaps

This file documents services or components in the repository that
exist but are not yet included in CI build/deploy workflows. Canonical
build matrix lives in `.github/workflows/deploy-azure-aks.yml`.

## `ffs-service`

Verified October 19, 2026; [CAP-20 evidence](../CAPABILITY_RECONCILIATION.md#traceable-capability-matrix). This build omission is distinct from fee-schedule-engine functionality.

Service exists at `src/services/ffs-service/` (csproj, Program.cs,
Models/) but does not yet have a `Dockerfile`. Excluded from
`.github/workflows/deploy-azure-aks.yml` and
`.github/workflows/docker-build.yml` matrices. Will be added once
the service is containerized.
