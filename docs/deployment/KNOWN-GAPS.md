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

## Scope correction — October 24, 2026

The missing Dockerfile describes the legacy `ffs-service` shell, not absent
fee-for-service pricing. Its `FfsRateConfig` model is superseded by provider
contract reimbursement terms under [ADR 016](../adr/016-unified-contract-pricing.md).
Do not containerize a dead API/model as a California launch prerequisite.
See the current [capability ledger](../CAPABILITY_RECONCILIATION.md).
